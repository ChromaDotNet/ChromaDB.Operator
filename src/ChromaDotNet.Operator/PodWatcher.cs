using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChromaDotNet.Operator;

/// <summary>
/// Watches the Pods of the operator and puts their ChromaServer in the queue at once when one changes: a Pod deleted, ready, or
/// failing. The watcher of KubeOps follows the ChromaServers only, and the periodic pass comes every 30 seconds when the server is
/// ready: without this watch, a Pod deleted by hand stayed missing for up to that long.
/// </summary>
internal sealed partial class PodWatcher(IKubernetesClient client, EntityQueue<V1Alpha1ChromaServer> queue, ILogger<PodWatcher> logger)
	: BackgroundService
{
	private static readonly string Selector = $"app.kubernetes.io/managed-by={Labels.ManagedBy}";

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await foreach (var (_, pod) in client.WatchAsync<V1Pod>(labelSelector: Selector, cancellationToken: stoppingToken))
				{
					// The server puts back a label removed or changed by hand at once.
					var name = ServerOf(pod);
					if (name is null)
						continue;
					var server = await client.GetAsync<V1Alpha1ChromaServer>(name, pod.Metadata.NamespaceProperty, stoppingToken);
					if (server is not null)
						await queue(server, ReconciliationType.Modified, ReconciliationTriggerSource.Operator, TimeSpan.Zero, 0, stoppingToken);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception e) when (!stoppingToken.IsCancellationRequested)
			{
				// Any failure of the watch, a timeout or an empty answer among them, starts it again: an exception that left this method
				// would stop the operator.
				WatchFailed(logger, e);
				await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
			}
		}
	}

	/// <summary>The ChromaServer of a Pod: its controller, which a label changed by hand cannot alter; the label only without one.</summary>
	public static string? ServerOf(V1Pod pod) =>
		pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "ChromaServer")?.Name
		?? (pod.Metadata.Labels?.TryGetValue(Labels.Server, out var label) == true ? label : null);

	[LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "The watch of the Pods ended; it starts again in 2 seconds.")]
	private static partial void WatchFailed(ILogger logger, Exception exception);
}
