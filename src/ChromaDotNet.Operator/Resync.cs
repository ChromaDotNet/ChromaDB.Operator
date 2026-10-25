using ChromaDotNet.Operator.Entities;
using k8s.LeaderElection;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator;

/// <summary>
/// Puts every Chroma in the queue once a minute, from a list read again from the API, for a pass within a minute: a pass for each, also
/// when the queue of KubeOps has lost one. On AKS a change of the spec once reached the watch and was never reconciled, and the periodic
/// passes stopped with it, until the operator restarted. The queue keeps the earlier of an entry and the one already scheduled, and every
/// Chroma has one within a minute (its requeue, or the retry after a failure), so the resync adds no pass and shortens no retry. Only the
/// leader puts them in the queue: the queue of another replica takes nothing.
/// </summary>
internal sealed partial class Resync(IKubernetesClient client, EntityQueue<V1Alpha1Chroma> queue, ILogger<Resync> logger,
	LeaderElector? elector = null, Liveness? liveness = null)
	: BackgroundService
{
	public static readonly TimeSpan Period = TimeSpan.FromSeconds(60);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(Period);
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			try
			{
				await ResyncOnceAsync(stoppingToken);
			}
			catch (Exception e) when (!stoppingToken.IsCancellationRequested)
			{
				// The next minute tries again: an exception that left this method would stop the operator.
				ResyncFailed(logger, e);
			}
		}
	}

	/// <summary>Every Chroma of the cluster in the queue, when this replica leads.</summary>
	internal async Task ResyncOnceAsync(CancellationToken cancellationToken)
	{
		if (elector?.IsLeader() == false)
			return;
		var chromas = (await client.ListAsync<V1Alpha1Chroma>(cancellationToken: cancellationToken)).Where(c => c.Metadata.DeletionTimestamp is null).ToList();
		liveness?.Listed(chromas, DateTime.UtcNow);
		foreach (var chroma in chromas)
		{
			// The queue refuses an entry while its intake is suspended, and says so only at Trace: the warning keeps it.
			if (!await queue(chroma, ReconciliationType.Modified, ReconciliationTriggerSource.Operator, Period, 0, cancellationToken))
				QueueRefused(logger, chroma.Name(), chroma.Namespace());
		}
	}

	[LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "The queue of KubeOps did not take the Chroma {Name} in {Namespace} from the resync.")]
	private static partial void QueueRefused(ILogger logger, string name, string @namespace);

	[LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "The resync of the Chroma resources failed; it runs again in a minute.")]
	private static partial void ResyncFailed(ILogger logger, Exception exception);
}
