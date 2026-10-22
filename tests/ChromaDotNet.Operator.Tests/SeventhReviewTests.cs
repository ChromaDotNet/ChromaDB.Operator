using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The case of the seventh review: the image a blocked server restarts with after its Pod ended.</summary>
public class SeventhReviewTests
{
	// 1.5.3 to 1.5.9 never ready, the spec back to 1.5.3, the Pod of 1.5.9 evicted. First pass: the ended Pod is deleted, and the image
	// it had is written down. Second pass, no Pod: the server starts again with 1.5.9, the only image the data allows.
	[Test]
	public void BlockedServerRestartsWithTheImageOfItsLastPod()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image153);
		var pod = RunningPod(Server(Image159), Image159, ready: false);
		pod.Status.Phase = "Failed";
		var first = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(first.Action, Is.EqualTo(PodAction.Delete));
		Assert.That(first.Status.RecoveryImage, Is.EqualTo(Image159));

		server.Status = first.Status;
		var second = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(second.Action, Is.EqualTo(PodAction.Create));
		Assert.That(second.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
		Assert.That(Condition(second, "UpgradeBlocked").Status, Is.EqualTo("True"));
	}

	// The image of the last Pod is checked again: no longer allowed, it does not start.
	[Test]
	public void RecoveryImageIsCheckedAgain()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image153);
		server.Status!.RecoveryImage = Image159;
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.DesiredPod?.Spec.Containers[0].Image, Is.EqualTo(Image159), "allowed");
		var narrow = Reconciliation.Decide(server, null, null, Now, new OperatorOptions("ghcr.io/chroma-core/chroma:1.5.9", "ghcr.io/chroma-core/chroma"));
		Assert.That(narrow.Action, Is.EqualTo(PodAction.None), "not allowed any more");
	}
}
