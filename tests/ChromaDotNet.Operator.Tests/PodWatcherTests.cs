using System.Runtime.CompilerServices;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.LeaderElection;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Queue;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The watch of the Pods starts again when it fails or ends, and a change of a Pod puts its ChromaServer in the queue.</summary>
public class PodWatcherTests
{
	[TestCase(true)]
	[TestCase(false)]
	[CancelAfter(15_000)]
	public async Task WatchStartsAgainAndQueuesTheServer(bool fails)
	{
		using var client = Substitute.For<IKubernetesClient>();
		var server = Server();
		var pod = RunningPod(server, Image159);
		var watches = 0;
		client.WatchAsync<V1Pod>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
			.Returns(call => ++watches == 1 ? First(fails) : Then(pod, call.Arg<CancellationToken>()));
		client.GetAsync<V1Alpha1ChromaServer>("demo", "rag", Arg.Any<CancellationToken>()).Returns(server);
		var queued = new TaskCompletionSource<V1Alpha1ChromaServer>(TaskCreationOptions.RunContinuationsAsynchronously);
		EntityQueue<V1Alpha1ChromaServer> queue = (entity, _, _, queueIn, _, _) =>
		{
			Assert.That(queueIn, Is.EqualTo(TimeSpan.Zero));
			queued.TrySetResult(entity);
			return Task.FromResult(true);
		};

		using var watcher = new PodWatcher(client, queue, NullLogger<PodWatcher>.Instance);
		await watcher.StartAsync(CancellationToken.None);
		Assert.That(await queued.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(server));
		await watcher.StopAsync(CancellationToken.None);
		Assert.That(watches, Is.EqualTo(2));
		client.Received().WatchAsync<V1Pod>(Arg.Any<string?>(), Arg.Any<string?>(), "app.kubernetes.io/managed-by=chroma-operator", Arg.Any<string?>(),
			Arg.Any<bool?>(), Arg.Any<CancellationToken>());
	}

	// With leader election, a replica that is not the leader does not watch the Pods: its queue takes nothing.
	[Test]
	[CancelAfter(15_000)]
	public async Task ReplicaThatIsNotTheLeaderDoesNotWatch()
	{
		using var client = Substitute.For<IKubernetesClient>();
		using var elector = new LeaderElector(new LeaderElectionConfig(Substitute.For<ILock>()));
		EntityQueue<V1Alpha1ChromaServer> queue = (_, _, _, _, _, _) => Task.FromResult(true);
		using var watcher = new PodWatcher(client, queue, NullLogger<PodWatcher>.Instance, elector);
		await watcher.StartAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromSeconds(1));
		await watcher.StopAsync(CancellationToken.None);
		client.DidNotReceive().WatchAsync<V1Pod>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
			Arg.Any<CancellationToken>());
	}

	/// <summary>A watch that fails, as with a timeout, or that the API ends.</summary>
	private static async IAsyncEnumerable<(WatchEventType, V1Pod)> First(bool fails)
	{
		await Task.Yield();
		foreach (var none in Array.Empty<(WatchEventType, V1Pod)>())
			yield return none;
		if (fails)
			throw new HttpRequestException("the watch ended");
	}

	private static async IAsyncEnumerable<(WatchEventType, V1Pod)> Then(V1Pod pod, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		yield return (WatchEventType.Deleted, pod);
		await Task.Delay(Timeout.Infinite, cancellationToken);
	}
}
