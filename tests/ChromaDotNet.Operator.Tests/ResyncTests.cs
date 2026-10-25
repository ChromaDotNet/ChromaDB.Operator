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

	private static readonly DateTime T0 = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

	private static V1Alpha1Chroma Named(string name)
	{
		var chroma = NewChroma();
		chroma.Metadata.Name = name;
		chroma.Metadata.Uid = "uid-" + name;
		return chroma;
	}

	// KubeOps reconciles one entity at a time under a lock of its own: one Chroma is stuck also while the others pass.
	[Test]
	public void OneChromaWithoutPassesIsStuckWhileOthersPass()
	{
		var (stuck, passing) = (Named("stuck"), Named("passing"));
		var liveness = new Liveness();
		liveness.Listed([stuck, passing], T0);
		for (var minute = 1; minute <= 6; minute++)
			liveness.PassDone(passing, T0 + TimeSpan.FromMinutes(minute));
		var now = T0 + TimeSpan.FromMinutes(6);
		Assert.Multiple(() =>
		{
			Assert.That(liveness.Stuck(leading: true, now), Does.StartWith("rag/stuck"));
			Assert.That(liveness.Stuck(leading: false, now), Is.Null, "a replica that does not lead reconciles nothing");
		});
	}

	[Test]
	public void FiveMinutesWithoutPassesAreNotYetStuck()
	{
		var liveness = new Liveness();
		liveness.Listed([Named("a")], T0);
		Assert.Multiple(() =>
		{
			Assert.That(liveness.Stuck(leading: true, T0 + Liveness.Threshold), Is.Null);
			Assert.That(liveness.Stuck(leading: true, T0 + Liveness.Threshold + TimeSpan.FromSeconds(1)), Is.Not.Null);
		});
	}

	// A Chroma counts from its first listing, and no more once a resync does not list it, as after its deletion.
	[Test]
	public void ChromaCountsWhileTheResyncListsIt()
	{
		var liveness = new Liveness();
		liveness.Listed([Named("gone")], T0);
		liveness.Listed([Named("new")], T0 + TimeSpan.FromMinutes(4));
		Assert.Multiple(() =>
		{
			Assert.That(liveness.Stuck(leading: true, T0 + TimeSpan.FromMinutes(6)), Is.Null);
			Assert.That(liveness.Stuck(leading: true, T0 + TimeSpan.FromMinutes(10)), Does.StartWith("rag/new"));
		});
	}

	// A replica that starts leading has had no pass yet on the Chroma resources it listed while it led before.
	[Test]
	public void LeadingAgainCountsFromThen()
	{
		var liveness = new Liveness();
		liveness.Listed([Named("a")], T0);
		liveness.StartedLeading(T0 + TimeSpan.FromMinutes(4));
		Assert.That(liveness.Stuck(leading: true, T0 + TimeSpan.FromMinutes(6)), Is.Null);
	}
}
