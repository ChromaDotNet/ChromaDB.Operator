using System.Net;
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
/// Reconciles a ChromaServer: the ConfigMap, the Service, the PersistentVolumeClaim and the Pod. The watcher of KubeOps reacts to
/// changes of the spec only, so the controller looks again on its own: every 2 seconds while the server is not ready, every 30
/// seconds after, and at once when one of its Pods changes (<see cref="PodWatcher"/>). The Pod and the PersistentVolumeClaim are
/// managed directly, as CloudNativePG does, without a StatefulSet. Objects with the same names that belong to someone else are never
/// changed: the server fails with the reason (<see cref="Ownership"/>).
/// </summary>
// Only what the operator does. On the ChromaServers: the watch, the default image and the finalizer (update), and the status. The
// owner references of its objects block the deletion of the server (blockOwnerDeletion): with OwnerReferencesPermissionEnforcement,
// as on OpenShift, setting them needs update on chromaservers/finalizers. On the core types GenericRbac, since EntityRbac would add
// their status too: the Pods are watched, recreated and deleted, the other objects read, created and updated.
[EntityRbac(typeof(V1Alpha1ChromaServer), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Update)]
[GenericRbac(Groups = [Labels.Group], Resources = ["chromaservers/finalizers"], Verbs = RbacVerb.Update)]
[GenericRbac(Groups = [""], Resources = ["pods"],
	Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch | RbacVerb.Create | RbacVerb.Update | RbacVerb.Delete)]
[GenericRbac(Groups = [""], Resources = ["configmaps", "persistentvolumeclaims", "services"], Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
public sealed class ChromaServerController(IKubernetesClient client, OperatorOptions options, ILogger<ChromaServerController> logger)
	: IEntityController<V1Alpha1ChromaServer>
{

	public async Task<ReconciliationResult<V1Alpha1ChromaServer>> ReconcileAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		var name = server.Name();
		try
		{
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

			// A pass that fails still records what a Pod of the server shows, and does nothing to the Pod.
			var failing = conflict is not null || pvc is null;
			var outcome = failing
				? Reconciliation.Conflict(server, conflict ?? "", now, reason, ownedPod, pvcDataVersion, options, pvcUnknown)
				: Reconciliation.Decide(server, pod, pvcDataVersion, now, options, pvcUnknown);

			// What the Pod has shown is written down before the Pod is touched: the data version on the claim, then the status. If the
			// claim cannot be written, the status keeps it and the server fails with the reason, and the Pod is left alone: a deleted Pod
			// could not tell the version again.
			if (await RecordDataVersionAsync(server, pvc, outcome.Status, pvcDataVersion, pvcUnknown, cancellationToken) is ({ } claimRefused, var claimReason) && !failing)
			{
				outcome = Reconciliation.Conflict(server, claimRefused, now, claimReason, ownedPod, pvcDataVersion, options, pvcUnknown);
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
					if (await WriteAsync("Pod", created, () => client.CreateAsync(desired, cancellationToken), conflictIsFailure: false) is ({ } refused, var refusedReason))
						return await WriteStatusAsync(server, Reconciliation.Conflict(server, refused, DateTime.UtcNow, refusedReason, null, null, options), cancellationToken);
					break;
				case PodAction.Delete when pod is not null:
					// The replacement is checked first by the API, without being created: a Pod it would refuse as invalid, like requests
					// above the limits, must not cost the running one. Other answers, like a quota counting the old Pod, do not stop it.
					if (outcome.DesiredPod is { } replacement && await DryRunAsync(replacement, cancellationToken) is { } invalid)
						return await WriteStatusAsync(server, Reconciliation.Conflict(server, invalid, now, "PodRefused", ownedPod, pvcDataVersion, options, pvcUnknown), cancellationToken);
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
			return ReconciliationResult<V1Alpha1ChromaServer>.Success(server, TimeSpan.FromSeconds(1));
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Forbidden && e.Response.Content?.Contains("NamespaceTerminating", StringComparison.Ordinal) == true)
		{
			// The namespace is being deleted, with the ChromaServer in it: nothing more to create there.
			return ReconciliationResult<V1Alpha1ChromaServer>.Success(server);
		}
		catch (HttpOperationException e)
		{
			var status = e.Response.StatusCode;
			var body = e.Response.Content;
			Log.ApiError(logger, e, name, status, body);
			return ReconciliationResult<V1Alpha1ChromaServer>.Failure(server, e.Response.Content ?? e.Message, e, TimeSpan.FromSeconds(10));
		}
	}

	public Task<ReconciliationResult<V1Alpha1ChromaServer>> DeletedAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		// The Pod, the Service and the ConfigMap have the ChromaServer as owner: the garbage collector of Kubernetes deletes them; the
		// claim too with pvcDeletionPolicy Delete, once a pass has made the server its owner. With Retain the claim stays, with the data:
		// the passes remove the server from its owners, and ClaimFinalizer does it at the deletion for a change no pass applied yet.
		var name = server.Metadata.Name;
		Log.Deleted(logger, name);
		return Task.FromResult(ReconciliationResult<V1Alpha1ChromaServer>.Success(server));
	}

	/// <summary>
	/// The data version on the claim too, and whether it is unknown: a ChromaServer created again on a retained claim then knows them.
	/// An update the API refuses is returned, as for the other writes.
	/// </summary>
	/// <param name="readVersion">The data version on the claim when this pass read it.</param>
	/// <param name="readIncident">The incident on the claim when this pass read it.</param>
	private async Task<(string? Conflict, string Reason)> RecordDataVersionAsync(V1Alpha1ChromaServer server, V1PersistentVolumeClaim? pvc,
		V1Alpha1ChromaServer.ServerStatus status, string? readVersion, string? readIncident, CancellationToken cancellationToken)
	{
		if (pvc is null)
			return (null, "");
		var changedMeanwhile = false;
		var (claim, conflict, reason) = await ApiWrites.UpdateClaimAsync(client, server, pvc, c => RecordOn(c, status, readVersion, readIncident, ref changedMeanwhile), cancellationToken);
		// Another writer changed the data version or the incident while this pass wrote them: both are kept, merged, and the pass stops
		// without touching the Pod, so that the next pass decides from the claim as it is now.
		if (conflict is null && changedMeanwhile)
			return ($"The PersistentVolumeClaim {claim.Name()} got another data version or incident while the operator wrote it: the next pass decides again.", "PersistentVolumeClaimConflict");
		return (conflict, reason);
	}

	/// <summary>
	/// Writes the data version and the incident of the status on the claim, merged with what the claim has now: a version is never
	/// lowered, and an incident the claim got from elsewhere is not cleared.
	/// </summary>
	private static bool RecordOn(V1PersistentVolumeClaim pvc, V1Alpha1ChromaServer.ServerStatus status, string? readVersion, string? readIncident, ref bool changedMeanwhile)
	{
		var annotations = pvc.Metadata.Annotations is null ? new Dictionary<string, string>() : new Dictionary<string, string>(pvc.Metadata.Annotations);
		var before = new Dictionary<string, string>(annotations);
		var claimVersion = annotations.TryGetValue(Labels.ClaimDataVersion, out var v) ? v : null;
		var claimIncident = annotations.TryGetValue(Labels.ClaimDataVersionUnknown, out var i) && !string.IsNullOrEmpty(i) ? i : null;
		changedMeanwhile |= claimVersion != readVersion || claimIncident != readIncident;
		if (Reconciliation.Max(status.DataVersion, claimVersion) is { } version)
			annotations[Labels.ClaimDataVersion] = version;
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

	private static bool HasFinalizer(V1Alpha1ChromaServer server) => server.Metadata.Finalizers?.Contains(ClaimFinalizer.Identifier) == true;

	private Task<V1Alpha1ChromaServer> SetFinalizerAsync(V1Alpha1ChromaServer server, bool present, CancellationToken cancellationToken)
	{
		var finalizers = (server.Metadata.Finalizers ?? []).Where(f => f != ClaimFinalizer.Identifier).ToList();
		if (present)
			finalizers.Add(ClaimFinalizer.Identifier);
		server.Metadata.Finalizers = finalizers;
		return client.UpdateAsync(server, cancellationToken);
	}

	private async Task<V1Pod?> OwnedPodAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		var pod = await client.GetAsync<V1Pod>(Resources.InstanceName(server.Name()), server.Namespace(), cancellationToken);
		return pod is not null && Ownership.IsControlledBy(pod.Metadata, server) ? pod : null;
	}

	private async Task<ReconciliationResult<V1Alpha1ChromaServer>> WriteStatusAsync(V1Alpha1ChromaServer server, Outcome outcome, CancellationToken cancellationToken)
	{
		if (!SameStatus(server.Status, outcome.Status))
		{
			server.Status = outcome.Status;
			server = await client.UpdateStatusAsync(server, cancellationToken);
		}
		return ReconciliationResult<V1Alpha1ChromaServer>.Success(server, outcome.RequeueAfter);
	}

	private async Task<(string? Conflict, string Reason)> EnsureConfigMapAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
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

	private async Task<(string? Conflict, string Reason)> EnsureServiceAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
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
	private async Task<(V1PersistentVolumeClaim? Pvc, string? Conflict)> ExistingPvcAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		var current = await client.GetAsync<V1PersistentVolumeClaim>(Resources.InstanceName(server.Name()), server.Namespace(), cancellationToken);
		if (current is null)
			return (null, null);
		return Ownership.ClaimConflict(current, server) is { } conflict ? (null, conflict) : (current, null);
	}

	/// <summary>Creates the claim; a claim the API refuses, like a size of 0 or a used-up quota, fails the server with the explanation of the API.</summary>
	private async Task<(V1PersistentVolumeClaim? Pvc, string? Conflict, string Reason)> CreatePvcAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		var desired = Resources.Pvc(server);
		var name = server.Metadata.Name;
		var pvc = desired.Metadata.Name;
		var size = server.Spec.Storage.Size;
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

	private static bool SameStatus(V1Alpha1ChromaServer.ServerStatus? a, V1Alpha1ChromaServer.ServerStatus b) =>
		a is not null && KubernetesJson.Serialize(a) == KubernetesJson.Serialize(b);
}
