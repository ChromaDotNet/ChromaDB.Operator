using ChromaDotNet.Operator.Entities;
using k8s.Models;

namespace ChromaDotNet.Operator;

/// <summary>
/// Which existing objects the operator may use and change. Pod, ConfigMap and Service carry the ChromaServer as their controller:
/// one with the same name and another controller, or none, belongs to someone else and is never changed. The claim is different:
/// with pvcDeletionPolicy Retain it has no owner, so that it survives the ChromaServer; it is used when it has the labels the
/// operator gave it for a server of this name, or the ChromaServer as controller.
/// </summary>
internal static class Ownership
{
	public static bool IsControlledBy(V1ObjectMeta metadata, V1Alpha1ChromaServer server) =>
		metadata.OwnerReferences?.Any(o => o.Controller == true && o.Uid == server.Uid()) == true;

	/// <summary>Null when the object belongs to the server; otherwise why it cannot be used.</summary>
	public static string? Conflict(string kind, V1ObjectMeta metadata, V1Alpha1ChromaServer server)
	{
		if (IsControlledBy(metadata, server))
			return null;
		var controller = metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
		return controller is null
			? $"{kind} {metadata.Name} exists and was not created by this ChromaServer: delete or rename it."
			: $"{kind} {metadata.Name} exists and belongs to {controller.Kind} {controller.Name}: delete or rename it.";
	}

	/// <summary>Null when the claim can hold the data of the server; otherwise why it cannot.</summary>
	public static string? ClaimConflict(V1PersistentVolumeClaim pvc, V1Alpha1ChromaServer server)
	{
		var controller = pvc.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
		if (controller is not null)
			return controller.Uid == server.Uid() ? null : $"PersistentVolumeClaim {pvc.Name()} belongs to {controller.Kind} {controller.Name}: delete or rename it.";
		var labels = pvc.Metadata.Labels;
		if (labels?.TryGetValue(Labels.Server, out var name) == true && name == server.Name()
			&& labels.TryGetValue("app.kubernetes.io/managed-by", out var managedBy) && managedBy == Labels.ManagedBy)
			return null;
		return $"PersistentVolumeClaim {pvc.Name()} exists and was not created by the operator for a ChromaServer {server.Name()}. To use its data, " +
			$"label it with {Labels.Server}={server.Name()} and app.kubernetes.io/managed-by={Labels.ManagedBy}; otherwise delete or rename it.";
	}

	/// <summary>Whether the server is among the owners of the claim, so that the garbage collector deletes the claim with it.</summary>
	public static bool OwnsClaim(V1PersistentVolumeClaim? pvc, V1Alpha1ChromaServer server) =>
		pvc?.Metadata.OwnerReferences?.Any(o => o.Uid == server.Uid()) == true;

	/// <summary>
	/// Makes the owner of the claim follow pvcDeletionPolicy: the ChromaServer as controller with Delete, so that the garbage collector
	/// deletes the claim with it; no reference to it with Retain. Other references stay. True when the claim changed.
	/// </summary>
	public static bool ReconcileClaimOwner(V1PersistentVolumeClaim pvc, V1Alpha1ChromaServer server)
	{
		var references = pvc.Metadata.OwnerReferences ?? [];
		var ours = OwnsClaim(pvc, server);
		if (server.Spec.PvcDeletionPolicy == "Delete")
		{
			if (ours)
				return false;
			pvc.Metadata.OwnerReferences = [.. references, Resources.OwnerReference(server)];
			return true;
		}
		if (!ours)
			return false;
		pvc.Metadata.OwnerReferences = references.Where(o => o.Uid != server.Uid()).ToList();
		return true;
	}

	/// <summary>
	/// Restores the labels the operator gives an object, as the label the Service selects the Pod by: without it the Service has no
	/// backend. Other labels stay. True when the metadata changed.
	/// </summary>
	public static bool ApplyLabels(V1ObjectMeta metadata, IDictionary<string, string> desired)
	{
		var labels = metadata.Labels ?? new Dictionary<string, string>();
		if (desired.All(d => labels.TryGetValue(d.Key, out var v) && v == d.Value))
			return false;
		var restored = new Dictionary<string, string>(labels);
		foreach (var (key, value) in desired)
			restored[key] = value;
		metadata.Labels = restored;
		return true;
	}

	/// <summary>
	/// Restores the routing of the Service when someone changed it: the selector of the Pod and the one HTTP port. The addresses
	/// Kubernetes gave it stay: the cluster IP, and the node port of a NodePort or LoadBalancer. True when the Service changed.
	/// </summary>
	public static bool ApplyRouting(V1Service current, V1Service desired)
	{
		var wanted = desired.Spec.Ports.Single();
		var ports = current.Spec.Ports ?? [];
		var port = ports.Count == 1 ? ports[0] : null;
		var selectorSame = current.Spec.Selector is { } selector && selector.Count == desired.Spec.Selector.Count
			&& desired.Spec.Selector.All(s => selector.TryGetValue(s.Key, out var v) && v == s.Value);
		var portSame = port is not null && port.Name == wanted.Name && port.Port == wanted.Port
			&& port.TargetPort?.Value == wanted.TargetPort.Value && (port.Protocol ?? "TCP") == wanted.Protocol;
		if (selectorSame && portSame)
			return false;
		current.Spec.Selector = new Dictionary<string, string>(desired.Spec.Selector);
		// The node port Kubernetes allocated stays: the one of the sole port, renamed or renumbered or not, or of the matching port.
		var nodePort = ports.Count == 1 ? ports[0].NodePort
			: (ports.FirstOrDefault(p => p.Name == wanted.Name) ?? ports.FirstOrDefault(p => p.Port == wanted.Port))?.NodePort;
		current.Spec.Ports =
		[
			new V1ServicePort { Name = wanted.Name, Port = wanted.Port, TargetPort = wanted.TargetPort, Protocol = wanted.Protocol, NodePort = nodePort },
		];
		return true;
	}

	/// <summary>
	/// Applies the annotations of the spec of the Service, and removes those the spec had and no longer has. The keys applied are kept
	/// in <see cref="Labels.ManagedAnnotations"/>; annotations of other controllers stay. True when the metadata changed.
	/// </summary>
	public static bool ApplyAnnotations(V1ObjectMeta metadata, IDictionary<string, string>? desired)
	{
		var annotations = metadata.Annotations is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata.Annotations);
		var before = new Dictionary<string, string>(annotations);
		var previous = annotations.TryGetValue(Labels.ManagedAnnotations, out var list) ? list.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
		desired ??= new Dictionary<string, string>();
		foreach (var key in previous.Where(k => !desired.ContainsKey(k)))
			annotations.Remove(key);
		foreach (var (key, value) in desired)
			annotations[key] = value;
		if (desired.Count > 0)
			annotations[Labels.ManagedAnnotations] = string.Join(',', desired.Keys.Order(StringComparer.Ordinal));
		else
			annotations.Remove(Labels.ManagedAnnotations);
		if (annotations.Count == before.Count && annotations.All(a => before.TryGetValue(a.Key, out var v) && v == a.Value))
			return false;
		metadata.Annotations = annotations;
		return true;
	}
}
