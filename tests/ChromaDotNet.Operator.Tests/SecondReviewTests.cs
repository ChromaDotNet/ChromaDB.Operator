using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the second review: the data version before a newer Pod starts, sidecars, an oversized default image.</summary>
public class SecondReviewTests
{
	// A newer Pod can migrate the data before its status says it started: the data version goes up before the Pod is created.
	[Test]
	public void NewerPodRaisesTheDataVersionBeforeItStarts()
	{
		var server = NewChroma(Image159, dataVersion: "1.5.3", runningImage: Image153);
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
	}

	// The spec goes back to 1.5.3 while the Pod of 1.5.9 still says ContainerCreating: the Pod is not replaced by one of 1.5.3.
	[Test]
	public void RevertWhileTheNewerPodLooksNotStartedIsBlocked()
	{
		var server = NewChroma(Image153, dataVersion: "1.5.9", runningImage: Image153);
		var pod = RunningPod(NewChroma(Image159), Image159, ready: false);
		pod.Status.ContainerStatuses[0] = new V1ContainerStatus
		{
			Name = "chroma",
			Image = Image159,
			ImageID = "",
			Started = false,
			State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ContainerCreating" } },
		};
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("True"));
	}

	// A sidecar injected before Chroma: the image and the state are those of the container named chroma.
	[Test]
	public void SidecarFirstDoesNotChangeTheImageOrTheDataVersion()
	{
		var server = NewChroma();
		var pod = WithSidecar(RunningPod(server, Image159));
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.Image, Is.EqualTo(Image159));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	[Test]
	public void SidecarFirstDoesNotHideAFailingChroma()
	{
		var server = NewChroma();
		var pod = WithSidecar(RunningPod(server, Image159, ready: false));
		pod.Status.ContainerStatuses[1].State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "CrashLoopBackOff", Message = "panic" } };
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("CrashLoopBackOff"));
	}

	[Test]
	public void OversizedDefaultImageStopsTheOperator()
	{
		var e = Assert.Throws<InvalidOperationException>(() => _ = new OperatorOptions("chromadb/chroma:2147483648.0.0"));
		Assert.That(e!.Message, Does.Contain("exact version"));
	}

	private static V1Pod WithSidecar(V1Pod pod)
	{
		const string proxy = "docker.io/envoyproxy/envoy:1.27.1";
		pod.Spec.Containers.Insert(0, new V1Container { Name = "proxy", Image = proxy });
		pod.Status.ContainerStatuses.Insert(0, new V1ContainerStatus
		{
			Name = "proxy",
			Image = proxy,
			ImageID = "",
			Ready = true,
			Started = true,
			State = new V1ContainerState { Running = new V1ContainerStateRunning() },
		});
		return pod;
	}
}
