using ChromaDotNet.Operator.Entities;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator.Controllers;

/// <summary>
/// Applies pvcDeletionPolicy Retain to the claim before the ChromaServer goes. The controller applies the policy at every pass, but a
/// server changed to Retain and deleted before the next pass, or while the operator was stopped, still owned its claim: the garbage
/// collector deleted the claim with it, and with the reclaim policy Delete the volume too. With this finalizer the ChromaServer stays
/// until the operator has removed it from the owners of the claim.
/// <para>
/// The controller puts the finalizer on a ChromaServer while the server owns its claim, which it does with Delete: added before the
/// owner reference, removed after it. While the operator is not running, a deleted ChromaServer that owns its claim waits, and so does a
/// namespace being deleted with it; one that owns nothing is deleted at once. With Delete the finalizer leaves the claim as the passes
/// made it: it never makes the server an owner, so that a deletion that orphans the dependents (--cascade=orphan) keeps the claim, and
/// a change to Delete that no pass applied yet keeps it too, with a line in the log.
/// </para>
/// </summary>
public sealed class ClaimFinalizer(IKubernetesClient client, ILogger<ClaimFinalizer> logger) : IEntityFinalizer<V1Alpha1ChromaServer>
{
	/// <summary>The name KubeOps gives the finalizer: the group of the entity and the name of the class.</summary>
	public const string Identifier = Labels.Group + "/claimfinalizer";

	public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

	public async Task<ReconciliationResult<V1Alpha1ChromaServer>> FinalizeAsync(V1Alpha1ChromaServer server, CancellationToken cancellationToken)
	{
		var serverName = server.Name();
		var name = Resources.InstanceName(serverName);
		try
		{
			var claim = await client.GetAsync<V1PersistentVolumeClaim>(name, server.Namespace(), cancellationToken);
			var owned = Ownership.OwnsClaim(claim, server);
			if (server.Spec.PvcDeletionPolicy == "Delete")
			{
				// Only the claim of this server: a claim with its name that belongs to another workload is none of its business.
				if (claim is not null && !owned && Ownership.ClaimConflict(claim, server) is null)
					Log.DeleteNotApplied(logger, serverName, name);
				return ReconciliationResult<V1Alpha1ChromaServer>.Success(server);
			}
			if (!owned)
				return ReconciliationResult<V1Alpha1ChromaServer>.Success(server);
			var (_, conflict, _) = await ApiWrites.UpdateClaimAsync(client, server, claim!, c => Ownership.ReconcileClaimOwner(c, server), cancellationToken);
			if (conflict is null)
			{
				Log.ClaimReleased(logger, serverName, name);
				return ReconciliationResult<V1Alpha1ChromaServer>.Success(server);
			}
			// A claim deleted meanwhile, or no longer owned by the server, needs nothing more.
			if (!Ownership.OwnsClaim(await client.GetAsync<V1PersistentVolumeClaim>(name, server.Namespace(), cancellationToken), server))
				return ReconciliationResult<V1Alpha1ChromaServer>.Success(server);
			var retry = RetryAfter.TotalSeconds;
			Log.ClaimNotReleased(logger, null, serverName, retry, conflict);
			return ReconciliationResult<V1Alpha1ChromaServer>.Failure(server, conflict, requeueAfter: RetryAfter);
		}
		catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			// Any failure, also of the network, keeps the ChromaServer and comes back: an exception that left this method would be retried
			// a few times and then dropped, and the ChromaServer would wait until the operator restarts.
			var message = e.Message;
			var retry = RetryAfter.TotalSeconds;
			Log.ClaimNotReleased(logger, e, serverName, retry, message);
			return ReconciliationResult<V1Alpha1ChromaServer>.Failure(server, message, e, RetryAfter);
		}
	}
}
