using ChromaDotNet.Operator.Entities;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>Every Chroma goes into the queue once a minute, from a fresh list: a pass also when the queue lost a change.</summary>
public class ResyncTests
{
	[Test]
	public async Task EveryChromaGoesIntoTheQueue()
	{
		using var client = Substitute.For<IKubernetesClient>();
		var deleting = NewChroma();
		deleting.Metadata.Name = "going";
		deleting.Metadata.DeletionTimestamp = Now;
		client.ListAsync<V1Alpha1Chroma>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns((IList<V1Alpha1Chroma>)[NewChroma(), deleting]);
		var queued = new List<(string Name, ReconciliationType Type, TimeSpan In)>();
		var resync = new Resync(client, (entity, type, _, queueIn, _, _) =>
		{
			queued.Add((entity.Name(), type, queueIn));
			return Task.FromResult(true);
		}, NullLogger<Resync>.Instance);
		await resync.ResyncOnceAsync(CancellationToken.None);
		Assert.That(queued, Is.EqualTo(new[] { ("demo", ReconciliationType.Modified, Resync.Period) }), "not the one being deleted");
	}

	// The queue of KubeOps refuses an entry while its intake is suspended, and logs it at Trace only.
	[Test]
	public async Task EntryTheQueueRefusesIsAWarning()
	{
		using var client = Substitute.For<IKubernetesClient>();
		client.ListAsync<V1Alpha1Chroma>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns((IList<V1Alpha1Chroma>)[NewChroma()]);
		var logger = new ListLogger<Resync>();
		await new Resync(client, (_, _, _, _, _, _) => Task.FromResult(false), logger).ResyncOnceAsync(CancellationToken.None);
		Assert.That(logger.Events, Is.EqualTo(new[] { 17 }));
	}
}
