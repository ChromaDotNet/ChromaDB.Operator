using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the eighth review: the recovery image through the loss of the Pod.</summary>
public class EighthReviewTests
{
	// The saved recovery image is 1.5.9; a Pod edited by hand to 1.5.3 does not replace it. After the Pod is gone, 1.5.9 starts.
	[Test]
	public void RecoveryImageSurvivesAPodEditedToAnOlderImage()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image153);
		server.Status!.RecoveryImage = Image159;
		var pod = RunningPod(Server(Image153), Image153);
		var first = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(first.Status.RecoveryImage, Is.EqualTo(Image159));

		server.Status = first.Status;
		var second = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(second.Action, Is.EqualTo(PodAction.Create));
		Assert.That(second.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
	}

	// The kubelet reports 1.5.9 running for a Pod whose spec says 1.5.3, and nothing was saved: the reported image becomes the recovery
	// image, so that a server without a Pod can start again.
	[Test]
	public void ReportedImageBecomesTheRecoveryImage()
	{
		var server = Server(Image153, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(Server(Image153), Image153);
		pod.Status.ContainerStatuses[0].Image = Image159;
		var first = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(first.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(first.Status.RecoveryImage, Is.EqualTo(Image159));

		server.Status = first.Status;
		var second = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(second.Action, Is.EqualTo(PodAction.Create));
		Assert.That(second.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
	}
}
