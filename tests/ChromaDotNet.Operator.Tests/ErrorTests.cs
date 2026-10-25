using System.Net;
using ChromaDotNet.Operator.Controllers;
using ChromaDotNet.Operator.Entities;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// Errors that are not a refusal of the API: an answer like 500, or no answer at all, like a network failure. The pass does not throw,
/// since KubeOps would drop the server after a few retries: it comes back with a growing delay, and the condition Reconciled says why.
/// </summary>
public class ErrorTests
{
	private IKubernetesClient _client = null!;
	private V1Alpha1Chroma _server = null!;
	private V1Alpha1Chroma.ChromaStatus? _written;
	// Each status written, as it was at the call: the controller can change the same object after.
	private readonly List<V1Alpha1Chroma.ChromaStatus> _writes = [];

	[SetUp]
	public void SetUp()
	{
		_server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Image159);
		_server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		_server.Kind = "Chroma";
		_server.Metadata.Uid = "uid-" + Guid.NewGuid().ToString("N");
		_client = Substitute.For<IKubernetesClient>();
		_client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.ConfigMap(_server));
		_client.GetAsync<V1Service>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Service(_server));
		_client.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Pvc(_server));
		_writes.Clear();
		_client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			_written = call.Arg<V1Alpha1Chroma>().Status;
			_writes.Add(k8s.KubernetesJson.Deserialize<V1Alpha1Chroma.ChromaStatus>(k8s.KubernetesJson.Serialize(_written)));
			return call.Arg<V1Alpha1Chroma>();
		});
	}

	[TearDown]
	public void TearDown() => _client.Dispose();

	[Test]
	public async Task NetworkFailureComesBackWithTheReason()
	{
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
		var result = await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(result.RequeueAfter, Is.Not.Null);
		var reconciled = _written?.Conditions.SingleOrDefault(c => c.Type == "Reconciled");
		Assert.That(reconciled?.Status, Is.EqualTo("False"));
		Assert.That(reconciled?.Reason, Is.EqualTo("Error"));
		Assert.That(reconciled?.Message, Does.Contain("connection refused"));
	}

	[Test]
	public async Task ServerErrorComesBackWithTheReason()
	{
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpOperationException("Internal")
		{
			Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.InternalServerError), "etcdserver: request timed out"),
		});
		var result = await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(result.RequeueAfter, Is.Not.Null);
		var reconciled = _written?.Conditions.SingleOrDefault(c => c.Type == "Reconciled");
		Assert.That(reconciled?.Reason, Is.EqualTo("ApiError"));
		Assert.That(reconciled?.Message, Does.Contain("500").And.Contain("etcdserver: request timed out"));
	}

	// The delay grows with each failure in a row, up to a limit, and a pass that succeeds starts it again.
	[Test]
	public async Task DelayGrowsAndResets()
	{
		var failing = true;
		var pod = RunningPod(_server, Image159);
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns(_ => failing ? throw new HttpRequestException("connection refused") : pod);
		var delays = new List<TimeSpan?>();
		for (var i = 0; i < 8; i++)
			delays.Add((await Controller().ReconcileAsync(_server, CancellationToken.None)).RequeueAfter);
		Assert.That(delays.Take(3), Is.EqualTo(new TimeSpan?[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40) }));
		Assert.That(delays.Max(), Is.EqualTo(TimeSpan.FromMinutes(1)), "the resync gives a pass within a minute");

		failing = false;
		_server.Status = Reconciliation.Decide(_server, pod, null, Now, Defaults).Status;
		Assert.That((await Controller().ReconcileAsync(_server, CancellationToken.None)).RequeueAfter, Is.EqualTo(Reconciliation.SteadyRequeue));
		failing = true;
		Assert.That((await Controller().ReconcileAsync(_server, CancellationToken.None)).RequeueAfter, Is.EqualTo(TimeSpan.FromSeconds(10)));
	}

	// After a failure, the next pass that goes through sets Reconciled back to True.
	[Test]
	public async Task PassThatGoesThroughClearsTheReason()
	{
		var failing = true;
		var pod = RunningPod(_server, Image159);
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns(_ => failing ? throw new HttpRequestException("connection refused") : pod);
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		_server.Status = _written!;
		failing = false;
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		var reconciled = _written!.Conditions.Single(c => c.Type == "Reconciled");
		Assert.That((reconciled.Status, reconciled.Reason), Is.EqualTo(("True", "Succeeded")));
		Assert.That(_written.Conditions.Single(c => c.Type == "Ready").Status, Is.EqualTo("True"));
	}

	// The pass may have written the server, so its own copy is out of date: the reason goes on the server as it is now.
	[Test]
	public async Task FailureIsWrittenOnTheServerAsItIsNow()
	{
		var current = k8s.KubernetesJson.Deserialize<V1Alpha1Chroma>(k8s.KubernetesJson.Serialize(_server));
		current.Metadata.ResourceVersion = "99";
		_client.GetAsync<V1Alpha1Chroma>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(current);
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		await _client.Received().UpdateStatusAsync(Arg.Is<V1Alpha1Chroma>(s => s.Metadata.ResourceVersion == "99"
			&& s.Status.Conditions.Any(c => c.Type == "Reconciled" && c.Status == "False")), Arg.Any<CancellationToken>());
	}

	// A failure that lasts, here the creation of the Pod: Reconciled stays False, without going to True at each pass.
	[Test]
	public async Task LastingFailureDoesNotSetSucceeded()
	{
		_server.Status!.Conditions.Add(new V1Condition { Type = "Reconciled", Status = "False", Reason = "ApiError", Message = "", LastTransitionTime = Now });
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		_client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpOperationException("Internal")
		{
			Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.InternalServerError), "etcdserver: request timed out"),
		});
		for (var i = 0; i < 2; i++)
			await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(_writes, Is.Not.Empty);
		Assert.That(_writes.SelectMany(w => w.Conditions).Where(c => c.Type == "Reconciled").Select(c => c.Status), Is.All.EqualTo("False"));
	}

	// A server deleted after failures leaves nothing behind: one created again with the same UID would start at 10 seconds.
	[Test]
	public async Task DeletionForgetsTheFailures()
	{
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		await Controller().DeletedAsync(_server, CancellationToken.None);
		Assert.That((await Controller().ReconcileAsync(_server, CancellationToken.None)).RequeueAfter, Is.EqualTo(TimeSpan.FromSeconds(10)));
	}

	// A pass that fails is a pass for the liveness too: the queue is not stuck.
	[Test]
	public async Task FailedPassCountsForTheLiveness()
	{
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
		var liveness = new Liveness();
		liveness.Listed([_server], DateTime.UtcNow - TimeSpan.FromMinutes(10));
		Assert.That(liveness.Stuck(leading: true, DateTime.UtcNow), Is.Not.Null);
		await new ChromaController(_client, Defaults, NullLogger<ChromaController>.Instance, liveness: liveness).ReconcileAsync(_server, CancellationToken.None);
		Assert.That(liveness.Stuck(leading: true, DateTime.UtcNow), Is.Null);
	}

	private ChromaController Controller() => new(_client, Defaults, NullLogger<ChromaController>.Instance);
}
