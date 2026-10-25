using System.Collections.Concurrent;
using ChromaDotNet.Operator.Entities;
using k8s.LeaderElection;
using k8s.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChromaDotNet.Operator;

/// <summary>
/// The liveness of the queue, for each Chroma: every Chroma gets a pass at least once a minute (its own requeue, or <see cref="Resync"/>),
/// so a leader with a Chroma that has had no pass for 5 minutes is stuck. /healthz then fails, and the kubelet restarts the operator:
/// on AKS a stuck queue of KubeOps took up again only after a restart. Each Chroma counts on its own, since KubeOps reconciles one entity
/// at a time under a lock of its own: one stuck Chroma is stuck also while the others pass. A replica that does not lead reconciles
/// nothing, and stays live.
/// </summary>
public sealed class Liveness(LeaderElector? elector = null) : IHealthCheck
{
	public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

	private readonly record struct Entry(string Name, DateTime LastPass);

	/// <summary>By UID, the Chroma resources of the last resync, with the end of their last pass, or the moment they were listed first.</summary>
	private readonly ConcurrentDictionary<string, Entry> _chromas = new();

	/// <summary>A pass ended, successful or not.</summary>
	public void PassDone(V1Alpha1Chroma chroma, DateTime now)
	{
		if (chroma.Uid() is { } uid)
			_chromas[uid] = new(Name(chroma), now);
	}

	/// <summary>The Chroma resources of a resync: a new one counts from now, and one that is gone counts no more.</summary>
	public void Listed(IReadOnlyCollection<V1Alpha1Chroma> chromas, DateTime now)
	{
		var uids = chromas.Select(c => c.Uid()).OfType<string>().ToHashSet();
		foreach (var uid in _chromas.Keys.Where(u => !uids.Contains(u)))
			_chromas.TryRemove(uid, out _);
		foreach (var chroma in chromas.Where(c => c.Uid() is not null))
			_chromas.TryAdd(chroma.Uid(), new(Name(chroma), now));
	}

	/// <summary>A replica that starts leading has had no pass yet: the time without passes counts from now.</summary>
	public void StartedLeading(DateTime now)
	{
		foreach (var (uid, entry) in _chromas)
			_chromas[uid] = entry with { LastPass = now };
	}

	/// <summary>The Chroma without a pass for longer than <see cref="Threshold"/>, or null.</summary>
	public string? Stuck(bool leading, DateTime now) =>
		leading ? _chromas.Values.Where(e => now - e.LastPass > Threshold).OrderBy(e => e.LastPass).Select(e => $"{e.Name}, since {e.LastPass:O}").FirstOrDefault() : null;

	public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
	{
		var stuck = Stuck(elector?.IsLeader() != false, DateTime.UtcNow);
		return Task.FromResult(stuck is null
			? HealthCheckResult.Healthy()
			: HealthCheckResult.Unhealthy($"No pass on the Chroma {stuck}: the queue is stuck, and a restart takes it up again."));
	}

	private static string Name(V1Alpha1Chroma chroma) => $"{chroma.Namespace()}/{chroma.Name()}";
}
