using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator.Controllers;

/// <summary>
/// A pass on a distributed Chroma (OPERATOR-DESIGN.md §7). In order, and each step stops the pass when it fails, with the reason in the
/// status:
/// <list type="number">
/// <item>a version the operator knows, images it allows, the only distributed Chroma of the namespace, no downgrade;</item>
/// <item>the Secrets, Postgres and the bucket, checked: without them nothing is created;</item>
/// <item>no object of the same name that belongs to someone else;</item>
/// <item>accounts, roles, MemberLists, ConfigMaps and Services; then the migration Job of the version, and its completion;</item>
/// <item>the workloads tier by tier, each after the one before is updated and ready: sysdb, log, the workers, the frontend last;</item>
/// <item>the two repairs of the trials: the work queue not ready for 180 s is recreated; MemberLists empty for 180 s while their
/// components are ready mean that the sysdb lost its leadership, and only a restart makes it run for it again.</item>
/// </list>
/// The objects are written with server-side apply, as the field manager chroma-operator. A MemberList is only created: the sysdb writes
/// its members. A Job is only created: its template does not change.
/// </summary>
internal sealed partial class DistributedReconciler(IKubernetesClient client, OperatorOptions options, IDependencyChecks checks, ILogger logger)
{
	public const string FieldManager = "chroma-operator";
	public static readonly TimeSpan WatchdogDelay = TimeSpan.FromSeconds(180);
	public static readonly TimeSpan BusyRequeue = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan SteadyRequeue = TimeSpan.FromSeconds(30);

	/// <summary>
	/// The generation of the spec whose objects were all applied, by UID of the Chroma. Until then every pass applies every object it
	/// reaches, so that a field removed from the spec is removed from the objects too; after, only the objects that differ.
	/// </summary>
	private static readonly ConcurrentDictionary<string, long> AppliedGeneration = new();

	public static void Forget(string uid) => AppliedGeneration.TryRemove(uid, out _);

	/// <summary>
	/// A MemberList without a controller: the sysdb writes the members with an update whose metadata has only the name, the namespace
	/// and the resource version (go/pkg/memberlist_manager/memberlist_store.go:159-182 of 52ce844), so its labels and its owner go at
	/// the first write. The other objects of the chart tell a distributed Chroma that is not this one.
	/// </summary>
	internal static bool StrippedBySysdb(IKubernetesObject<V1ObjectMeta> o) =>
		o is V1MemberList && o.Metadata.OwnerReferences?.Any(r => r.Controller == true) != true;

	/// <summary>
	/// At the deletion of the Chroma, its MemberLists: the garbage collector deletes the other objects with it, but the MemberLists have
	/// lost their owner (<see cref="StrippedBySysdb"/>). Those of another controller stay, and so do all of them when an older distributed
	/// Chroma is in the namespace: this one was refused, and the MemberLists are of the other.
	/// </summary>
	public async Task DeletedAsync(V1Alpha1Chroma chroma, CancellationToken cancellationToken)
	{
		if (await OlderDistributedAsync(chroma, cancellationToken) is not null)
			return;
		foreach (var memberList in await client.ListAsync<V1MemberList>(chroma.Namespace(), cancellationToken: cancellationToken))
		{
			if (DistributedResources.MemberLists.Any(m => m.MemberList == memberList.Name())
				&& (StrippedBySysdb(memberList) || Ownership.IsControlledBy(memberList.Metadata, chroma)))
				await client.DeleteAsync(memberList, cancellationToken);
		}
	}

	public async Task<ReconciliationResult<V1Alpha1Chroma>> PassAsync(V1Alpha1Chroma chroma, DateTime now, CancellationToken cancellationToken)
	{
		var name = chroma.Name();
		var ns = chroma.Namespace();
		var spec = chroma.Spec.Distributed!;
		var old = chroma.Status ?? new V1Alpha1Chroma.ChromaStatus();
		var status = new V1Alpha1Chroma.ChromaStatus
		{
			Phase = old.Phase,
			ObservedGeneration = chroma.Metadata.Generation,
			Endpoint = old.Endpoint,
			Version = old.Version,
			MigratedVersion = old.MigratedVersion,
			VersionHistory = old.VersionHistory?.ToList(),
			Components = old.Components?.ToList(),
			Conditions = old.Conditions.Select(Reconciliation.Copy).ToList(),
		};

		var version = DistributedVersion.Find(spec.Version);
		if (version is null)
			return await FailAsync(chroma, status, "VersionNotSupported",
				$"The operator does not run version {spec.Version} of distributed Chroma. It runs {string.Join(", ", DistributedVersion.Known.Select(v => $"{v.Tag} (chart {v.Chart})"))}: each with the objects of its chart and the config files of its commit.", now, cancellationToken);
		var refused = DistributedResources.Images.Keys.Select(c => DistributedResources.Image(chroma, version, c)).Where(i => !options.IsAllowed(i)).ToList();
		if (refused.Count > 0)
			return await FailAsync(chroma, status, "ImageNotAllowed",
				$"The images {string.Join(", ", refused)} are not in {OperatorOptions.AllowedImagesVariable} of the operator.", now, cancellationToken);
		if (await OlderDistributedAsync(chroma, cancellationToken) is { } owner)
			return await FailAsync(chroma, status, "DistributedNamespaceTaken",
				$"The namespace {ns} has the distributed Chroma {owner} already: the objects of the chart have fixed names, so there is one distributed Chroma per namespace.", now, cancellationToken);

		// A version migrated before the last one: the Postgres of the sysdb may hold migrations of a newer one. Nothing is written, and
		// the status keeps showing the components as they run.
		var history = status.VersionHistory ?? [];
		var allowDowngrade = chroma.Metadata.Annotations?.TryGetValue(Labels.AllowDowngrade, out var allow) == true && allow == "true";
		var downgrade = history.Count > 0 && history.Contains(version.Tag) && history[^1] != version.Tag;
		if (downgrade && !allowDowngrade)
		{
			Reconciliation.SetCondition(status, "UpgradeBlocked", true, "Downgrade",
				$"Version {version.Tag} was migrated before {history[^1]}, the last one: going back is a downgrade, and the Postgres of the sysdb may hold the migrations of {history[^1]}. To run {version.Tag} anyway, annotate the Chroma with {Labels.AllowDowngrade}: \"true\".", now);
			var existing = await ExistingAsync(ns, cancellationToken);
			var running = DistributedResources.Tiers.SelectMany(t => t).All(c => RolledOut(existing.Workload(c), DistributedResources.Replicas(chroma, c)));
			Observe(chroma, status, existing, running ? status.Version : null, rolledOut: running, now);
			return await WriteStatusAsync(chroma, status, SteadyRequeue, cancellationToken);
		}
		Reconciliation.SetCondition(status, "UpgradeBlocked", false, "VersionAllowed", "", now);

		// The checks run from the operator: before the migration of the version they stop the pass; after it, a failure is in the
		// status and the pass goes on, since the components reach Postgres and the bucket on their own, and the repairs must run.
		var dependencies = await DependenciesReadyAsync(chroma, status, now, cancellationToken);
		var current = await ExistingAsync(ns, cancellationToken);
		var jobName = DistributedResources.MigrationJobName(version);
		if (!dependencies && !current.Jobs.ContainsKey(jobName))
		{
			status.Phase = "Failed";
			var failed = status.Conditions.First(c => c.Type is "PostgresReady" or "ObjectStorageReady" && c.Status == "False");
			Reconciliation.SetCondition(status, "Ready", false, failed.Reason, failed.Message, now);
			return await WriteStatusAsync(chroma, status, SteadyRequeue, cancellationToken);
		}

		var desired = DistributedResources.All(chroma, version);
		foreach (var o in desired)
		{
			if (current.Get(o) is { } found && !StrippedBySysdb(found) && Ownership.Conflict(o.Kind, found.Metadata, chroma) is { } conflict)
				return await FailAsync(chroma, status, "NameConflict", conflict, now, cancellationToken);
		}

		var full = !AppliedGeneration.TryGetValue(chroma.Uid(), out var applied) || applied != chroma.Metadata.Generation;
		foreach (var o in DistributedResources.Foundation(chroma, version))
		{
			if (await ApplyAsync(chroma, o, current, full, cancellationToken) is { } refusal)
				return await FailAsync(chroma, status, refusal.Reason, refusal.Message, now, cancellationToken);
		}

		// The migrations of the version, before any component of it.
		var job = current.Jobs.GetValueOrDefault(jobName);
		if (job is null)
		{
			Log.CreatingMigrationJob(logger, name, jobName);
			if (await ApplyAsync(chroma, DistributedResources.MigrationJob(chroma, version), current, full, cancellationToken) is { } refusal)
				return await FailAsync(chroma, status, refusal.Reason, refusal.Message, now, cancellationToken);
			job = current.Jobs.GetValueOrDefault(jobName);
		}
		if (JobCondition(job, "Failed") is { } jobFailed)
		{
			Reconciliation.SetCondition(status, "Migrated", false, "JobFailed", $"The Job {jobName} failed: {jobFailed.Message}. Its logs say why: kubectl logs -n {ns} job/{jobName}. Once the cause is fixed, delete the Job: the operator creates it again.", now);
			return await FailAsync(chroma, status, "MigrationFailed", $"The migration Job {jobName} failed; no component of {version.Tag} starts.", now, cancellationToken);
		}
		if (JobCondition(job, "Complete") is null)
		{
			Reconciliation.SetCondition(status, "Migrated", false, "JobRunning", $"The Job {jobName} migrates the Postgres of the sysdb to {version.Tag}.", now);
			status.Phase = "Migrating";
			Reconciliation.SetCondition(status, "Ready", false, "Migrating", $"The Job {jobName} is running.", now);
			Observe(chroma, status, current, null, rolledOut: false, now, phase: false);
			return await WriteStatusAsync(chroma, status, BusyRequeue, cancellationToken);
		}
		Reconciliation.SetCondition(status, "Migrated", true, "JobComplete", $"The Job {jobName} migrated the Postgres of the sysdb to {version.Tag}.", now);
		status.MigratedVersion = version.Tag;
		if (history.Count == 0 || history[^1] != version.Tag)
			status.VersionHistory = [.. history.Where(v => v != version.Tag), version.Tag];
		// The Jobs of the versions before, as Helm deletes them at an upgrade.
		foreach (var stale in current.Jobs.Values.Where(j => j.Name() != jobName && j.Name().StartsWith("sysdb-migration-", StringComparison.Ordinal) && Ownership.IsControlledBy(j.Metadata, chroma)).ToList())
		{
			// With its Pods: the API orphans them by default for a Job. One deleted meanwhile is gone already.
			try
			{
				await client.ApiClient.BatchV1.DeleteNamespacedJobAsync(stale.Name(), ns, propagationPolicy: "Background", cancellationToken: cancellationToken);
			}
			catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
			}
		}

		// The workloads, tier by tier.
		var rolledOut = true;
		foreach (var tier in DistributedResources.Tiers)
		{
			foreach (var component in tier)
			{
				if (await ApplyAsync(chroma, DistributedResources.Workload(chroma, version, component), current, full, cancellationToken) is { } refusal)
					return await FailAsync(chroma, status, refusal.Reason, refusal.Message, now, cancellationToken);
			}
			if (!tier.All(c => RolledOut(current.Workload(c), DistributedResources.Replicas(chroma, c))))
			{
				rolledOut = false;
				break;
			}
		}
		if (rolledOut)
			AppliedGeneration[chroma.Uid()] = chroma.Metadata.Generation ?? 0;

		Observe(chroma, status, current, version.Tag, rolledOut, now);
		await RepairAsync(chroma, status, current, now, cancellationToken);
		return await WriteStatusAsync(chroma, status, status.Phase == "Ready" ? SteadyRequeue : BusyRequeue, cancellationToken);
	}

	/// <summary>The components in the status, the condition MembersReady, and the phase with the condition Ready.</summary>
	/// <param name="version">The version that runs when every workload is rolled out: the one of the spec, or the one before a refused downgrade.</param>
	private static void Observe(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, Existing current, string? version, bool rolledOut, DateTime now, bool phase = true)
	{
		var components = new List<V1Alpha1Chroma.ComponentStatus>();
		foreach (var component in DistributedResources.Tiers.SelectMany(t => t))
		{
			var workload = current.Workload(component);
			var memberList = DistributedResources.MemberLists.FirstOrDefault(m => m.Component == component).MemberList;
			components.Add(new V1Alpha1Chroma.ComponentStatus
			{
				Name = component,
				Replicas = DistributedResources.Replicas(chroma, component),
				Ready = workload switch
				{
					V1Deployment d => d.Status?.ReadyReplicas ?? 0,
					V1StatefulSet s => s.Status?.ReadyReplicas ?? 0,
					_ => 0,
				},
				Members = memberList is null ? null : current.MemberLists.GetValueOrDefault(memberList)?.Spec?.Members?.Count ?? 0,
			});
		}
		status.Components = components;

		var unlisted = components.Where(c => c.Members == 0 && c.Ready > 0).Select(c => c.Name).ToList();
		if (unlisted.Count == 0)
			Reconciliation.SetCondition(status, "MembersReady", true, "AllListed", "The MemberLists list the ready Pods of their components.", now);
		else
			Reconciliation.SetCondition(status, "MembersReady", false, "MembersMissing", $"Ready Pods of {string.Join(", ", unlisted)} are not in their MemberLists: the sysdb leader lists them.", now);

		if (!phase)
			return;
		var notReady = components.Where(c => c.Ready < c.Replicas).Select(c => $"{c.Name} {c.Ready}/{c.Replicas}").ToList();
		if (rolledOut && version is not null && notReady.Count == 0 && unlisted.Count == 0)
		{
			status.Phase = "Ready";
			status.Version = version;
			status.Endpoint = DistributedResources.Endpoint(chroma);
			Reconciliation.SetCondition(status, "Ready", true, "AllComponentsReady", $"Chroma {version} answers on {status.Endpoint}.", now);
			return;
		}
		status.Phase = status.Version is null ? "Creating" : "Updating";
		var why = notReady.Count > 0 ? $"Not ready: {string.Join(", ", notReady)}." : unlisted.Count > 0 ? "The MemberLists are not complete." : "The components are being updated, tier by tier.";
		Reconciliation.SetCondition(status, "Ready", false, notReady.Count > 0 ? "ComponentsNotReady" : "RollingOut", why, now);
	}

	/// <summary>
	/// The two watchdogs of the trials, as actions of the operator, with the reason in the condition Repaired:
	/// <list type="bullet">
	/// <item>the work queue panics if it starts before the storage and stays NOT_SERVING: its Pod, running and not ready for 180 s, is
	/// deleted (WorkQueueRestarted);</item>
	/// <item>the sysdb runs for leader once: if it loses the Lease the MemberLists stay empty. MembersReady False for 180 s, with the sysdb
	/// ready for as long: its Pod is deleted (SysdbLeaderLost).</item>
	/// </list>
	/// </summary>
	private async Task RepairAsync(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, Existing current, DateTime now, CancellationToken cancellationToken)
	{
		var ns = chroma.Namespace();
		var pods = await client.ListAsync<V1Pod>(ns, cancellationToken: cancellationToken);
		var workQueue = pods.FirstOrDefault(p => p.GetLabel("app") == DistributedResources.WorkQueue && p.Metadata.DeletionTimestamp is null
			&& Owned(p, current.Workload(DistributedResources.WorkQueue)));
		if (workQueue is not null && workQueue.Status?.Phase == "Running" && NotReadySince(workQueue) is { } since && now - since >= WatchdogDelay)
		{
			await DeletePodAsync(chroma, status, workQueue, "WorkQueueRestarted",
				$"The work queue was not ready for {(now - since).TotalSeconds:0} s: it stays NOT_SERVING when it starts before the storage. Pod {workQueue.Name()} deleted at {now:O}.", now, cancellationToken);
		}
		var members = status.Conditions.FirstOrDefault(c => c.Type == "MembersReady");
		// A Pod of the Deployment sysdb, through one of its ReplicaSets, sysdb-<hash>: not another Pod with the same label.
		var sysdb = pods.FirstOrDefault(p => p.GetLabel("app") == DistributedResources.Sysdb && p.Metadata.DeletionTimestamp is null && Reconciliation.IsReady(p)
			&& p.Metadata.OwnerReferences?.Any(o => o.Controller == true && o.Kind == "ReplicaSet" && o.Name.StartsWith(DistributedResources.Sysdb + "-", StringComparison.Ordinal)) == true);
		if (members is { Status: "False", LastTransitionTime: { } emptySince } && now - emptySince >= WatchdogDelay
			&& sysdb is not null && ReadySince(sysdb) is { } readySince && now - readySince >= WatchdogDelay)
		{
			await DeletePodAsync(chroma, status, sysdb, "SysdbLeaderLost",
				$"The MemberLists missed ready Pods for {(now - emptySince).TotalSeconds:0} s with the sysdb ready: it runs for leader only once, at its start. Pod {sysdb.Name()} deleted at {now:O}.", now, cancellationToken);
		}
	}

	private async Task DeletePodAsync(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, V1Pod pod, string reason, string message, DateTime now, CancellationToken cancellationToken)
	{
		var (server, deleted) = (chroma.Name(), pod.Name());
		Log.Repairing(logger, server, deleted, reason, message);
		await client.ApiClient.CoreV1.DeleteNamespacedPodAsync(pod.Name(), pod.Namespace(),
			new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = pod.Uid(), ResourceVersion = pod.ResourceVersion() } },
			cancellationToken: cancellationToken);
		Reconciliation.SetCondition(status, "Repaired", true, reason, message, now);
	}

	private static bool Owned(V1Pod pod, IKubernetesObject<V1ObjectMeta>? workload) =>
		workload is not null && pod.Metadata.OwnerReferences?.Any(o => o.Controller == true && o.Uid == workload.Uid()) == true;

	private static DateTime? NotReadySince(V1Pod pod) =>
		pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready") is { Status: "False", LastTransitionTime: { } t } ? t : null;

	private static DateTime? ReadySince(V1Pod pod) =>
		pod.Status?.Conditions?.FirstOrDefault(c => c.Type == "Ready") is { Status: "True", LastTransitionTime: { } t } ? t : null;

	/// <summary>Updated and ready: the controller has seen the last spec, and every replica runs it and is ready.</summary>
	internal static bool RolledOut(IKubernetesObject<V1ObjectMeta>? workload, int replicas) => workload switch
	{
		V1Deployment d => d.Status is { } s && s.ObservedGeneration >= d.Metadata.Generation
			&& (s.UpdatedReplicas ?? 0) == replicas && (s.ReadyReplicas ?? 0) == replicas && (s.Replicas ?? 0) == replicas,
		V1StatefulSet s => s.Status is { } st && st.ObservedGeneration >= s.Metadata.Generation
			&& (st.UpdatedReplicas ?? 0) == replicas && (st.ReadyReplicas ?? 0) == replicas && st.CurrentRevision == st.UpdateRevision,
		_ => false,
	};

	private static V1JobCondition? JobCondition(V1Job? job, string type) =>
		job?.Status?.Conditions?.FirstOrDefault(c => c.Type == type && c.Status == "True");

	/// <summary>
	/// The Secrets, then Postgres and the bucket. The credentials of Postgres go unquoted into the shell command of the sysdb and into the
	/// URL of the migration Job, as the chart writes them: only characters safe in both are accepted.
	/// </summary>
	private async Task<bool> DependenciesReadyAsync(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, DateTime now, CancellationToken cancellationToken)
	{
		var spec = chroma.Spec.Distributed!;
		var ns = chroma.Namespace();
		var (postgres, postgresError) = await SecretValuesAsync(ns, spec.Postgres.CredentialsSecret.Name, [DistributedResources.UsernameKey, DistributedResources.PasswordKey], cancellationToken);
		var postgresCheck = postgresError
			?? (postgres!.Values.All(v => SafeCredential().IsMatch(v)) ? null
				: new CheckResult(false, "CredentialsNotSupported", $"The username or the password in the Secret {spec.Postgres.CredentialsSecret.Name} has characters other than letters, digits and . _ ~ -: the chart passes them unquoted to a shell and into a URL."))
			?? await checks.PostgresAsync(new PostgresTarget(ns, spec.Postgres.Host, DistributedResources.PostgresPort(chroma), DistributedResources.Database(chroma),
				DistributedResources.SslMode(chroma), postgres[DistributedResources.UsernameKey], postgres[DistributedResources.PasswordKey]), cancellationToken);
		Reconciliation.SetCondition(status, "PostgresReady", postgresCheck.Ok, postgresCheck.Reason, postgresCheck.Message, now);

		var (storage, storageError) = await SecretValuesAsync(ns, spec.ObjectStorage.S3.CredentialsSecret.Name, [DistributedResources.AccessKeyIdKey, DistributedResources.SecretAccessKeyKey], cancellationToken);
		var storageCheck = storageError
			?? await checks.BucketAsync(new BucketTarget(ns, DistributedResources.StorageEndpoint(chroma), DistributedResources.Bucket(chroma), DistributedResources.Region(chroma),
				storage![DistributedResources.AccessKeyIdKey], storage[DistributedResources.SecretAccessKeyKey]), cancellationToken);
		Reconciliation.SetCondition(status, "ObjectStorageReady", storageCheck.Ok, storageCheck.Reason, storageCheck.Message, now);
		return postgresCheck.Ok && storageCheck.Ok;
	}

	[GeneratedRegex("^[A-Za-z0-9._~-]+$")]
	private static partial Regex SafeCredential();

	private async Task<(Dictionary<string, string>? Values, CheckResult? Error)> SecretValuesAsync(string ns, string name, string[] keys, CancellationToken cancellationToken)
	{
		var secret = await client.GetAsync<V1Secret>(name, ns, cancellationToken);
		if (secret is null)
			return (null, new CheckResult(false, "SecretNotFound", $"There is no Secret {name} in {ns}: it needs the keys {string.Join(" and ", keys)}."));
		var values = new Dictionary<string, string>();
		foreach (var key in keys)
		{
			if (secret.Data?.TryGetValue(key, out var bytes) != true || bytes is not { Length: > 0 })
				return (null, new CheckResult(false, "KeyMissing", $"The Secret {name} has no key {key}, or it is empty: it needs {string.Join(" and ", keys)}."));
			values[key] = Encoding.UTF8.GetString(bytes);
		}
		return (values, null);
	}

	/// <summary>The oldest other distributed Chroma of the namespace, if it is older than this one.</summary>
	private async Task<string?> OlderDistributedAsync(V1Alpha1Chroma chroma, CancellationToken cancellationToken)
	{
		var all = await client.ListAsync<V1Alpha1Chroma>(chroma.Namespace(), cancellationToken: cancellationToken);
		return all
			.Where(c => c.Uid() != chroma.Uid() && c.Spec.Mode == V1Alpha1Chroma.ChromaSpec.DistributedMode && c.Metadata.DeletionTimestamp is null)
			.Where(c => Nullable.Compare(c.Metadata.CreationTimestamp, chroma.Metadata.CreationTimestamp) is var t && (t < 0 || (t == 0 && string.CompareOrdinal(c.Name(), chroma.Name()) < 0)))
			.OrderBy(c => c.Metadata.CreationTimestamp).ThenBy(c => c.Name(), StringComparer.Ordinal)
			.Select(c => c.Name()).FirstOrDefault();
	}

	/// <summary>
	/// Writes an object with server-side apply, when it is missing, when the spec changed since all the objects were applied, or when it
	/// differs from what the operator wants. A MemberList or a Job that exists is left as it is. A write the API refuses is returned.
	/// </summary>
	private async Task<CheckResult?> ApplyAsync(V1Alpha1Chroma chroma, IKubernetesObject<V1ObjectMeta> desired, Existing current, bool full, CancellationToken cancellationToken)
	{
		var found = current.Get(desired);
		if (found is not null && (desired is V1MemberList or V1Job || (!full && Covers(JsonNode.Parse(KubernetesJson.Serialize(found)), Wanted(desired)))))
			return null;
		try
		{
			IKubernetesObject<V1ObjectMeta> written = desired switch
			{
				V1MemberList m => await client.CreateAsync(m, cancellationToken),
				V1ServiceAccount o => await ServerSideApplyAsync(o, cancellationToken),
				V1Role o => await ServerSideApplyAsync(o, cancellationToken),
				V1RoleBinding o => await ServerSideApplyAsync(o, cancellationToken),
				V1ConfigMap o => await ServerSideApplyAsync(o, cancellationToken),
				V1Service o => await ServerSideApplyAsync(o, cancellationToken),
				V1Job o => await ServerSideApplyAsync(o, cancellationToken),
				V1Deployment o => await ServerSideApplyAsync(o, cancellationToken),
				V1StatefulSet o => await ServerSideApplyAsync(o, cancellationToken),
				_ => throw new ArgumentOutOfRangeException(nameof(desired), desired.Kind, "Not an object of distributed Chroma."),
			};
			current.Set(written);
			if (found is null)
			{
				var (server, kind, created) = (chroma.Name(), desired.Kind, desired.Name());
				Log.Created(logger, server, kind, created);
			}
			return null;
		}
		catch (HttpOperationException e) when (ApiWrites.IsRefusal(e))
		{
			return new CheckResult(false, desired.Kind + "Refused", $"The API refused the {desired.Kind} {desired.Name()}: {e.Response.Content}");
		}
	}

	private Task<T> ServerSideApplyAsync<T>(T desired, CancellationToken cancellationToken) where T : IKubernetesObject<V1ObjectMeta> =>
		client.PatchAsync<T>(new V1Patch(desired, V1Patch.PatchType.ApplyPatch), desired.Name(), desired.Namespace(), FieldManager, true, null, cancellationToken);

	/// <summary>The fields of an object the operator wants, without apiVersion and kind: listed objects do not have them.</summary>
	private static JsonObject Wanted(IKubernetesObject<V1ObjectMeta> desired)
	{
		var wanted = JsonNode.Parse(KubernetesJson.Serialize(desired))!.AsObject();
		wanted.Remove("apiVersion");
		wanted.Remove("kind");
		return wanted;
	}

	/// <summary>Whether every field the operator wants is in the object as it is: fields the API or others added do not count.</summary>
	internal static bool Covers(JsonNode? current, JsonNode? desired) => (current, desired) switch
	{
		(_, null) => true,
		(JsonObject c, JsonObject d) => d.All(p => p.Value is null || Covers(c[p.Key], p.Value)),
		(JsonArray c, JsonArray d) => c.Count == d.Count && c.Zip(d).All(p => Covers(p.First, p.Second)),
		(null, _) => false,
		_ => current.ToString() == desired.ToString(),
	};

	private async Task<Existing> ExistingAsync(string ns, CancellationToken cancellationToken)
	{
		var lists = new Task<IEnumerable<IKubernetesObject<V1ObjectMeta>>>[]
		{
			ListAsync<V1ServiceAccount>(ns, cancellationToken), ListAsync<V1Role>(ns, cancellationToken), ListAsync<V1RoleBinding>(ns, cancellationToken),
			ListAsync<V1MemberList>(ns, cancellationToken), ListAsync<V1ConfigMap>(ns, cancellationToken), ListAsync<V1Service>(ns, cancellationToken),
			ListAsync<V1Job>(ns, cancellationToken), ListAsync<V1Deployment>(ns, cancellationToken), ListAsync<V1StatefulSet>(ns, cancellationToken),
		};
		var existing = new Existing();
		foreach (var o in (await Task.WhenAll(lists)).SelectMany(l => l))
			existing.Set(o);
		return existing;
	}

	private async Task<IEnumerable<IKubernetesObject<V1ObjectMeta>>> ListAsync<T>(string ns, CancellationToken cancellationToken) where T : IKubernetesObject<V1ObjectMeta> =>
		(await client.ListAsync<T>(ns, cancellationToken: cancellationToken)).Cast<IKubernetesObject<V1ObjectMeta>>();

	/// <summary>The objects of the namespace, by kind and name, as listed at the start of the pass and as the pass wrote them.</summary>
	private sealed class Existing
	{
		private readonly Dictionary<(string Kind, string Name), IKubernetesObject<V1ObjectMeta>> _objects = [];

		public Dictionary<string, V1Job> Jobs { get; } = [];

		public Dictionary<string, V1MemberList> MemberLists { get; } = [];

		public IKubernetesObject<V1ObjectMeta>? Get(IKubernetesObject<V1ObjectMeta> o) => _objects.GetValueOrDefault((KindOf(o), o.Name()));

		public IKubernetesObject<V1ObjectMeta>? Workload(string component) =>
			_objects.GetValueOrDefault(("Deployment", component)) ?? _objects.GetValueOrDefault(("StatefulSet", component));

		public void Set(IKubernetesObject<V1ObjectMeta> o)
		{
			_objects[(KindOf(o), o.Name())] = o;
			if (o is V1Job job)
				Jobs[job.Name()] = job;
			if (o is V1MemberList memberList)
				MemberLists[memberList.Name()] = memberList;
		}

		// Listed objects have no kind: the type says it.
		private static string KindOf(IKubernetesObject<V1ObjectMeta> o) => o switch
		{
			V1ServiceAccount => "ServiceAccount",
			V1Role => "Role",
			V1RoleBinding => "RoleBinding",
			V1MemberList => "MemberList",
			V1ConfigMap => "ConfigMap",
			V1Service => "Service",
			V1Job => "Job",
			V1Deployment => "Deployment",
			V1StatefulSet => "StatefulSet",
			_ => o.Kind,
		};
	}

	private async Task<ReconciliationResult<V1Alpha1Chroma>> FailAsync(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, string reason, string message, DateTime now, CancellationToken cancellationToken)
	{
		status.Phase = "Failed";
		Reconciliation.SetCondition(status, "Ready", false, reason, message, now);
		return await WriteStatusAsync(chroma, status, SteadyRequeue, cancellationToken);
	}

	private async Task<ReconciliationResult<V1Alpha1Chroma>> WriteStatusAsync(V1Alpha1Chroma chroma, V1Alpha1Chroma.ChromaStatus status, TimeSpan requeue, CancellationToken cancellationToken)
	{
		if (chroma.Status is null || KubernetesJson.Serialize(chroma.Status) != KubernetesJson.Serialize(status))
		{
			chroma.Status = status;
			chroma = await client.UpdateStatusAsync(chroma, cancellationToken);
		}
		return ReconciliationResult<V1Alpha1Chroma>.Success(chroma, requeue);
	}
}
