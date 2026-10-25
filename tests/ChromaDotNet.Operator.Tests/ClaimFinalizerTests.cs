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
/// The finalizer of a Chroma being deleted: with Retain the claim loses the server as owner before the server goes, also when
/// the policy changed after the last pass; with Delete the claim is left to the garbage collector.
/// </summary>
public class ClaimFinalizerTests
{
	private IKubernetesClient _client = null!;
	private V1Alpha1Chroma _server = null!;
	private V1PersistentVolumeClaim _claim = null!;
	private V1OwnerReference _other = null!;

	[SetUp]
	public void SetUp()
	{
		_server = NewChroma();
		_server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		_server.Kind = "Chroma";
		_server.Metadata.DeletionTimestamp = Now;
		// The claim of a server created with Delete, changed to Retain just before its deletion: the pass that removes the owner has not run.
		_server.Spec.PvcDeletionPolicy = "Retain";
		_claim = Resources.Pvc(_server);
		_other = new V1OwnerReference { ApiVersion = "v1", Kind = "ConfigMap", Name = "keeper", Uid = "uid-other" };
		_claim.Metadata.OwnerReferences = [Resources.OwnerReference(_server), _other];
		_client = Substitute.For<IKubernetesClient>();
		// Each read gives a new object, as the API does.
		_client.GetAsync<V1PersistentVolumeClaim>("demo-1", "rag", Arg.Any<CancellationToken>())
			.Returns(_ => k8s.KubernetesJson.Deserialize<V1PersistentVolumeClaim>(k8s.KubernetesJson.Serialize(_claim)));
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => _claim = call.Arg<V1PersistentVolumeClaim>());
	}

	[TearDown]
	public void TearDown() => _client.Dispose();

	[Test]
	public async Task RetainRemovesTheServerFromTheOwnersOfTheClaim()
	{
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.True);
		Assert.That(_claim.Metadata.OwnerReferences, Is.EqualTo(new[] { _other }), "the other owner stays");
	}

	[Test]
	public async Task RetainWithAClaimNotOwnedWritesNothing()
	{
		_claim.Metadata.OwnerReferences = [_other];
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.True);
		await _client.DidNotReceive().UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task NoClaimIsNothingToDo()
	{
		_client.GetAsync<V1PersistentVolumeClaim>("demo-1", "rag", Arg.Any<CancellationToken>()).Returns((V1PersistentVolumeClaim?)null);
		Assert.That((await Finalizer().FinalizeAsync(_server, CancellationToken.None)).IsSuccess, Is.True);
	}

	// With Delete the finalizer adds no owner: a deletion that orphans the dependents keeps the claim.
	[Test]
	public async Task DeleteLeavesTheClaimAlone()
	{
		_server.Spec.PvcDeletionPolicy = "Delete";
		_claim.Metadata.OwnerReferences = [];
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.True);
		await _client.DidNotReceive().UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task ConflictIsRetriedWithAFreshClaim()
	{
		var attempts = 0;
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			if (++attempts == 1)
				throw Answer(HttpStatusCode.Conflict, "the object has been modified");
			return _claim = call.Arg<V1PersistentVolumeClaim>();
		});
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.True);
		Assert.That(attempts, Is.EqualTo(2));
		Assert.That(_claim.Metadata.OwnerReferences.Any(o => o.Uid == _server.Uid()), Is.False);
	}

	// An update the API refuses keeps the server: removing its finalizer would let the garbage collector delete the claim.
	[Test]
	public async Task RefusedUpdateKeepsTheServer()
	{
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Throws(Answer(HttpStatusCode.UnprocessableEntity, "claims are frozen"));
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.False);
		Assert.That(result.RequeueAfter, Is.EqualTo(ClaimFinalizer.RetryAfter));
	}

	// A failure that is not an answer of the API, like the network, keeps the server too, and comes back: an exception that left the
	// finalizer would be retried a few times, then dropped.
	[Test]
	public async Task NetworkFailureKeepsTheServerAndComesBack()
	{
		_client.GetAsync<V1PersistentVolumeClaim>("demo-1", "rag", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));
		var result = await Finalizer().FinalizeAsync(_server, CancellationToken.None);
		Assert.That(result.IsSuccess, Is.False);
		Assert.That(result.RequeueAfter, Is.EqualTo(ClaimFinalizer.RetryAfter));
	}

	// With Delete, a claim with the name that belongs to another workload is not the server's: no line says to delete it.
	[Test]
	public async Task DeleteNotAppliedIsNotLoggedForTheClaimOfSomeoneElse()
	{
		_server.Spec.PvcDeletionPolicy = "Delete";
		_claim = new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "demo-1", NamespaceProperty = "rag" } };
		var logger = new Fixtures.ListLogger<ClaimFinalizer>();
		Assert.That((await new ClaimFinalizer(_client, logger).FinalizeAsync(_server, CancellationToken.None)).IsSuccess, Is.True);
		Assert.That(logger.Events, Does.Not.Contain(DeleteNotApplied));
	}

	// With Delete, the claim of the server that no pass made it own: the claim stays, and the log says so.
	[Test]
	public async Task DeleteNotAppliedIsLoggedForTheClaimOfTheServer()
	{
		_server.Spec.PvcDeletionPolicy = "Delete";
		_claim.Metadata.OwnerReferences = [_other];
		var logger = new Fixtures.ListLogger<ClaimFinalizer>();
		Assert.That((await new ClaimFinalizer(_client, logger).FinalizeAsync(_server, CancellationToken.None)).IsSuccess, Is.True);
		Assert.That(logger.Events, Does.Contain(DeleteNotApplied));
	}

	// The claim is deleted while the finalizer writes it: done, and no line says that it stays.
	[Test]
	public async Task ClaimDeletedMeanwhileIsNotReportedAsKept()
	{
		var reads = 0;
		var first = k8s.KubernetesJson.Deserialize<V1PersistentVolumeClaim>(k8s.KubernetesJson.Serialize(_claim));
		_client.GetAsync<V1PersistentVolumeClaim>("demo-1", "rag", Arg.Any<CancellationToken>()).Returns(_ => ++reads == 1 ? first : null);
		_client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Throws(Answer(HttpStatusCode.Conflict, "the object has been modified"));
		var logger = new Fixtures.ListLogger<ClaimFinalizer>();
		Assert.That((await new ClaimFinalizer(_client, logger).FinalizeAsync(_server, CancellationToken.None)).IsSuccess, Is.True);
		Assert.That(logger.Events, Does.Not.Contain(ClaimReleased).And.Not.Contain(ClaimNotReleased));
	}

	private const int ClaimReleased = 6;
	private const int ClaimNotReleased = 7;
	private const int DeleteNotApplied = 8;

	private ClaimFinalizer Finalizer() => new(_client, NullLogger<ClaimFinalizer>.Instance);

	private static HttpOperationException Answer(HttpStatusCode status, string content) => new(status.ToString())
	{
		Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), content),
	};
}
