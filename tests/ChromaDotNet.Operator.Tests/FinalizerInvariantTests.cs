using System.Net;
using ChromaDotNet.Operator.Controllers;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The finalizer is on a ChromaServer when, and only when, the server owns its claim: added before the owner reference, removed after
/// it. A server that owns nothing does not wait for the operator to be deleted, nor does its namespace.
/// </summary>
public class FinalizerInvariantTests
{
	private const string Finalizer = "chroma.chromadotnet.org/claimfinalizer";

	private IKubernetesClient _client = null!;
	private V1PersistentVolumeClaim? _claim;

	[SetUp]
	public void SetUp()
	{
		_claim = null;
		_client = Substitute.For<IKubernetesClient>();
		_client.CreateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1ConfigMap>());
		_client.CreateAsync(Arg.Any<V1Service>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Service>());
		_client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Pod>());
		_client.CreateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => _claim = call.Arg<V1PersistentVolumeClaim>());
		// The claim as the API keeps it: a read gives a copy, and only an accepted update changes it.
		_client.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns(_ => _claim is null ? null : KubernetesJson.Deserialize<V1PersistentVolumeClaim>(KubernetesJson.Serialize(_claim)));
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => _claim = call.Arg<V1PersistentVolumeClaim>());
		_client.UpdateAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1ChromaServer>());
		_client.UpdateStatusAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1ChromaServer>());
	}

	[TearDown]
	public void TearDown() => _client.Dispose();

	[Test]
	public void IdentifierIsTheOneKubeOpsGives() =>
		Assert.That(new ClaimFinalizer(_client, NullLogger<ClaimFinalizer>.Instance).GetIdentifierName(Named(Server())), Is.EqualTo(Finalizer));

	// A server with Retain whose claim it does not own: the finalizer, added by an earlier version to every server, goes.
	[Test]
	public async Task RetainWithoutOwningTheClaimHasNoFinalizer()
	{
		var server = Steady("Retain");
		server.Metadata.Finalizers = [Finalizer];
		await Controller().ReconcileAsync(server, CancellationToken.None);
		await _client.Received(1).UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => !s.Finalizers().Contains(Finalizer)), Arg.Any<CancellationToken>());
	}

	// A server with Retain and no finalizer is not written: the steady state.
	[Test]
	public async Task RetainWithoutTheFinalizerIsNotWritten()
	{
		await Controller().ReconcileAsync(Steady("Retain"), CancellationToken.None);
		await _client.DidNotReceive().UpdateAsync(Arg.Any<V1Alpha1ChromaServer>(), Arg.Any<CancellationToken>());
	}

	// A change to Delete: the finalizer first, then the owner reference on the claim.
	[Test]
	public async Task DeleteAddsTheFinalizerBeforeTheOwner()
	{
		var server = Steady("Retain");
		server.Spec.PvcDeletionPolicy = "Delete";
		await Controller().ReconcileAsync(server, CancellationToken.None);
		Received.InOrder(() =>
		{
			_client.UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Finalizers().Contains(Finalizer)), Arg.Any<CancellationToken>());
			_client.UpdateAsync(Arg.Is<V1PersistentVolumeClaim>(c => c.Metadata.OwnerReferences.Any(o => o.Uid == "uid-1")), Arg.Any<CancellationToken>());
		});
	}

	// A new server with Delete: the finalizer before the claim, which is created owned.
	[Test]
	public async Task NewServerWithDeleteHasTheFinalizerBeforeItsClaim()
	{
		var server = Named(Server());
		server.Spec.PvcDeletionPolicy = "Delete";
		await Controller().ReconcileAsync(server, CancellationToken.None);
		Received.InOrder(() =>
		{
			_client.UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => s.Finalizers().Contains(Finalizer)), Arg.Any<CancellationToken>());
			_client.CreateAsync(Arg.Is<V1PersistentVolumeClaim>(c => c.Metadata.OwnerReferences.Any(o => o.Uid == "uid-1")), Arg.Any<CancellationToken>());
		});
	}

	// A change to Retain: the owner reference goes first, then the finalizer.
	[Test]
	public async Task RetainRemovesTheOwnerBeforeTheFinalizer()
	{
		var server = Steady("Delete");
		server.Metadata.Finalizers = [Finalizer];
		server.Spec.PvcDeletionPolicy = "Retain";
		await Controller().ReconcileAsync(server, CancellationToken.None);
		Received.InOrder(() =>
		{
			_client.UpdateAsync(Arg.Is<V1PersistentVolumeClaim>(c => !(c.Metadata.OwnerReferences ?? new List<V1OwnerReference>()).Any(o => o.Uid == "uid-1")), Arg.Any<CancellationToken>());
			_client.UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => !s.Finalizers().Contains(Finalizer)), Arg.Any<CancellationToken>());
		});
	}

	// The API refuses to remove the owner reference: the claim is still owned, so the finalizer stays.
	[Test]
	public async Task RefusedReleaseKeepsTheFinalizer()
	{
		var server = Steady("Delete");
		server.Metadata.Finalizers = [Finalizer];
		server.Spec.PvcDeletionPolicy = "Retain";
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Throws(new HttpOperationException("Unprocessable")
		{
			Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity), "claims are frozen"),
		});
		await Controller().ReconcileAsync(server, CancellationToken.None);
		await _client.DidNotReceive().UpdateAsync(Arg.Is<V1Alpha1ChromaServer>(s => !s.Finalizers().Contains(Finalizer)), Arg.Any<CancellationToken>());
	}

	private ChromaServerController Controller() => new(_client, Defaults, NullLogger<ChromaServerController>.Instance);

	/// <summary>A ready server of 1.5.9 with its objects, its claim created under the policy given.</summary>
	private V1Alpha1ChromaServer Steady(string policy)
	{
		var server = Named(Server(Image159, dataVersion: "1.5.9", runningImage: Image159));
		server.Spec.PvcDeletionPolicy = policy;
		var pod = RunningPod(server, Image159);
		_claim = Resources.Pvc(server);
		_claim.Metadata.Annotations = new Dictionary<string, string> { [Labels.ClaimDataVersion] = "1.5.9" };
		_client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.ConfigMap(server));
		_client.GetAsync<V1Service>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Service(server));
		_client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pod);
		server.Status = Reconciliation.Decide(server, pod, "1.5.9", Now, Defaults).Status;
		return server;
	}

	private static V1Alpha1ChromaServer Named(V1Alpha1ChromaServer server)
	{
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "ChromaServer";
		return server;
	}
}
