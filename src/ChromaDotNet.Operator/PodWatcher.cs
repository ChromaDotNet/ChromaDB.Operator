using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.LeaderElection;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator;

/// <summary>
/// Watches the Pods of the operator and puts their Chroma in the queue at once when one changes: a Pod deleted, ready, or
/// failing. The watcher of KubeOps follows the Chroma resources only, and the periodic pass comes every 30 seconds when the server is
/// ready: without this watch, a Pod deleted by hand stayed missing for up to that long. With leader election only the leader watches:
/// the queue of another replica takes nothing, and the watch would only load the API.
/// </summary>
internal sealed partial class PodWatcher(IKubernetesClient client, EntityQueue<V1Alpha1Chroma> queue, ILogger<PodWatcher> logger,
	LeaderElector? elector = null)
	: BackgroundService
{
	private static readonly string Selector = $"app.kubernetes.io/managed-by={Labels.ManagedBy}";
	private static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			if (elector?.IsLeader() == false)
			{
				await Task.Delay(Pause, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
				continue;
			}
			// The watch ends when the leadership goes.
			using var leading = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			void Stop() => leading.Cancel();
			if (elector is not null)
				elector.OnStoppedLeading += Stop;
			try
			{
				await foreach (var (_, pod) in client.WatchAsync<V1Pod>(labelSelector: Selector, cancellationToken: leading.Token))
				{
					// The server puts back a label removed or changed by hand at once.
					var name = ChromaOf(pod);
					if (name is null)
						continue;
					var server = await client.GetAsync<V1Alpha1Chroma>(name, pod.Metadata.NamespaceProperty, leading.Token);
					if (server is not null)
						await queue(server, ReconciliationType.Modified, ReconciliationTriggerSource.Operator, TimeSpan.Zero, 0, leading.Token);
				}
			}
			catch (OperationCanceledException) when (leading.IsCancellationRequested)
			{
				// The operator stops, or another replica leads now.
			}
			catch (Exception e) when (!stoppingToken.IsCancellationRequested)
			{
				// Any failure of the watch, a timeout or an empty answer among them, starts it again: an exception that left this method
				// would stop the operator.
				WatchFailed(logger, e);
				await Task.Delay(Pause, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
			}
			finally
			{
				if (elector is not null)
					elector.OnStoppedLeading -= Stop;
			}
		}
	}

	/// <summary>
	/// The Chroma of a Pod: its controller, which a label changed by hand cannot alter, of this group only (another operator can have a
	/// kind Chroma); the label only without one.
	/// </summary>
	public static string? ChromaOf(V1Pod pod) =>
		pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == V1Alpha1Chroma.KindName
			&& o.ApiVersion?.StartsWith(Labels.Group + "/", StringComparison.Ordinal) == true)?.Name
		?? (pod.Metadata.Labels?.TryGetValue(Labels.Chroma, out var label) == true ? label : null);

	[LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "The watch of the Pods ended; it starts again in 2 seconds.")]
	private static partial void WatchFailed(ILogger logger, Exception exception);
}
