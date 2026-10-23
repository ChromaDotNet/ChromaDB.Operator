using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The case of the twelfth review: the declared Pod is recognized while it stops, and only that Pod.</summary>
public class TwelfthReviewTests
{
	private const string Unversioned = "docker.io/chromadb/chroma:latest";

	// Live Pod without a version, declared, then stopping, then gone: no new incident on the way, and the spec image starts.
	[Test]
	public void DeclaredPodIsRecognizedWhileItStops()
	{
		var server = Server(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Metadata.Uid = "pod-1";
		pod.Spec.Containers[0].Image = Unversioned;
		var live = Reconciliation.Decide(server, pod, null, Now, Defaults);
		var incident = live.Status.DataVersionUnknownIncident!;

		server.Status = live.Status;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/" + incident };
		var declared = Reconciliation.Decide(server, pod, null, Now.AddMinutes(1), Defaults);
		Assert.That(declared.Status.DataVersionUnknown, Is.False);
		Assert.That(declared.Action, Is.EqualTo(PodAction.Delete), "the Pod differs from the spec");

		server.Status = declared.Status;
		pod.Metadata.DeletionTimestamp = Now.AddMinutes(2);
		var stopping = Reconciliation.Decide(server, pod, null, Now.AddMinutes(2), Defaults);
		Assert.That(stopping.Status.DataVersionUnknown, Is.False, "the same Pod, already declared");
		Assert.That(stopping.Action, Is.EqualTo(PodAction.Wait));

		server.Status = stopping.Status;
		var gone = Reconciliation.Decide(server, null, null, Now.AddMinutes(3), Defaults);
		Assert.That(gone.Action, Is.EqualTo(PodAction.Create));
		Assert.That(gone.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
	}

	// The declaration covers that Pod with that image only: another image without a version is a new incident.
	[Test]
	public void DeclarationDoesNotCoverAnotherImage()
	{
		var server = Server(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Metadata.Uid = "pod-1";
		pod.Spec.Containers[0].Image = Unversioned;
		var live = Reconciliation.Decide(server, pod, null, Now, Defaults);
		server.Status = live.Status;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/" + live.Status.DataVersionUnknownIncident };
		server.Status = Reconciliation.Decide(server, pod, null, Now.AddMinutes(1), Defaults).Status;

		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:nightly";
		var edited = Reconciliation.Decide(server, pod, null, Now.AddMinutes(2), Defaults);
		Assert.That(edited.Status.DataVersionUnknown, Is.True);
		Assert.That(edited.Action, Is.EqualTo(PodAction.None));
	}
}
