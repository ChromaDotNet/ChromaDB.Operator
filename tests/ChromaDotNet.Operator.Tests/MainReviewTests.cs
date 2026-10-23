using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the review of main after v0.1: each sign that the container of Chroma has run, and the values of allow-downgrade.</summary>
public class MainReviewTests
{
	public static IEnumerable<TestCaseData> HasRun()
	{
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", Started = true }).SetName("Started");
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", RestartCount = 2, State = Waiting("CrashLoopBackOff") }).SetName("Restarted");
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", State = new V1ContainerState { Running = new V1ContainerStateRunning() } }).SetName("Running");
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", State = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1 } } }).SetName("Terminated");
		yield return new TestCaseData(new V1ContainerStatus
		{
			Name = "chroma",
			State = Waiting("CrashLoopBackOff"),
			LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 137 } },
		}).SetName("TerminatedBefore");
	}

	[TestCaseSource(nameof(HasRun))]
	public void ContainerHasRun(V1ContainerStatus container) =>
		Assert.That(Reconciliation.ContainerHasRun(PodWith(container)), Is.True);

	public static IEnumerable<TestCaseData> HasNotRun()
	{
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", Started = false, RestartCount = 0, State = Waiting("ContainerCreating") }).SetName("Creating");
		yield return new TestCaseData(new V1ContainerStatus { Name = "chroma", RestartCount = 0, State = Waiting("ErrImagePull") }).SetName("NotPulled");
		yield return new TestCaseData(new V1ContainerStatus { Name = "proxy", Started = true, State = new V1ContainerState { Running = new V1ContainerStateRunning() } }).SetName("OnlyASidecarRan");
	}

	[TestCaseSource(nameof(HasNotRun))]
	public void ContainerHasNotRun(V1ContainerStatus container) =>
		Assert.That(Reconciliation.ContainerHasRun(PodWith(container)), Is.False);

	[Test]
	public void ContainerWithoutStatusHasNotRun() =>
		Assert.That(Reconciliation.ContainerHasRun(new V1Pod { Status = new V1PodStatus() }), Is.False);

	// Only "true", exactly, forces an image older than the data: any other value, or an empty one, keeps the running Pod.
	[TestCase("true", true)]
	[TestCase("True", false)]
	[TestCase("TRUE", false)]
	[TestCase("yes", false)]
	[TestCase("1", false)]
	[TestCase("", false)]
	[TestCase("false", false)]
	public void AllowDowngradeTakesOnlyTrue(string value, bool allowed)
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image159);
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowDowngrade] = value };
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(allowed ? PodAction.Delete : PodAction.None));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo(allowed ? "False" : "True"));
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
	}

	private static V1ContainerState Waiting(string reason) => new() { Waiting = new V1ContainerStateWaiting { Reason = reason } };

	private static V1Pod PodWith(V1ContainerStatus container)
	{
		container.Image ??= Image159;
		container.ImageID ??= "";
		return new V1Pod { Status = new V1PodStatus { ContainerStatuses = [container] } };
	}
}
