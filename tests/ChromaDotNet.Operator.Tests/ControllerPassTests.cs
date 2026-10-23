using System.Net;
using ChromaDotNet.Operator.Controllers;
using ChromaDotNet.Operator.Entities;
using k8s;
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
/// The controller against a fake client in the passes without conflicts: a new server creates its objects, a server in its steady state
/// writes nothing, and a new image replaces the Pod after the API has checked the new one, deleting only the Pod that was read.
/// </summary>
public class ControllerPassTests
{
	private IKubernetesClient _client = null!;
	private ICoreV1Operations _core = null!;

	[SetUp]
	public void SetUp()
	{
		_client = Substitute.For<IKubernetesClient>();
		_core = _client.ApiClient.CoreV1;
		_client.CreateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1ConfigMap>());
		_client.CreateAsync(Arg.Any<V1Service>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Service>());
		_client.CreateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		_client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Pod>());
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		_client.UpdateStatusAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1ChromaServer>());
		_core.CreateNamespacedPodWithHttpMessagesAsync(Arg.Any<V1Pod>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
				Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.Returns(call => new HttpOperationResponse<V1Pod> { Body = call.Arg<V1Pod>() });
		_core.DeleteNamespacedPodWithHttpMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
				Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.Returns(new HttpOperationResponse<V1Pod>());
	}

	[TearDown]
	public void TearDown() => _client.Dispose();

	// Nothing exists yet: ConfigMap, Service, claim and Pod are created, and the data version is on the claim and in the status before
	// the Pod is created.
	[Test]
	public async Task NewServerCreatesItsObjects()
	{
		var server = Named(Server());
		await Controller().ReconcileAsync(server, CancellationToken.None);
		await _client.Received(1).CreateAsync(Arg.Is<V1ConfigMap>(c => c.Name() == "demo-config"), Arg.Any<CancellationToken>());
		await _client.Received(1).CreateAsync(Arg.Is<V1Service>(s => s.Name() == "demo-rw"), Arg.Any<CancellationToken>());
		await _client.Received(1).CreateAsync(Arg.Is<V1PersistentVolumeClaim>(c => c.Name() == "demo-1"), Arg.Any<CancellationToken>());
		Received.InOrder(() =>
		{
			_client.UpdateAsync(Arg.Is<V1PersistentVolumeClaim>(c => c.Metadata.Annotations[Labels.ClaimDataVersion] == "1.5.9"), Arg.Any<CancellationToken>());
			_client.UpdateStatusAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Status.DataVersion == "1.5.9" && s.Status.Phase == "Creating"), Arg.Any<CancellationToken>());
			_client.CreateAsync(Arg.Is<V1Pod>(p => p.Name() == "demo-1" && p.Spec.Containers[0].Image == Image159), Arg.Any<CancellationToken>());
		});
	}

	// Everything as the operator wants it: the pass reads and writes nothing, the status included.
	[Test]
	public async Task SteadyServerWritesNothing()
	{
		var server = Named(Server(Image159, dataVersion: "1.5.9", runningImage: Image159));
		var pod = Existing(server, Image159, "1.5.9");
		server.Status = Reconciliation.Decide(server, pod, "1.5.9", Now, Defaults).Status;
		var result = await Controller().ReconcileAsync(server, CancellationToken.None);
		Assert.That(result.RequeueAfter, Is.EqualTo(Reconciliation.SteadyRequeue));
		Assert.That(_client.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Where(n => n != nameof(IKubernetesClient.GetAsync) && n != "get_ApiClient"), Is.Empty);
		Assert.That(_core.ReceivedCalls(), Is.Empty);
	}

	// The same objects, but the status written earlier differs from what the pass decides: only the status is written.
	[Test]
	public async Task DifferentStatusIsWritten()
	{
		var server = Named(Server(Image159, dataVersion: "1.5.9", runningImage: Image159));
		var pod = Existing(server, Image159, "1.5.9");
		server.Status = Reconciliation.Decide(server, pod, "1.5.9", Now, Defaults).Status;
		server.Status.Phase = "Updating";
		await Controller().ReconcileAsync(server, CancellationToken.None);
		await _client.Received(1).UpdateStatusAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Status.Phase == "Ready"), Arg.Any<CancellationToken>());
		Assert.That(_client.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
			.Where(n => n is not (nameof(IKubernetesClient.GetAsync) or "get_ApiClient" or nameof(IKubernetesClient.UpdateStatusAsync))), Is.Empty);
	}

	// 1.5.3 runs, the spec asks for 1.5.9: the API checks the new Pod without creating it, then the Pod that was read is deleted, by
	// UID and resource version. The next pass creates the new one.
	[Test]
	public async Task NewImageDeletesThePodThatWasRead()
	{
		var server = Named(Server(Image159, dataVersion: "1.5.3", runningImage: Image153));
		Existing(server, Image153, "1.5.3");
		await Controller().ReconcileAsync(server, CancellationToken.None);
		await _core.Received(1).CreateNamespacedPodWithHttpMessagesAsync(Arg.Is<V1Pod>(p => p.Name() == "demo-1-check" && p.Spec.Containers[0].Image == Image159),
			"rag", "All", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
		await _core.Received(1).DeleteNamespacedPodWithHttpMessagesAsync("demo-1", "rag",
			Arg.Is<V1DeleteOptions>(o => o.Preconditions.Uid == "pod-1" && o.Preconditions.ResourceVersion == "41"),
			Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
			Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
		Assert.That(PodDeletes(_client), Is.EqualTo(1), "what the other tests count");
		await _client.DidNotReceive().CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
	}

	// The API refuses the new Pod (422): the running one stays, and the server fails with the explanation.
	[Test]
	public async Task RefusedReplacementKeepsThePod()
	{
		var server = Named(Server(Image159, dataVersion: "1.5.3", runningImage: Image153));
		Existing(server, Image153, "1.5.3");
		_core.CreateNamespacedPodWithHttpMessagesAsync(Arg.Any<V1Pod>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
				Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.ThrowsAsync(new HttpOperationException("Unprocessable")
			{
				Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity), "requests must not exceed limits"),
			});
		await Controller().ReconcileAsync(server, CancellationToken.None);
		Assert.That(PodDeletes(_client), Is.Zero);
		await _client.Received().UpdateStatusAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Status.Conditions.Any(c => c.Type == "Ready" && c.Reason == "PodRefused")), Arg.Any<CancellationToken>());
	}

	private ChromaServerController Controller() => new(_client, Defaults, NullLogger<ChromaServerController>.Instance);

	private static V1Alpha1ChromaServer Named(V1Alpha1ChromaServer server)
	{
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "ChromaServer";
		return server;
	}

	/// <summary>The objects of the server as the operator created them, with a ready Pod of the image and the data version on the claim.</summary>
	private V1Pod Existing(V1Alpha1ChromaServer server, string image, string dataVersion)
	{
		var pod = RunningPod(server, image);
		pod.Metadata.Uid = "pod-1";
		pod.Metadata.ResourceVersion = "41";
		var claim = Resources.Pvc(server);
		claim.Metadata.Annotations = new Dictionary<string, string> { [Labels.ClaimDataVersion] = dataVersion };
		_client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.ConfigMap(server));
		_client.GetAsync<V1Service>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Service(server));
		_client.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(claim);
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pod);
		return pod;
	}
}
