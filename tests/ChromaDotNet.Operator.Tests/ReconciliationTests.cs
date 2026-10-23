using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

public class ReconciliationTests
{
	[Test]
	public void NoPodCreatesOne()
	{
		var outcome = Reconciliation.Decide(Server(), null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
		Assert.That(outcome.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Creating"));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"), "written before the Pod starts: it can migrate the data before it reports that it started");
		Assert.That(outcome.RequeueAfter, Is.EqualTo(Reconciliation.BusyRequeue));
	}

	[Test]
	public void ReadyPodMakesTheServerReady()
	{
		var server = Server();
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Ready"));
		Assert.That(outcome.Status.Image, Is.EqualTo(Image159));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Status.ObservedGeneration, Is.EqualTo(3));
		Assert.That(Condition(outcome, "Ready").Status, Is.EqualTo("True"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("False"));
		Assert.That(outcome.RequeueAfter, Is.EqualTo(Reconciliation.SteadyRequeue));
	}

	// The Pod of 1.5.3 is deleted first, with the data still at 1.5.3; the next pass, without a Pod, raises the data version to 1.5.9
	// before it creates the new one.
	[Test]
	public void NewImageReplacesThePod()
	{
		var server = Server(Image159, dataVersion: "1.5.3", runningImage: Image153);
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image153), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Delete));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Updating"));
		Assert.That(outcome.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159), "the replacement the API checks");
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.3"), "1.5.9 has not run yet");

		server.Status = outcome.Status;
		var next = Reconciliation.Decide(server, null, null, Now.AddSeconds(2), Defaults);
		Assert.That(next.Action, Is.EqualTo(PodAction.Create));
		Assert.That(next.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
		Assert.That(next.Status.DataVersion, Is.EqualTo("1.5.9"));
	}

	[Test]
	public void NewConfigurationReplacesThePod()
	{
		var server = Server(dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		server.Spec.Config = new() { MaxPayloadSizeBytes = 1000 };
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Action, Is.EqualTo(PodAction.Delete));
	}

	[Test]
	public void TerminatingPodIsAwaited()
	{
		var server = Server();
		var pod = RunningPod(server, Image153);
		pod.Metadata.DeletionTimestamp = Now;
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Action, Is.EqualTo(PodAction.Wait));
	}

	[TestCase("Failed")]
	[TestCase("Succeeded")]
	public void EndedPodIsReplaced(string phase)
	{
		var server = Server();
		var pod = RunningPod(server, Image159);
		pod.Status.Phase = phase;
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Action, Is.EqualTo(PodAction.Delete));
	}

	// Chroma migrates the data at its start, and an older version does not open it again: 1.0.0 panics on data opened by 1.5.9.
	[Test]
	public void OlderImageThanTheDataIsRefused()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image159);
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None), "the running Pod stays");
		Assert.That(outcome.Status.Phase, Is.EqualTo("Ready"));
		Assert.That(outcome.Status.Image, Is.EqualTo(Image159));
		var blocked = Condition(outcome, "UpgradeBlocked");
		Assert.That(blocked.Status, Is.EqualTo("True"));
		Assert.That(blocked.Message, Does.Contain("1.5.9").And.Contain(Labels.AllowDowngrade));
	}

	[Test]
	public void OlderImageIsAllowedWithTheAnnotation()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image159);
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowDowngrade] = "true" };
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Delete));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("False"));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"), "a downgrade never lowers the data version");
	}

	[Test]
	public void RefusedImageWithoutARunningPodRestartsTheRunningImage()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image159);
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
		Assert.That(outcome.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Image159));
	}

	// A ChromaServer created again on a claim kept with Retain: the data version comes from the claim. The new server has seen no image
	// it could start instead, so it starts nothing.
	[Test]
	public void DataVersionOfARetainedClaim()
	{
		var outcome = Reconciliation.Decide(Server(Image153), null, "1.5.9", Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("True"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("NoImage"));
	}

	// The data version grows as soon as the container has started, ready or not: its migrations may already be applied.
	[Test]
	public void DataVersionGrowsWhenTheContainerHasRun()
	{
		var server = Server(Image159, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(server, Image159, ready: false);
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Updating"));
		Assert.That(outcome.Status.Image, Is.EqualTo(Image153), "the image of status changes once the new Pod is ready");
	}

	[Test]
	public void DataVersionCountsAPodThatHasNotRun()
	{
		var server = Server(Image159, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(server, Image159, ready: false);
		pod.Status.ContainerStatuses[0] = new V1ContainerStatus
		{
			Name = "chroma",
			Image = Image159,
			ImageID = "",
			Started = false,
			State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ContainerCreating" } },
		};
		Assert.That(Reconciliation.Decide(server, pod, null, Now, Defaults).Status.DataVersion, Is.EqualTo("1.5.9"), "the conservative bound: it may have started");
	}

	[TestCase("CrashLoopBackOff")]
	[TestCase("ImagePullBackOff")]
	public void FailingContainerFailsTheServer(string reason)
	{
		var server = Server();
		var pod = RunningPod(server, Image159, ready: false);
		pod.Status.ContainerStatuses[0].State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = reason, Message = "details" } };
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo(reason));
	}

	// The operator allows the repositories of Chroma only, unless its administrator says otherwise: the running Pod stays.
	[Test]
	public void ImageOutsideTheAllowedRepositoriesIsRefused()
	{
		var server = Server("docker.io/someone/not-chroma:1.5.9", dataVersion: "1.5.9", runningImage: Image159);
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Image, Is.EqualTo(Image159));
		var blocked = Condition(outcome, "UpgradeBlocked");
		Assert.That(blocked.Status, Is.EqualTo("True"));
		Assert.That(blocked.Reason, Is.EqualTo("ImageNotAllowed"));
		Assert.That(blocked.Message, Does.Contain(OperatorOptions.AllowedImagesVariable));
	}

	[Test]
	public void ImageOutsideTheAllowedRepositoriesStartsNothing()
	{
		var outcome = Reconciliation.Decide(Server("docker.io/someone/not-chroma:1.5.9"), null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
	}

	[Test]
	public void AnyImageWhenTheAdministratorAllowsIt()
	{
		var outcome = Reconciliation.Decide(Server("registry.local:5055/team/chroma:1.5.9"), null, null, Now, new OperatorOptions(allowedImages: "*"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
	}

	// The running Pod stays, and what it shows is still recorded.
	[Test]
	public void InvalidImageFailsWithoutTouchingThePod()
	{
		var server = Server("chromadb/chroma:latest", dataVersion: "1.5.3", runningImage: Image153);
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.DesiredPod, Is.Null);
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("InvalidImage"));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Status.RecoveryImage, Is.EqualTo(Image159));

		var withoutPod = Reconciliation.Decide(Server("chromadb/chroma:latest"), null, null, Now, Defaults);
		Assert.That(withoutPod.Action, Is.EqualTo(PodAction.None), "nothing is created either");
	}

	[Test]
	public void TransitionTimeChangesOnlyWithTheStatus()
	{
		var server = Server();
		var first = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		server.Status = first.Status;
		var later = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now.AddMinutes(5), Defaults);
		Assert.That(Condition(later, "Ready").LastTransitionTime, Is.EqualTo(Now));
		var notReady = Reconciliation.Decide(server, RunningPod(server, Image159, ready: false), null, Now.AddMinutes(10), Defaults);
		Assert.That(Condition(notReady, "Ready").LastTransitionTime, Is.EqualTo(Now.AddMinutes(10)));
	}
}
