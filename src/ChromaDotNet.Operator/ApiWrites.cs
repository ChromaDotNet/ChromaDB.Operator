using System.Net;
using ChromaDotNet.Operator.Entities;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;

namespace ChromaDotNet.Operator;

/// <summary>The writes of the controller and of the finalizer that need the same handling of refusals and conflicts.</summary>
internal static class ApiWrites
{
	public const int Attempts = 5;

	/// <summary>
	/// A write the API refuses: invalid (422), like an annotation with a key that is not a qualified name, or forbidden (403), like a
	/// ResourceQuota that is used up. A namespace being deleted is not a refusal.
	/// </summary>
	public static bool IsRefusal(HttpOperationException e) =>
		e.Response.StatusCode == HttpStatusCode.UnprocessableEntity
		|| (e.Response.StatusCode == HttpStatusCode.Forbidden && e.Response.Content?.Contains("NamespaceTerminating", StringComparison.Ordinal) != true);

	/// <summary>
	/// Changes the claim and writes it. When another writer changed it meanwhile (409), the claim is read again and the change made
	/// again, a few times; a claim that keeps changing, or an update the API refuses, is returned with the reason.
	/// </summary>
	public static async Task<(V1PersistentVolumeClaim Claim, string? Conflict, string Reason)> UpdateClaimAsync(IKubernetesClient client,
		V1Alpha1ChromaServer server, V1PersistentVolumeClaim claim, Func<V1PersistentVolumeClaim, bool> change, CancellationToken cancellationToken)
	{
		for (var attempt = 1; attempt <= Attempts; attempt++)
		{
			if (attempt > 1)
			{
				var fresh = await client.GetAsync<V1PersistentVolumeClaim>(claim.Name(), claim.Namespace(), cancellationToken);
				if (fresh is null || Ownership.ClaimConflict(fresh, server) is not null)
					return (claim, $"The PersistentVolumeClaim {claim.Name()} changed while the operator wrote it.", "PersistentVolumeClaimConflict");
				claim = fresh;
			}
			if (!change(claim))
				return (claim, null, "");
			try
			{
				return (await client.UpdateAsync(claim, cancellationToken), null, "");
			}
			catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
			{
			}
			catch (HttpOperationException e) when (IsRefusal(e))
			{
				return (claim, $"The API refused the update of the PersistentVolumeClaim {claim.Name()}: {e.Response.Content}", "PersistentVolumeClaimRefused");
			}
		}
		return (claim, $"The PersistentVolumeClaim {claim.Name()} kept changing while the operator wrote it ({Attempts} attempts).", "PersistentVolumeClaimConflict");
	}
}
