using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the third review: an image that is never pulled, a Pod or a Service changed by hand.</summary>
public class ThirdReviewTests
{
	// imagePullPolicy Never on a node without the image.
	[Test]
	public void ImageNeverPulledFailsTheServer()
	{
		var server = NewChroma();
		var pod = RunningPod(server, Image159, ready: false);
		pod.Status.ContainerStatuses[0].State = new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ErrImageNeverPull" } };
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("ErrImageNeverPull"));
	}

	// The image of the container changed by hand, the annotation left as it was: the Pod is replaced with the image of the spec.
	[Test]
	public void PodWithAnotherImageThanItsAnnotationIsReplaced()
	{
		var server = NewChroma(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:1.5.8";
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Delete));
	}

	// Selector and port changed by hand are restored; the addresses Kubernetes gave the Service stay.
	[Test]
	public void ServiceRoutingIsRestored()
	{
		var server = NewChroma();
		var desired = Resources.Service(server);
		var current = Resources.Service(server);
		current.Spec.ClusterIP = "10.43.0.10";
		current.Spec.Type = "NodePort";
		current.Spec.Ports[0].NodePort = 31000;
		Assert.That(Ownership.ApplyRouting(current, desired), Is.False, "nothing to restore");

		current.Spec.Selector = new Dictionary<string, string> { ["app"] = "other" };
		current.Spec.Ports[0].TargetPort = 9000;
		Assert.That(Ownership.ApplyRouting(current, desired), Is.True);
		Assert.That(current.Spec.Selector, Is.EqualTo(desired.Spec.Selector));
		Assert.That(current.Spec.Ports.Single().TargetPort.Value, Is.EqualTo("http"));
		Assert.That(current.Spec.Ports.Single().Port, Is.EqualTo(8000));
		Assert.That(current.Spec.Ports.Single().NodePort, Is.EqualTo(31000), "the node port Kubernetes allocated stays");
		Assert.That(current.Spec.ClusterIP, Is.EqualTo("10.43.0.10"));
	}

	[Test]
	public void ServiceWithAnExtraPortIsRestored()
	{
		var server = NewChroma();
		var current = Resources.Service(server);
		current.Spec.Ports.Add(new V1ServicePort { Name = "debug", Port = 9999 });
		Assert.That(Ownership.ApplyRouting(current, Resources.Service(server)), Is.True);
		Assert.That(current.Spec.Ports.Select(p => p.Name), Is.EqualTo(new[] { "http" }));
	}
}
