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
/// The controller against a fake client: what happens when the claim cannot be written because another writer changed it (409).
/// The owned Pod was edited by hand from 1.5.3 to 1.5.9 and the spec still says 1.5.3, so the server is blocked and keeps the Pod.
/// </summary>
public class ControllerTests
{
	private IKubernetesClient _client = null!;
	private V1Alpha1ChromaServer _server = null!;
	private V1PersistentVolumeClaim _claim = null!;
	private V1Alpha1ChromaServer.ServerStatus? _written;

	[SetUp]
	public void SetUp()
	{
		_server = Server(Image153, dataVersion: "1.5.3", runningImage: Image153);
		_server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		_server.Kind = "ChromaServer";
		_claim = Resources.Pvc(_server);
		_claim.Metadata.Annotations = new Dictionary<string, string> { [Labels.ClaimDataVersion] = "1.5.3" };
		var pod = RunningPod(_server, Image153);
		pod.Spec.Containers[0].Image = Image159;
		pod.Status.ContainerStatuses[0].Image = Image159;

		_client = Substitute.For<IKubernetesClient>();
		_client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.ConfigMap(_server));
		_client.GetAsync<V1Service>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Service(_server));
		_client.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => Copy(_claim));
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pod);
		_client.UpdateStatusAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			var server = call.Arg<V1Alpha1ChromaServer>();
			_written = server.Status;
			return server;
		});
	}

	[TearDown]
	public void TearDown() => _client.Dispose();

	// The first update of the claim conflicts; the controller reads it again and writes it, then the status.
	[Test]
	public async Task ClaimConflictIsRetriedWithAFreshClaim()
	{
		var attempts = 0;
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			if (++attempts == 1)
				throw Conflict();
			_claim = call.Arg<V1PersistentVolumeClaim>();
			return _claim;
		});
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(attempts, Is.EqualTo(2));
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.5.9"));
		Assert.That(_written?.DataVersion, Is.EqualTo("1.5.9"));
		await _client.DidNotReceive().CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
	}

	// The claim keeps conflicting: the status still gets 1.5.9, the server fails with the reason, and nothing is done to the Pod.
	[Test]
	public async Task ClaimThatKeepsConflictingStillRecordsTheVersionInTheStatus()
	{
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Throws(Conflict());
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(_written?.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(_written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("PersistentVolumeClaimConflict"));
		await _client.DidNotReceive().CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
		Assert.That(PodDeletes(_client), Is.Zero);
	}

	// During the conflict another writer recorded 1.6.0 on the claim: the retry does not lower it to the 1.5.9 seen here, and the pass
	// stops without touching the Pod, so that the next pass decides from the fresh claim.
	[Test]
	public async Task RetryDoesNotLowerAVersionWrittenMeanwhile()
	{
		var attempts = 0;
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			if (++attempts == 1)
			{
				_claim.Metadata.Annotations[Labels.ClaimDataVersion] = "1.6.0";
				throw Conflict();
			}
			_claim = call.Arg<V1PersistentVolumeClaim>();
			return _claim;
		});
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.6.0"));
		Assert.That(_written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("PersistentVolumeClaimConflict"));
		await _client.DidNotReceive().CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
	}

	// During the conflict another writer recorded an incident on the claim: the retry keeps it.
	[Test]
	public async Task RetryKeepsAnIncidentWrittenMeanwhile()
	{
		var attempts = 0;
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			if (++attempts == 1)
			{
				_claim.Metadata.Annotations[Labels.ClaimDataVersionUnknown] = "c0ffee00";
				throw Conflict();
			}
			_claim = call.Arg<V1PersistentVolumeClaim>();
			return _claim;
		});
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersionUnknown], Is.EqualTo("c0ffee00"));
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.5.9"), "the version seen here is still written");
		Assert.That(_written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("PersistentVolumeClaimConflict"));
	}

	// A ConfigMap update that conflicts: the pass still records 1.5.9 on the claim and in the status, and does nothing to the Pod.
	[Test]
	public async Task ConfigMapConflictStillRecordsWhatThePodShows()
	{
		var stale = Resources.ConfigMap(_server);
		stale.Data[Resources.ConfigFile] = "persist_path: \"/data\"\n";
		_client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(stale);
		_client.UpdateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Throws(Conflict());
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			_claim = call.Arg<V1PersistentVolumeClaim>();
			return _claim;
		});
		await Controller().ReconcileAsync(_server, CancellationToken.None);
		Assert.That(_written?.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.5.9"));
		Assert.That(_written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("ConfigMapConflict"));
		await _client.DidNotReceive().CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
	}

	// A server without an image gets the default, 1.5.3 here, and the same pass still records the 1.5.9 its Pod shows.
	[Test]
	public async Task DefaultImageStillRecordsWhatThePodShows()
	{
		var options = new OperatorOptions(Image153);
		_server.Spec.Image = null;
		_client.UpdateAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1ChromaServer>());
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			_claim = call.Arg<V1PersistentVolumeClaim>();
			return _claim;
		});
		await new ChromaServerController(_client, options, NullLogger<ChromaServerController>.Instance).ReconcileAsync(_server, CancellationToken.None);
		await _client.Received().UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Spec.Image == Image153), Arg.Any<CancellationToken>());
		Assert.That(_written?.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(_claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.5.9"));
	}

	private ChromaServerController Controller() => new(_client, Defaults, NullLogger<ChromaServerController>.Instance);

	private static V1PersistentVolumeClaim Copy(V1PersistentVolumeClaim claim) =>
		k8s.KubernetesJson.Deserialize<V1PersistentVolumeClaim>(k8s.KubernetesJson.Serialize(claim));

	private static HttpOperationException Conflict() => new("Conflict")
	{
		Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), "the object has been modified"),
	};
}
