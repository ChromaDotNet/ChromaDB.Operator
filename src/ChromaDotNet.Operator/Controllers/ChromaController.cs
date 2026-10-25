using System.Collections.Concurrent;
using System.Net;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator.Controllers;

/// <summary>
/// Reconciles a Chroma: the ConfigMap, the Service, the PersistentVolumeClaim and the Pod. The watcher of KubeOps reacts to
/// changes of the spec only, so the controller looks again on its own: every 2 seconds while the server is not ready, every 30
/// seconds after, and at once when one of its Pods changes (<see cref="PodWatcher"/>). The Pod and the PersistentVolumeClaim are
/// managed directly, as CloudNativePG does, without a StatefulSet. Objects with the same names that belong to someone else are never
/// changed: the server fails with the reason (<see cref="Ownership"/>).
/// </summary>
// Only what the operator does. On the Chroma resources: the watch, the default image and the finalizer (update), and the status. The
// owner references of its objects block the deletion of the server (blockOwnerDeletion): with OwnerReferencesPermissionEnforcement,
// as on OpenShift, setting them needs update on chromas/finalizers. On the core types GenericRbac, since EntityRbac would add
// their status too: the Pods are watched, recreated and deleted, the other objects read, created and updated.
[EntityRbac(typeof(V1Alpha1Chroma), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Update)]
[GenericRbac(Groups = [Labels.Group], Resources = ["chromas/finalizers"], Verbs = RbacVerb.Update)]
[GenericRbac(Groups = [""], Resources = ["pods"],
	Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [""], Resources = ["persistentvolumeclaims"], Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
// Distributed: the objects of the chart, listed to tell those of others, and written with server-side apply (patch). The Secrets of
// Postgres and of the bucket are read for the checks before the start. Kubernetes lets a Role grant only what its writer holds: the
// operator holds what the Roles of the chart grant, on the MemberLists, the Pods and the Leases.
[GenericRbac(Groups = [""], Resources = ["configmaps", "services"], Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch)]
[GenericRbac(Groups = [""], Resources = ["serviceaccounts"], Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Patch)]
[GenericRbac(Groups = [""], Resources = ["secrets"], Verbs = RbacVerb.Get)]
[GenericRbac(Groups = ["rbac.authorization.k8s.io"], Resources = ["roles", "rolebindings"], Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Patch)]
[GenericRbac(Groups = ["apps"], Resources = ["deployments", "statefulsets"], Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Patch)]
[GenericRbac(Groups = ["batch"], Resources = ["jobs"], Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Create | RbacVerb.Patch | RbacVerb.Delete)]
[GenericRbac(Groups = ["chroma.cluster"], Resources = ["memberlists"],
	Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Create | RbacVerb.Update | RbacVerb.Patch | RbacVerb.Delete)]
[GenericRbac(Groups = ["coordination.k8s.io"], Resources = ["leases"],
	Verbs = RbacVerb.Get | RbacVerb.Watch | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
public sealed class ChromaController(IKubernetesClient client, OperatorOptions options, ILogger<ChromaController> logger, IDependencyChecks? checks = null)
	: IEntityController<V1Alpha1Chroma>
{
	private readonly DistributedReconciler _distributed = new(client, options, checks ?? new DependencyChecks(), logger);

	public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
	/// <summary>The resync puts every Chroma in the queue for a pass within a minute: a later retry would not hold.</summary>
	public static readonly TimeSpan LastRetry = Resync.Period;

	/// <summary>The passes that failed in a row, by UID of the server: the delay before the next one doubles with each.</summary>
	private static readonly ConcurrentDictionary<string, int> Failures = new();

	/// <summary>
	/// A pass. One that fails for another reason than the server, like an answer 500 of the API or no answer at all, does not throw: an
	/// exception would be retried by KubeOps a few times, then dropped, and the server would not be reconciled until its next change. It
	/// comes back after 10 seconds, then 20, 40, then every minute, and the condition Reconciled says why, if the API takes the status.
	/// </summary>
	public async Task<ReconciliationResult<V1Alpha1Chroma>> ReconcileAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		try
		{
			var result = await PassAsync(server, cancellationToken);
			Failures.TryRemove(server.Uid(), out _);
			// A pass that failed set Reconciled to False; this one went through, Pod included.
			if (result.Entity.Status?.Conditions.FirstOrDefault(c => c.Type == "Reconciled") is { Status: "False" })
			{
				Reconciliation.SetCondition(result.Entity.Status, "Reconciled", true, "Succeeded", "", DateTime.UtcNow);
				return ReconciliationResult<V1Alpha1Chroma>.Success(await client.UpdateStatusAsync(result.Entity, cancellationToken), result.RequeueAfter);
			}
			return result;
		}
		catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return await FailedAsync(server, e, cancellationToken);
		}
	}

	private async Task<ReconciliationResult<V1Alpha1Chroma>> FailedAsync(V1Alpha1Chroma server, Exception e, CancellationToken cancellationToken)
	{
		var name = server.Name();
		var failures = Failures.AddOrUpdate(server.Uid(), 1, (_, n) => n + 1);
		var delay = TimeSpan.FromTicks(Math.Min(LastRetry.Ticks, FirstRetry.Ticks << Math.Min(failures - 1, 16)));
		var (reason, message) = e is HttpOperationException http
			? ("ApiError", $"The API answered {(int)http.Response.StatusCode} {http.Response.StatusCode}: {http.Response.Content}")
			: ("Error", $"{e.GetType().Name}: {e.Message}");
		var seconds = delay.TotalSeconds;
		Log.PassFailed(logger, e, name, reason, seconds, message);
		try
		{
			// The pass may have written the server: its copy here is out of date.
			var current = await client.GetAsync<V1Alpha1Chroma>(name, server.Namespace(), cancellationToken) ?? server;
			var status = current.Status ?? new V1Alpha1Chroma.ChromaStatus();
			Reconciliation.SetCondition(status, "Reconciled", false, reason, message, DateTime.UtcNow);
			current.Status = status;
			await client.UpdateStatusAsync(current, cancellationToken);
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			// The status could not be written either, as when the API does not answer: the log has the reason.
		}
		return ReconciliationResult<V1Alpha1Chroma>.Failure(server, message, e, delay);
	}

	private async Task<ReconciliationResult<V1Alpha1Chroma>> PassAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var name = server.Name();
		try
		{
			// The distributed mode has its own pass: the objects of the chart, not a Pod and its claim.
			if (server.Spec.Mode == V1Alpha1Chroma.ChromaSpec.DistributedMode)
				return await _distributed.PassAsync(server, DateTime.UtcNow, cancellationToken);
			// Without an image, the default of the operator goes into the spec: the version is then written down, and a new version of the
			// operator with another default does not change it. The pass goes on with the updated server, so that what a Pod shows is
			// recorded in this pass too.
			if (string.IsNullOrEmpty(server.Spec.Image))
			{
				server.Spec.Image = options.DefaultImage;
				server = await client.UpdateAsync(server, cancellationToken);
			}
			// A name that gives invalid names to the objects creates nothing: Decide says why, with what a Pod of the server shows.
			if (Resources.NameError(name) is not null)
			{
				var existing = await OwnedPodAsync(server, cancellationToken);
				return await WriteStatusAsync(server, Reconciliation.Decide(server, existing, null, DateTime.UtcNow, options), cancellationToken);
			}

			// The owner of an existing claim of this server follows pvcDeletionPolicy first, before any other write that could fail or
			// conflict: otherwise a change to Retain would not be applied, and deleting the server would delete the data. The finalizer of
			// the claim is on the server while the server owns the claim: added before the owner reference, removed after it, so that a
			// change to Retain followed by a deletion is still applied, and a server that owns nothing is deleted at once.
			var now = DateTime.UtcNow;
			var (pvc, conflict) = await ExistingPvcAsync(server, cancellationToken);
			var reason = "NameConflict";
			var delete = server.Spec.PvcDeletionPolicy == "Delete";
			if (delete && conflict is null && !HasFinalizer(server))
				server = await SetFinalizerAsync(server, true, cancellationToken);
			if (pvc is not null)
				(pvc, conflict, reason) = await ApiWrites.UpdateClaimAsync(client, server, pvc, c => Ownership.ReconcileClaimOwner(c, server), cancellationToken);
			if (!delete && HasFinalizer(server)
				&& !Ownership.OwnsClaim(await client.GetAsync<V1PersistentVolumeClaim>(Resources.InstanceName(name), server.Namespace(), cancellationToken), server))
				server = await SetFinalizerAsync(server, false, cancellationToken);
			if (conflict is null)
				(conflict, reason) = await EnsureConfigMapAsync(server, cancellationToken);
			if (conflict is null)
				(conflict, reason) = await EnsureServiceAsync(server, cancellationToken);
			if (conflict is null && pvc is null)
				(pvc, conflict, reason) = await CreatePvcAsync(server, cancellationToken);
			var pod = await client.GetAsync<V1Pod>(Resources.InstanceName(name), server.Namespace(), cancellationToken);
			if (conflict is null && pod is not null && Ownership.Conflict("Pod", pod.Metadata, server) is { } podConflict)
				(conflict, reason) = (podConflict, "NameConflict");
			var ownedPod = pod is not null && Ownership.IsControlledBy(pod.Metadata, server) ? pod : null;
			// The Service selects the Pod by its labels: labels removed or changed by hand are restored.
			if (conflict is null && ownedPod is not null && ownedPod.Metadata.DeletionTimestamp is null && Ownership.ApplyLabels(ownedPod.Metadata, Labels.For(name)))
			{
				(ownedPod, conflict, reason) = await TryUpdateAsync("Pod", ownedPod, cancellationToken);
				pod = ownedPod;
			}
			var pvcDataVersion = pvc?.Metadata.Annotations?.TryGetValue(Labels.ClaimDataVersion, out var version) == true ? version : null;
			var pvcUnknown = pvc?.Metadata.Annotations?.TryGetValue(Labels.ClaimDataVersionUnknown, out var unknown) == true && !string.IsNullOrEmpty(unknown) ? unknown : null;
			var pvcProduct = pvc?.Metadata.Annotations?.TryGetValue(Labels.ClaimDataProduct, out var product) == true ? product : null;
			var pvcVersions = pvc?.Metadata.Annotations?.TryGetValue(Labels.ClaimDataVersions, out var versions) == true ? versions : null;

			// A pass that fails still records what a Pod of the server shows, and does nothing to the Pod.
			var failing = conflict is not null || pvc is null;
			var outcome = failing
				? Reconciliation.Conflict(server, conflict ?? "", now, reason, ownedPod, pvcDataVersion, options, pvcUnknown, pvcProduct, pvcVersions)
				: Reconciliation.Decide(server, pod, pvcDataVersion, now, options, pvcUnknown, pvcProduct, pvcVersions);

			// What the Pod has shown is written down before the Pod is touched: the data version on the claim, then the status. If the
			// claim cannot be written, the status keeps it and the server fails with the reason, and the Pod is left alone: a deleted Pod
			// could not tell the version again.
			if (await RecordDataVersionAsync(server, pvc, outcome.Status, pvcDataVersion, pvcUnknown, pvcProduct, cancellationToken) is ({ } claimRefused, var claimReason) && !failing)
			{
				outcome = Reconciliation.Conflict(server, claimRefused, now, claimReason, ownedPod, pvcDataVersion, options, pvcUnknown, pvcProduct, pvcVersions);
				failing = true;
			}
			var result = await WriteStatusAsync(server, outcome, cancellationToken);
			if (failing)
				return result;
			server = result.Entity;

			switch (outcome.Action)
			{
				case PodAction.Create when outcome.DesiredPod is { } desired:
					var created = desired.Metadata.Name;
					var image = desired.Spec.Containers[0].Image;
					Log.CreatingPod(logger, name, created, image);
					// A Pod the API refuses, like requests above the limits, fails the server with the explanation of the API.
					V1Pod? admitted = null;
					if (await WriteAsync("Pod", created, async () => admitted = await client.CreateAsync(desired, cancellationToken), conflictIsFailure: false) is ({ } refused, var refusedReason))
						return await WriteStatusAsync(server, Reconciliation.Conflict(server, refused, DateTime.UtcNow, refusedReason, null, null, options), cancellationToken);
					// A mutating webhook rewrote the image, like a mirror or a policy that adds a digest: the Pod keeps the image the API
					// admitted, so that the next passes tell it from an edit by hand and do not replace it again and again.
					if (ImageOf(admitted) is { } admittedImage && admittedImage != image)
						await RecordAdmittedImageAsync(name, admitted!, admittedImage, cancellationToken);
					break;
				case PodAction.Delete when pod is not null:
					// The replacement is checked first by the API, without being created: a Pod it would refuse as invalid, like requests
					// above the limits, must not cost the running one. Other answers, like a quota counting the old Pod, do not stop it.
					if (outcome.DesiredPod is { } replacement && await DryRunAsync(replacement, cancellationToken) is { } invalid)
						return await WriteStatusAsync(server, Reconciliation.Conflict(server, invalid, now, "PodRefused", ownedPod, pvcDataVersion, options, pvcUnknown, pvcProduct, pvcVersions), cancellationToken);
					var deleted = pod.Metadata.Name;
					var why = outcome.Status.Conditions.First(c => c.Type == "Ready").Message;
					Log.DeletingPod(logger, name, deleted, why);
					// Only the Pod that was checked, as it was checked: if another Pod took its name, or the Pod was edited meanwhile, the UID or
					// the resource version differs and the API answers 409; the next pass reads it again.
					await client.ApiClient.CoreV1.DeleteNamespacedPodAsync(pod.Name(), pod.Namespace(),
						new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = pod.Uid(), ResourceVersion = pod.ResourceVersion() } },
						cancellationToken: cancellationToken);
					break;
			}
			return result;
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
		{
			// Another writer changed an object between the read and the write: the next pass reads it again.
			return ReconciliationResult<V1Alpha1Chroma>.Success(server, TimeSpan.FromSeconds(1));
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Forbidden && e.Response.Content?.Contains("NamespaceTerminating", StringComparison.Ordinal) == true)
		{
			// The namespace is being deleted, with the Chroma in it: nothing more to create there.
			return ReconciliationResult<V1Alpha1Chroma>.Success(server);
		}
	}

	public async Task<ReconciliationResult<V1Alpha1Chroma>> DeletedAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		// The Pod, the Service and the ConfigMap have the Chroma as owner: the garbage collector of Kubernetes deletes them; the
		// claim too with pvcDeletionPolicy Delete, once a pass has made the server its owner. With Retain the claim stays, with the data:
		// the passes remove the server from its owners, and ClaimFinalizer does it at the deletion for a change no pass applied yet.
		// Distributed: the objects of the chart have it as owner too, but the MemberLists lose it to the sysdb, and are deleted here.
		var name = server.Metadata.Name;
		Failures.TryRemove(server.Uid(), out _);
		DistributedReconciler.Forget(server.Uid());
		Log.Deleted(logger, name);
		if (server.Spec.Mode == V1Alpha1Chroma.ChromaSpec.DistributedMode)
		{
			try
			{
				await _distributed.DeletedAsync(server, cancellationToken);
			}
			catch (HttpOperationException e)
			{
				Log.MemberListsNotDeleted(logger, e, name, server.Namespace());
			}
		}
		return ReconciliationResult<V1Alpha1Chroma>.Success(server);
	}

	/// <summary>
	/// The data version on the claim too, and whether it is unknown: a Chroma created again on a retained claim then knows them.
	/// An update the API refuses is returned, as for the other writes.
	/// </summary>
	/// <param name="readVersion">The data version on the claim when this pass read it.</param>
	/// <param name="readIncident">The incident on the claim when this pass read it.</param>
	/// <param name="readProduct">The product of the data version on the claim when this pass read it.</param>
	private async Task<(string? Conflict, string Reason)> RecordDataVersionAsync(V1Alpha1Chroma server, V1PersistentVolumeClaim? pvc,
		V1Alpha1Chroma.ChromaStatus status, string? readVersion, string? readIncident, string? readProduct, CancellationToken cancellationToken)
	{
		if (pvc is null)
			return (null, "");
		var changedMeanwhile = false;
		var (claim, conflict, reason) = await ApiWrites.UpdateClaimAsync(client, server, pvc, c => RecordOn(c, status, readVersion, readIncident, readProduct, ref changedMeanwhile), cancellationToken);
		// Another writer changed the data version or the incident while this pass wrote them: both are kept, merged, and the pass stops
		// without touching the Pod, so that the next pass decides from the claim as it is now.
		if (conflict is null && changedMeanwhile)
			return ($"The PersistentVolumeClaim {claim.Name()} got another data version or incident while the operator wrote it: the next pass decides again.", "PersistentVolumeClaimConflict");
		return (conflict, reason);
	}

	/// <summary>
	/// Writes the data version and the incident of the status on the claim, merged with what the claim has now: the highest version of
	/// a product is never lowered, and an incident the claim got from elsewhere is not cleared. The product is the one of the status: the
	/// data has passed to it.
	/// </summary>
	private static bool RecordOn(V1PersistentVolumeClaim pvc, V1Alpha1Chroma.ChromaStatus status, string? readVersion, string? readIncident, string? readProduct, ref bool changedMeanwhile)
	{
		var annotations = pvc.Metadata.Annotations is null ? new Dictionary<string, string>() : new Dictionary<string, string>(pvc.Metadata.Annotations);
		var before = new Dictionary<string, string>(annotations);
		var claimVersion = annotations.TryGetValue(Labels.ClaimDataVersion, out var v) ? v : null;
		var claimIncident = annotations.TryGetValue(Labels.ClaimDataVersionUnknown, out var i) && !string.IsNullOrEmpty(i) ? i : null;
		var claimProduct = annotations.TryGetValue(Labels.ClaimDataProduct, out var p) ? p : null;
		changedMeanwhile |= claimVersion != readVersion || claimIncident != readIncident || claimProduct != readProduct;
		// The highest version of each product, of the claim and of the status together: the data version of the claim is the one of the
		// product of the status, which opened the data last.
		var data = Reconciliation.StateOf(status);
		var highest = Reconciliation.ParseVersions(annotations.GetValueOrDefault(Labels.ClaimDataVersions));
		if (ChromaImage.Parse(claimVersion) is { } claimed)
			Reconciliation.Raise(highest, Products.Parse(claimProduct) ?? Products.Chroma, claimed);
		foreach (var (product, version) in data.Highest)
			Reconciliation.Raise(highest, product, version);
		if (status.DataProduct is not null || status.DataVersion is not null)
		{
			// The version of the product that opened the data last; without it, as after an image without a version, none: a version of
			// another product would read as one of this.
			if (highest.TryGetValue(data.Product, out var current))
				annotations[Labels.ClaimDataVersion] = current.ToString();
			else
				annotations.Remove(Labels.ClaimDataVersion);
			// A claim without the product is of Chroma: the annotation is written for VecturaDB, or to change it; the versions of each
			// product once another product than this one has opened the data.
			if (data.Product != Products.Chroma || claimProduct is not null)
				annotations[Labels.ClaimDataProduct] = data.Product;
			if (highest.Keys.Any(p => p != data.Product) || annotations.ContainsKey(Labels.ClaimDataVersions))
				annotations[Labels.ClaimDataVersions] = Reconciliation.FormatVersions(highest);
		}
		if (status.DataVersionUnknown == true && status.DataVersionUnknownIncident is { } incident)
			annotations[Labels.ClaimDataVersionUnknown] = claimIncident is not null && claimIncident != readIncident ? claimIncident : incident;
		else if (claimIncident is not null && claimIncident == readIncident)
			annotations.Remove(Labels.ClaimDataVersionUnknown);
		if (annotations.Count == before.Count && annotations.All(a => before.TryGetValue(a.Key, out var b) && b == a.Value))
			return false;
		pvc.Metadata.Annotations = annotations;
		return true;
	}

	/// <summary>Null when the API would create the Pod; otherwise why it is invalid (422). The check uses another name, so that the running Pod does not conflict.</summary>
	private async Task<string?> DryRunAsync(V1Pod pod, CancellationToken cancellationToken)
	{
		var probe = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(pod));
		probe.Metadata.Name = pod.Metadata.Name + "-check";
		try
		{
			await client.ApiClient.CoreV1.CreateNamespacedPodAsync(probe, pod.Namespace(), dryRun: "All", cancellationToken: cancellationToken);
			return null;
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.UnprocessableEntity)
		{
			return $"The API refused the new Pod {pod.Name()}, so the running one stays: {e.Response.Content}";
		}
		catch (HttpOperationException)
		{
			return null;
		}
	}

	/// <summary>
	/// An update; one the API refuses (422, or 403 outside a namespace being deleted) is returned with its explanation. A conflict (409)
	/// leaves the object as it was read: the next pass reads it again, and this pass still records what it saw.
	/// </summary>
	private async Task<(T Value, string? Conflict, string Reason)> TryUpdateAsync<T>(string kind, T value, CancellationToken cancellationToken)
		where T : IKubernetesObject<V1ObjectMeta>
	{
		try
		{
			return (await client.UpdateAsync(value, cancellationToken), null, "");
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
		{
			return (value, null, "");
		}
		catch (HttpOperationException e) when (ApiWrites.IsRefusal(e))
		{
			return (value, $"The API refused the update of the {kind} {value.Name()}: {e.Response.Content}", kind + "Refused");
		}
	}

	private static string? ImageOf(V1Pod? pod) => pod?.Spec?.Containers?.FirstOrDefault(c => c.Name == Resources.ContainerName)?.Image;

	/// <summary>
	/// Writes on the Pod just created the image the API admitted. A Pod changed meanwhile (409), as at its binding to a node, is read
	/// again, a few times; a Pod whose image is no longer the admitted one was edited, and is not marked.
	/// </summary>
	private async Task RecordAdmittedImageAsync(string server, V1Pod pod, string admittedImage, CancellationToken cancellationToken)
	{
		for (var attempt = 1; attempt <= ApiWrites.Attempts; attempt++)
		{
			if (attempt > 1)
			{
				var fresh = await client.GetAsync<V1Pod>(pod.Name(), pod.Namespace(), cancellationToken);
				if (fresh is null || fresh.Uid() != pod.Uid() || ImageOf(fresh) != admittedImage)
					return;
				pod = fresh;
			}
			pod.Metadata.Annotations ??= new Dictionary<string, string>();
			pod.Metadata.Annotations[Labels.AdmittedImage] = admittedImage;
			try
			{
				await client.UpdateAsync(pod, cancellationToken);
				return;
			}
			catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
			{
			}
			catch (HttpOperationException e) when (ApiWrites.IsRefusal(e))
			{
				var podName = pod.Name();
				var body = e.Response.Content;
				Log.AdmittedImageRefused(logger, server, podName, body);
				return;
			}
		}
	}

	private static bool HasFinalizer(V1Alpha1Chroma server) => server.Metadata.Finalizers?.Contains(ClaimFinalizer.Identifier) == true;

	private Task<V1Alpha1Chroma> SetFinalizerAsync(V1Alpha1Chroma server, bool present, CancellationToken cancellationToken)
	{
		var finalizers = (server.Metadata.Finalizers ?? []).Where(f => f != ClaimFinalizer.Identifier).ToList();
		if (present)
			finalizers.Add(ClaimFinalizer.Identifier);
		server.Metadata.Finalizers = finalizers;
		return client.UpdateAsync(server, cancellationToken);
	}

	private async Task<V1Pod?> OwnedPodAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var pod = await client.GetAsync<V1Pod>(Resources.InstanceName(server.Name()), server.Namespace(), cancellationToken);
		return pod is not null && Ownership.IsControlledBy(pod.Metadata, server) ? pod : null;
	}

	private async Task<ReconciliationResult<V1Alpha1Chroma>> WriteStatusAsync(V1Alpha1Chroma server, Outcome outcome, CancellationToken cancellationToken)
	{
		if (!SameStatus(server.Status, outcome.Status))
		{
			server.Status = outcome.Status;
			server = await client.UpdateStatusAsync(server, cancellationToken);
		}
		return ReconciliationResult<V1Alpha1Chroma>.Success(server, outcome.RequeueAfter);
	}

	private async Task<(string? Conflict, string Reason)> EnsureConfigMapAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var desired = Resources.ConfigMap(server);
		var current = await client.GetAsync<V1ConfigMap>(desired.Name(), desired.Namespace(), cancellationToken);
		if (current is not null && Ownership.Conflict("ConfigMap", current.Metadata, server) is { } conflict)
			return (conflict, "NameConflict");
		if (current is not null && current.Data?.TryGetValue(Resources.ConfigFile, out var data) == true && data == desired.Data[Resources.ConfigFile])
			return (null, "");
		if (current is not null)
			current.Data = desired.Data;
		return await WriteAsync("ConfigMap", desired.Name(), () => current is null ? client.CreateAsync(desired, cancellationToken) : client.UpdateAsync(current, cancellationToken));
	}

	private async Task<(string? Conflict, string Reason)> EnsureServiceAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var desired = Resources.Service(server);
		Ownership.ApplyAnnotations(desired.Metadata, server.Spec.Service?.Annotations);
		var current = await client.GetAsync<V1Service>(desired.Name(), desired.Namespace(), cancellationToken);
		if (current is null)
			return await WriteAsync("Service", desired.Name(), () => client.CreateAsync(desired, cancellationToken));
		if (Ownership.Conflict("Service", current.Metadata, server) is { } conflict)
			return (conflict, "NameConflict");
		var changed = Ownership.ApplyAnnotations(current.Metadata, server.Spec.Service?.Annotations);
		changed |= Ownership.ApplyRouting(current, desired);
		if (current.Spec.Type != desired.Spec.Type)
		{
			current.Spec.Type = desired.Spec.Type;
			changed = true;
		}
		return changed ? await WriteAsync("Service", desired.Name(), () => client.UpdateAsync(current, cancellationToken)) : (null, "");
	}

	/// <summary>A write the API refuses (<see cref="ApiWrites.IsRefusal"/>) fails the server with the explanation of the API.</summary>
	/// <param name="conflictIsFailure">Whether a conflict (409) fails the pass with what the Pod showed, as for a ConfigMap or a Service
	/// changed meanwhile; false for the creation of the Pod, which is tried again on the next pass.</param>
	private static async Task<(string? Conflict, string Reason)> WriteAsync<T>(string kind, string name, Func<Task<T>> write, bool conflictIsFailure = true)
	{
		try
		{
			await write();
			return (null, "");
		}
		catch (HttpOperationException e) when (ApiWrites.IsRefusal(e))
		{
			return ($"The API refused the {kind} {name}: {e.Response.Content}", kind + "Refused");
		}
		catch (HttpOperationException e) when (conflictIsFailure && e.Response.StatusCode == HttpStatusCode.Conflict)
		{
			return ($"The {kind} {name} changed while the operator wrote it: the next pass writes it again.", kind + "Conflict");
		}
	}

	/// <summary>The existing claim of the server, or why it cannot be used; null and null when there is none.</summary>
	private async Task<(V1PersistentVolumeClaim? Pvc, string? Conflict)> ExistingPvcAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var current = await client.GetAsync<V1PersistentVolumeClaim>(Resources.InstanceName(server.Name()), server.Namespace(), cancellationToken);
		if (current is null)
			return (null, null);
		return Ownership.ClaimConflict(current, server) is { } conflict ? (null, conflict) : (current, null);
	}

	/// <summary>Creates the claim; a claim the API refuses, like a size of 0 or a used-up quota, fails the server with the explanation of the API.</summary>
	private async Task<(V1PersistentVolumeClaim? Pvc, string? Conflict, string Reason)> CreatePvcAsync(V1Alpha1Chroma server, CancellationToken cancellationToken)
	{
		var desired = Resources.Pvc(server);
		var name = server.Metadata.Name;
		var pvc = desired.Metadata.Name;
		var size = server.Spec.Storage!.Size;
		Log.CreatingPvc(logger, name, pvc, size);
		try
		{
			return (await client.CreateAsync(desired, cancellationToken), null, "");
		}
		catch (HttpOperationException e) when (ApiWrites.IsRefusal(e))
		{
			return (null, $"The API refused the PersistentVolumeClaim {pvc}: {e.Response.Content}", "PersistentVolumeClaimRefused");
		}
	}

	private static bool SameStatus(V1Alpha1Chroma.ChromaStatus? a, V1Alpha1Chroma.ChromaStatus b) =>
		a is not null && KubernetesJson.Serialize(a) == KubernetesJson.Serialize(b);
}
