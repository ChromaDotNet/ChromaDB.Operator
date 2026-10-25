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
/// A mutating webhook rewrites the image of the Pod at its creation, as a mirror or a policy that adds a digest does. The Pod keeps the
/// image the API admitted, and a Pod that runs it is the one the operator created: it stays. An image changed after that is replaced.
/// </summary>
public class AdmittedImageTests
{
	private const string Rewritten = "chromadb/chroma:1.5.9";

	[Test]
	public void PodWithTheAdmittedImageStays()
	{
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Rewritten);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = Rewritten;
		pod.Metadata.Annotations[Labels.AdmittedImage] = Rewritten;
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Ready"));
	}

	[Test]
	public void ImageChangedAfterTheAdmissionIsReplaced()
	{
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Rewritten);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = "ghcr.io/chroma-core/chroma:1.5.9";
		pod.Metadata.Annotations[Labels.AdmittedImage] = Rewritten;
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Action, Is.EqualTo(PodAction.Delete));
	}

	[Test]
	public void RewrittenImageWithoutTheAnnotationIsReplaced()
	{
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = Rewritten;
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Action, Is.EqualTo(PodAction.Delete));
	}

	// The API answers the creation with the Pod as the webhook made it: the operator writes the admitted image on it.
	[Test]
	public async Task CreatedPodGetsTheAdmittedImage()
	{
		var server = NewChroma();
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "Chroma";
		using var client = Substitute.For<IKubernetesClient>();
		client.CreateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1ConfigMap>());
		client.CreateAsync(Arg.Any<V1Service>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Service>());
		client.CreateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1Chroma>());
		client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			var admitted = k8s.KubernetesJson.Deserialize<V1Pod>(k8s.KubernetesJson.Serialize(call.Arg<V1Pod>()));
			admitted.Spec.Containers[0].Image = Rewritten;
			return admitted;
		});
		client.UpdateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Pod>());
		await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(server, CancellationToken.None);
		await client.Received(1).UpdateAsync(Arg.Is<V1Pod>(p => p.Metadata.Annotations[Labels.AdmittedImage] == Rewritten), Arg.Any<CancellationToken>());
	}

	// The Pod changed between its creation and the annotation, as when the scheduler binds it to a node: it is read again and marked.
	[Test]
	public async Task AdmittedImageIsWrittenAfterAConflict()
	{
		var (client, server) = NewChromaWithWebhook();
		V1Pod? created = null;
		client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => created);
		client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => created = Rewrite(call.Arg<V1Pod>()));
		var updates = 0;
		client.UpdateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => ++updates == 1 ? throw Answer(HttpStatusCode.Conflict) : call.Arg<V1Pod>());
		// The first read finds no Pod: the pass creates it; the read after the conflict finds it.
		var reads = 0;
		client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => ++reads == 1 ? null : Copy(created!));
		using (client)
			await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(server, CancellationToken.None);
		Assert.That(updates, Is.EqualTo(2));
		await client.Received().UpdateAsync(Arg.Is<V1Pod>(p => p.Metadata.Annotations[Labels.AdmittedImage] == Rewritten), Arg.Any<CancellationToken>());
	}

	// The Pod was edited between its creation and the annotation: its image is not the admitted one, so it is not marked.
	[Test]
	public async Task PodEditedMeanwhileIsNotMarked()
	{
		var (client, server) = NewChromaWithWebhook();
		V1Pod? created = null;
		client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => created = Rewrite(call.Arg<V1Pod>()));
		var updates = 0;
		client.UpdateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => ++updates == 1 ? throw Answer(HttpStatusCode.Conflict) : call.Arg<V1Pod>());
		var reads = 0;
		client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ =>
		{
			if (++reads == 1)
				return null;
			var edited = Copy(created!);
			edited.Spec.Containers[0].Image = "ghcr.io/chroma-core/chroma:1.5.9";
			return edited;
		});
		using (client)
			await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(server, CancellationToken.None);
		Assert.That(updates, Is.EqualTo(1));
	}

	// A webhook that adds a digest to the tag keeps the version: the data version is known.
	[Test]
	public void DigestAddedToTheTagKeepsTheVersion()
	{
		const string pinned = "docker.io/chromadb/chroma:1.5.9@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: pinned);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = pinned;
		pod.Metadata.Annotations[Labels.AdmittedImage] = pinned;
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.DataVersionUnknown, Is.False);
	}

	// A webhook that replaces the tag with a digest leaves no version: the data version is unknown, which stops the server until an
	// administrator declares it. Safe, and documented: such a rewrite must keep the tag.
	[Test]
	public void DigestWithoutTheTagMakesTheVersionUnknown()
	{
		const string pinned = "docker.io/chromadb/chroma@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = pinned;
		pod.Metadata.Annotations[Labels.AdmittedImage] = pinned;
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Status.DataVersionUnknown, Is.True);
	}

	private static (IKubernetesClient Client, V1Alpha1Chroma Server) NewChromaWithWebhook()
	{
		var server = NewChroma();
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "Chroma";
		var client = Substitute.For<IKubernetesClient>();
		client.CreateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1ConfigMap>());
		client.CreateAsync(Arg.Any<V1Service>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Service>());
		client.CreateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1Chroma>());
		return (client, server);
	}

	private static V1Pod Rewrite(V1Pod pod)
	{
		var admitted = Copy(pod);
		admitted.Metadata.Uid = "pod-1";
		admitted.Spec.Containers[0].Image = Rewritten;
		return admitted;
	}

	private static V1Pod Copy(V1Pod pod) => k8s.KubernetesJson.Deserialize<V1Pod>(k8s.KubernetesJson.Serialize(pod));

	private static HttpOperationException Answer(HttpStatusCode status) => new(status.ToString())
	{
		Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), status.ToString()),
	};

	// Without a webhook the admitted image is the one asked for: no annotation, no update.
	[Test]
	public async Task CreatedPodAsAskedIsNotUpdated()
	{
		var server = NewChroma();
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "Chroma";
		using var client = Substitute.For<IKubernetesClient>();
		client.CreateAsync(Arg.Any<V1ConfigMap>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1ConfigMap>());
		client.CreateAsync(Arg.Any<V1Service>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Service>());
		client.CreateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1Chroma>());
		client.CreateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Pod>());
		await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(server, CancellationToken.None);
		await client.DidNotReceive().UpdateAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>());
	}
}
