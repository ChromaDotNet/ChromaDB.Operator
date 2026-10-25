using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the sixth review: the image of a Pod that has not reported its start yet.</summary>
public class SixthReviewTests
{
	// A pending Pod of 1.5.3 edited in place to 1.5.9 can migrate the data while it still reports ContainerCreating: its image counts.
	[Test]
	public void DataVersionCountsThePodImageBeforeItStarts()
	{
		var server = NewChroma(Image153, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(NewChroma(Image153), Image153, ready: false);
		pod.Spec.Containers[0].Image = Image159;
		pod.Status.ContainerStatuses[0] = new V1ContainerStatus
		{
			Name = "chroma",
			Image = Image153,
			ImageID = "",
			Started = false,
			State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ContainerCreating" } },
		};
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None), "1.5.3 is now older than the data: the Pod stays");
	}

	[Test]
	public void ConflictKeepsItsReason()
	{
		var outcome = Reconciliation.Conflict(NewChroma(), "Service demo-rw refused", Now, "ServiceRefused", null, null, Defaults);
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("ServiceRefused"));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
	}
}
