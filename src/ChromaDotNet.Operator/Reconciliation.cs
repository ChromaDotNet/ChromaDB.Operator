using ChromaDotNet.Operator.Entities;
using k8s.Models;

namespace ChromaDotNet.Operator;

internal enum PodAction
{
	None,
	Create,
	Delete,
	Wait,
}

/// <summary>What to do with the Pod, the status to write, and when to look again.</summary>
internal sealed record Outcome(PodAction Action, V1Pod? DesiredPod, V1Alpha1Chroma.ChromaStatus Status, TimeSpan RequeueAfter);

/// <summary>
/// The decisions of the controller, without the API calls, so that they can be tested alone.
/// <list type="bullet">
/// <item>The Pod is recreated when the Pod the operator wants differs from the running one: a new image, a new configuration, other
/// settings. Chroma runs on a ReadWriteOnce volume, one server at a time: the new Pod is created only when the old one is gone.</item>
/// <item>An image outside the repositories allowed by the operator is refused: the running Pod stays.</item>
/// <item>status.dataVersion is the highest version of Chroma whose container has run on the data. Chroma migrates the data forward at
/// its start, and an older version does not open it again (1.0.0 panics on data opened by 1.5.9): an image older than dataVersion is
/// refused, unless the annotation <see cref="Labels.AllowDowngrade"/> is "true".</item>
/// </list>
/// </summary>
internal static class Reconciliation
{
	public static readonly TimeSpan SteadyRequeue = TimeSpan.FromSeconds(30);
	public static readonly TimeSpan BusyRequeue = TimeSpan.FromSeconds(2);

	private static readonly HashSet<string> FailingReasons =
		["CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "ErrImageNeverPull", "CreateContainerConfigError", "CreateContainerError", "InvalidImageName"];

	/// <param name="server">The Chroma.</param>
	/// <param name="pod">The Pod of the instance, or null.</param>
	/// <param name="pvcDataVersion">The data version kept on the PersistentVolumeClaim, for a claim retained by an earlier Chroma.</param>
	/// <param name="now">The time of the conditions.</param>
	/// <param name="options">The images the operator allows.</param>
	public static Outcome Decide(V1Alpha1Chroma server, V1Pod? pod, string? pvcDataVersion, DateTime now, OperatorOptions options, string? pvcDataVersionUnknownIncident = null)
	{
		var old = server.Status ?? new V1Alpha1Chroma.ChromaStatus();
		// What the Pod shows is recorded first, whatever the pass decides after: also a pass that fails keeps the data version and the
		// recovery image, so that a later pass without the Pod still refuses an image older than the data.
		var (status, seen) = Observe(server, pod, pvcDataVersion, pvcDataVersionUnknownIncident, options, now);
		var running = seen.Running;

		if (Resources.NameError(server.Name()) is { } nameError)
		{
			status.Phase = "Failed";
			SetCondition(status, "Ready", false, "InvalidName", nameError, now);
			return new Outcome(PodAction.None, null, status, SteadyRequeue);
		}

		var wanted = ChromaImage.VersionOf(server.Spec.Image);
		if (wanted is null)
		{
			status.Phase = "Failed";
			SetCondition(status, "Ready", false, "InvalidImage", $"{server.Spec.Image} has no exact version tag.", now);
			return new Outcome(PodAction.None, null, status, SteadyRequeue);
		}

		// An image without a version ran on the data: its version is unknown, and no Pod is replaced or started on it until an
		// administrator says the version. A living Pod stays as it is.
		if (status.DataVersionUnknown == true)
		{
			SetCondition(status, "UpgradeBlocked", true, "DataVersionUnknown",
				"An image without a version ran on the data, so its version is unknown: no Pod is replaced or started. Annotate the Chroma " +
				$"with {Labels.DeclaredDataVersion}=<x.y.z>/{status.DataVersionUnknownIncident}, where <x.y.z> is the highest version of Chroma that " +
				"has run on the data.", now);
			if (pod is not null && pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase is not ("Failed" or "Succeeded"))
				return PodState(status, pod, running, status.Image is null ? "Creating" : "Updating", now);
			status.Phase = "Failed";
			SetCondition(status, "Ready", false, "DataVersionUnknown", "No Pod starts on data of an unknown version.", now);
			return new Outcome(PodAction.None, null, status, SteadyRequeue);
		}

		var dataVersion = ChromaImage.Parse(status.DataVersion);
		var candidates = seen.Candidates;
		var allowDowngrade = server.Metadata.Annotations?.TryGetValue(Labels.AllowDowngrade, out var allow) == true && allow == "true";
		string? image = server.Spec.Image;
		var blocked = true;
		if (!options.IsAllowed(server.Spec.Image!))
		{
			SetCondition(status, "UpgradeBlocked", true, "ImageNotAllowed",
				$"{server.Spec.Image} is not in the repositories the operator allows ({string.Join(", ", options.AllowedRepositories)}): " +
				$"the running image stays. The administrator of the operator sets them in {OperatorOptions.AllowedImagesVariable}.", now);
			image = Fallback(candidates, dataVersion, allowDowngrade, options);
		}
		else if (dataVersion is not null && wanted < dataVersion && !allowDowngrade)
		{
			SetCondition(status, "UpgradeBlocked", true, "Downgrade",
				$"{server.Spec.Image} is older than the data, opened by {dataVersion}: Chroma does not open data of a newer version. " +
				$"The running image stays. To force it, annotate the Chroma with {Labels.AllowDowngrade}=true.", now);
			image = Fallback(candidates, dataVersion, allowDowngrade, options);
		}
		else
		{
			blocked = false;
			SetCondition(status, "UpgradeBlocked", false, "None", "", now);
		}

		// A blocked server keeps a living Pod as it is, whatever else changed: replacing it could leave no image the data allows, as
		// after a rollback of image and configuration together while a newer Pod was starting.
		var starting = old.Image is null ? "Creating" : "Updating";
		if (blocked && pod is not null && pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase is not ("Failed" or "Succeeded"))
			return PodState(status, pod, running, starting, now);

		if (image is null)
		{
			// No image can start a new Pod: a running Pod stays as it is, without being replaced.
			status.Phase = "Failed";
			SetCondition(status, "Ready", false, "NoImage", "The image is refused and there is no running image to keep.", now);
			return new Outcome(PodAction.None, null, status, SteadyRequeue);
		}

		var desired = Resources.Pod(server, image);
		if (pod is null)
		{
			// A newer Chroma can migrate the data before its Pod reports that it started: the data version goes up before the Pod is
			// created, and the controller writes it down first. A later image older than this is then refused.
			status.DataVersion = Max(status.DataVersion, ChromaImage.VersionOf(image)?.ToString(3));
			// A forced downgrade does not become the recovery image: it is older than the data.
			if (ChromaImage.VersionOf(image) >= ChromaImage.Parse(status.DataVersion))
				status.RecoveryImage = image;
			status.Phase = starting;
			SetCondition(status, "Ready", false, "PodCreated", $"Pod {desired.Name()} created with {image}.", now);
			return new Outcome(PodAction.Create, desired, status, BusyRequeue);
		}
		if (pod.Metadata.DeletionTimestamp is not null)
		{
			status.Phase = starting;
			SetCondition(status, "Ready", false, "PodTerminating", $"Pod {pod.Name()} is stopping.", now);
			return new Outcome(PodAction.Wait, null, status, BusyRequeue);
		}
		var current = pod.Metadata.Annotations?.TryGetValue(Labels.PodSpecHash, out var hash) == true ? hash : null;
		// The annotation says what the operator created; the image of the container says what runs, also after an edit by hand. A
		// mutating webhook can rewrite the image at the creation (a mirror, a digest): the image the API admitted is that of the Pod
		// the operator created, not an edit.
		var admitted = pod.Metadata.Annotations?.TryGetValue(Labels.AdmittedImage, out var admittedImage) == true ? admittedImage : null;
		if (current != desired.Metadata.Annotations[Labels.PodSpecHash] || (running != image && running != admitted) || pod.Status?.Phase is "Failed" or "Succeeded")
		{
			status.Phase = "Updating";
			var reason = pod.Status?.Phase is "Failed" or "Succeeded" ? $"Pod {pod.Name()} ended ({pod.Status.Phase})." : $"Pod {pod.Name()} differs from the spec.";
			SetCondition(status, "Ready", false, "PodReplaced", reason, now);
			return new Outcome(PodAction.Delete, desired, status, BusyRequeue);
		}

		return PodState(status, pod, running, starting, now);
	}

	/// <summary>
	/// The first of the candidates (the image of the Pod, the image the kubelet reports, the recovery image, the last ready image) that
	/// is still allowed and not older than the data. Without this check a Pod of 1.5.9 that ran without becoming ready, then disappeared,
	/// let a Pod of 1.5.3 start on migrated data; without the recovery image, the server had no image left to start with.
	/// </summary>
	private static string? Fallback(string?[] candidates, Version? dataVersion, bool allowDowngrade, OperatorOptions options)
	{
		foreach (var image in candidates)
		{
			if (image is null || !options.IsAllowed(image) || ChromaImage.VersionOf(image) is not { } version)
				continue;
			if (dataVersion is null || allowDowngrade || version >= dataVersion)
				return image;
		}
		return null;
	}

	/// <summary>The status from a Pod that stays as it is: ready, failing, or starting.</summary>
	private static Outcome PodState(V1Alpha1Chroma.ChromaStatus status, V1Pod pod, string? running, string starting, DateTime now)
	{
		if (IsReady(pod))
		{
			status.Phase = "Ready";
			status.Image = running;
			SetCondition(status, "Ready", true, "PodReady", $"Chroma answers on {status.Endpoint}.", now);
			return new Outcome(PodAction.None, null, status, SteadyRequeue);
		}
		var waiting = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == Resources.ContainerName)?.State?.Waiting;
		if (waiting?.Reason is { } waitingReason && FailingReasons.Contains(waitingReason))
		{
			status.Phase = "Failed";
			SetCondition(status, "Ready", false, waitingReason, waiting.Message ?? waitingReason, now);
			return new Outcome(PodAction.None, null, status, BusyRequeue);
		}
		status.Phase = starting;
		SetCondition(status, "Ready", false, "PodStarting", $"Pod {pod.Name()} is starting.", now);
		return new Outcome(PodAction.None, null, status, BusyRequeue);
	}

	private sealed record Seen(string? Running, string?[] Candidates);

	/// <summary>
	/// The status with what the Pod shows. The data version grows with the image of the Pod as soon as it exists, since a Pod edited in
	/// place to a newer image can migrate the data while it still reports ContainerCreating, and with the image the kubelet reports once
	/// the container has run, since the spec of the Pod can be edited while another image runs. The image is the one of the container
	/// named chroma: an admission webhook can add sidecars, before it too. The recovery image is the newest image seen that the data
	/// and the operator still allow: the image a server whose spec is refused starts again with.
	/// </summary>
	private static (V1Alpha1Chroma.ChromaStatus Status, Seen Seen) Observe(V1Alpha1Chroma server, V1Pod? pod, string? pvcDataVersion, string? pvcDataVersionUnknownIncident, OperatorOptions options, DateTime now)
	{
		var old = server.Status ?? new V1Alpha1Chroma.ChromaStatus();
		var status = new V1Alpha1Chroma.ChromaStatus
		{
			Phase = old.Phase,
			Image = old.Image,
			RecoveryImage = old.RecoveryImage,
			DataVersionDeclaredPod = old.DataVersionDeclaredPod,
			DataVersion = Max(old.DataVersion, pvcDataVersion),
			Pod = Resources.InstanceName(server.Name()),
			Pvc = Resources.InstanceName(server.Name()),
			Endpoint = Resources.Endpoint(server),
			ObservedGeneration = server.Metadata.Generation,
			Conditions = old.Conditions.Select(Copy).ToList(),
		};
		var running = pod?.Spec?.Containers?.FirstOrDefault(c => c.Name == Resources.ContainerName)?.Image;
		string? reported = null;
		// An image without a version, like latest or a digest alone, put into the Pod by hand: the version of the data becomes unknown.
		// The claim keeps the code of an open incident: while it cannot be cleared, the next pass sees the same incident, which the same
		// declaration resolves again.
		var unknown = old.DataVersionUnknown == true || pvcDataVersionUnknownIncident is not null;
		// The Pod and image a declaration covered stay covered while that Pod stops: no new incident for them, and only for them.
		var podImage = pod is null ? null : $"{pod.Uid()}/{running}";
		// The declaration covers that Pod with that image only: once the Pod or its image changes, it covers nothing any more, also if
		// the same Pod later goes back to the same image.
		if (pod is not null && podImage != old.DataVersionDeclaredPod)
			status.DataVersionDeclaredPod = null;
		var seenNow = false;
		void Count(string? image)
		{
			if (image is null)
				return;
			if (ChromaImage.VersionOf(image) is { } version)
				status.DataVersion = Max(status.DataVersion, version.ToString(3));
			else if (podImage != old.DataVersionDeclaredPod)
				unknown = seenNow = true;
		}
		if (pod is not null)
		{
			Count(running);
			if (ContainerHasRun(pod))
			{
				reported = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == Resources.ContainerName)?.Image;
				// The kubelet writes the image in its own way, sometimes by digest or image id alone: it counts only with a version. Whether
				// the version is unknown is decided by the image of the Pod spec.
				if (ChromaImage.VersionOf(reported) is { } reportedVersion)
					status.DataVersion = Max(status.DataVersion, reportedVersion.ToString(3));
			}
		}
		// Each incident has a code, and a declaration resolves only the incident it names: <x.y.z>/<code>. A declaration left on the
		// Chroma after an earlier incident names an old code, and a new incident needs a new declaration.
		// The code on the claim is reused only to retry the incident it names: an image without a version seen now, while no incident is
		// open in the status, is a new incident with a fresh code, which an older declaration does not resolve.
		var fresh = Resources.Hash($"{server.Uid()}|{pod?.Uid()}|{running}|{now:O}")[..8];
		// Two different open incidents, one in the status and one on the claim: one code for both, from the two codes, so that a
		// declaration of one alone resolves neither. The code is the same on every pass, and once on the claim it stays.
		var open = old.DataVersionUnknownIncident;
		if (open is not null && pvcDataVersionUnknownIncident is not null && open != pvcDataVersionUnknownIncident)
			open = Resources.Hash($"{open}+{pvcDataVersionUnknownIncident}")[..8];
		status.DataVersionUnknownIncident = unknown
			? open ?? (seenNow ? fresh : pvcDataVersionUnknownIncident ?? fresh)
			: null;
		if (unknown && Declared(server, status.DataVersionUnknownIncident!) is { } declared)
		{
			status.DataVersion = Max(status.DataVersion, declared.ToString(3));
			unknown = false;
			status.DataVersionUnknownIncident = null;
			if (pod is not null && ChromaImage.VersionOf(running) is null)
				status.DataVersionDeclaredPod = podImage;
		}
		status.DataVersionUnknown = unknown;
		string?[] candidates = [running, reported, old.RecoveryImage, old.Image];
		status.RecoveryImage = Fallback(candidates, ChromaImage.Parse(status.DataVersion), false, options) ?? old.RecoveryImage;
		return (status, new Seen(running, candidates));
	}

	/// <summary>
	/// The outcome when an object of the server cannot be used or written: one of someone else with its name (NameConflict), or a write
	/// the API refused. Nothing is touched.
	/// </summary>
	/// <param name="pod">The Pod of the server, when it belongs to it: what it shows is recorded, nothing is done to it.</param>
	/// <summary>The version the administrator declared for this incident, or null.</summary>
	private static Version? Declared(V1Alpha1Chroma server, string incident)
	{
		if (server.Metadata.Annotations?.TryGetValue(Labels.DeclaredDataVersion, out var text) != true)
			return null;
		var slash = text!.IndexOf('/', StringComparison.Ordinal);
		return slash > 0 && text![(slash + 1)..] == incident ? ChromaImage.Parse(text![..slash]) : null;
	}

	/// <summary>
	/// The outcome for a Chroma in the distributed mode, which this version of the operator does not implement: nothing is created, and
	/// no later pass changes that, since the mode does not change after the creation.
	/// </summary>
	public static Outcome DistributedNotImplemented(V1Alpha1Chroma server, DateTime now)
	{
		var old = server.Status ?? new V1Alpha1Chroma.ChromaStatus();
		var status = new V1Alpha1Chroma.ChromaStatus
		{
			Phase = "Failed",
			ObservedGeneration = server.Metadata.Generation,
			Conditions = old.Conditions.Select(Copy).ToList(),
		};
		SetCondition(status, "Ready", false, "DistributedNotImplemented",
			"The distributed mode is not implemented yet by this version of the operator: nothing is created.", now);
		return new Outcome(PodAction.None, null, status, TimeSpan.Zero);
	}

	public static Outcome Conflict(V1Alpha1Chroma server, string message, DateTime now, string reason, V1Pod? pod, string? pvcDataVersion, OperatorOptions options, string? pvcDataVersionUnknownIncident = null)
	{
		var (status, _) = Observe(server, pod, pvcDataVersion, pvcDataVersionUnknownIncident, options, now);
		status.Phase = "Failed";
		SetCondition(status, "Ready", false, reason, message, now);
		return new Outcome(PodAction.None, null, status, SteadyRequeue);
	}

	public static bool IsReady(V1Pod pod) =>
		pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;

	/// <summary>Whether the container of Chroma has started at least once: then its migrations can have changed the data.</summary>
	public static bool ContainerHasRun(V1Pod pod)
	{
		var container = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == Resources.ContainerName);
		if (container is null)
			return false;
		return container.Started == true || container.RestartCount > 0 || container.State?.Running is not null
			|| container.State?.Terminated is not null || container.LastState?.Terminated is not null;
	}

	public static string? Max(string? a, string? b)
	{
		var va = ChromaImage.Parse(a);
		var vb = ChromaImage.Parse(b);
		if (va is null)
			return vb?.ToString(3);
		if (vb is null)
			return va.ToString(3);
		return (va >= vb ? va : vb).ToString(3);
	}

	public static void SetCondition(V1Alpha1Chroma.ChromaStatus status, string type, bool value, string reason, string message, DateTime now)
	{
		var text = value ? "True" : "False";
		var condition = status.Conditions.FirstOrDefault(c => c.Type == type);
		if (condition is null)
		{
			status.Conditions.Add(new V1Condition { Type = type, Status = text, Reason = reason, Message = message, LastTransitionTime = now });
			return;
		}
		if (condition.Status != text)
			condition.LastTransitionTime = now;
		condition.Status = text;
		condition.Reason = reason;
		condition.Message = message;
	}

	private static V1Condition Copy(V1Condition c) => new()
	{
		Type = c.Type,
		Status = c.Status,
		Reason = c.Reason,
		Message = c.Message,
		LastTransitionTime = c.LastTransitionTime,
		ObservedGeneration = c.ObservedGeneration,
	};
}
