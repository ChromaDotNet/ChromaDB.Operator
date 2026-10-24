using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the fifth review: the image the kubelet reports, a combined rollback, several ports, the owner of a Pod.</summary>
public class FifthReviewTests
{
	// The Pod spec says 1.5.3 again, but the kubelet reports 1.5.9 running: the data version counts the image that runs.
	[Test]
	public void DataVersionCountsTheReportedImage()
	{
		var server = NewChroma(Image153, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(server, Image153);
		pod.Status.ContainerStatuses[0].Image = Image159;
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
	}

	// 1.5.3 to 1.5.9 under way, then image and configuration both reverted: the Pod of 1.5.9 stays, as the only one the data allows.
	[Test]
	public void BlockedServerKeepsItsPodAgainstAConfigurationChange()
	{
		var upgrading = NewChroma(Image159);
		var pod = RunningPod(upgrading, Image159, ready: false);
		var server = NewChroma(Image153, dataVersion: "1.5.9", runningImage: Image153);
		server.Spec.Config = new() { MaxPayloadSizeBytes = 1000 };
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("True"));
	}

	// With several ports, the node port of the port named http stays, in either order.
	[TestCase(false)]
	[TestCase(true)]
	public void NodePortOfTheHttpPortStays(bool httpFirst)
	{
		var server = NewChroma();
		var current = Resources.Service(server);
		current.Spec.Type = "NodePort";
		var debug = new V1ServicePort { Name = "debug", Port = 8000, TargetPort = 8000, Protocol = "TCP", NodePort = 32000 };
		var http = new V1ServicePort { Name = "http", Port = 9000, TargetPort = 9000, Protocol = "TCP", NodePort = 31000 };
		current.Spec.Ports = httpFirst ? [http, debug] : [debug, http];
		Assert.That(Ownership.ApplyRouting(current, Resources.Service(server)), Is.True);
		Assert.That(current.Spec.Ports.Single().NodePort, Is.EqualTo(31000));
	}

	// The owner says which server a Pod belongs to; the label only when there is no owner.
	[Test]
	public void ServerOfAPod()
	{
		var pod = Resources.Pod(NewChroma(), Image159);
		Assert.That(PodWatcher.ChromaOf(pod), Is.EqualTo("demo"));
		pod.Metadata.Labels[Labels.Chroma] = "other";
		Assert.That(PodWatcher.ChromaOf(pod), Is.EqualTo("demo"), "a wrong label");
		pod.Metadata.Labels.Remove(Labels.Chroma);
		Assert.That(PodWatcher.ChromaOf(pod), Is.EqualTo("demo"), "no label");
		pod.Metadata.OwnerReferences = null;
		pod.Metadata.Labels[Labels.Chroma] = "label";
		Assert.That(PodWatcher.ChromaOf(pod), Is.EqualTo("label"), "no owner");
	}

	// A kind Chroma of another group, from another operator, is not ours: the label decides.
	[Test]
	public void OwnerChromaOfAnotherGroupIsNotOurs()
	{
		var pod = Resources.Pod(NewChroma(), Image159);
		pod.Metadata.OwnerReferences[0].ApiVersion = "example.com/v1";
		pod.Metadata.Labels[Labels.Chroma] = "label";
		Assert.That(PodWatcher.ChromaOf(pod), Is.EqualTo("label"));
	}
}
