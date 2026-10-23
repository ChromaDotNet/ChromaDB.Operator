using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the fourth review: the labels of the Pod the Service selects, and the node port of a renamed port.</summary>
public class FourthReviewTests
{
	[TestCase(null)]
	[TestCase("other")]
	public void LabelsOfThePodAreRestored(string? value)
	{
		var server = Server();
		var pod = RunningPod(server, Image159);
		if (value is null)
			pod.Metadata.Labels.Remove(Labels.Server);
		else
			pod.Metadata.Labels[Labels.Server] = value;
		pod.Metadata.Labels["team"] = "kept";
		Assert.That(Ownership.ApplyLabels(pod.Metadata, Labels.For("demo")), Is.True);
		Assert.That(pod.Metadata.Labels[Labels.Server], Is.EqualTo("demo"));
		Assert.That(pod.Metadata.Labels["team"], Is.EqualTo("kept"));
		Assert.That(Ownership.ApplyLabels(pod.Metadata, Labels.For("demo")), Is.False, "nothing more to change");
	}

	// The sole port renamed and renumbered by hand: its node port stays.
	[Test]
	public void NodePortOfTheSolePortStays()
	{
		var server = Server();
		var current = Resources.Service(server);
		current.Spec.Type = "NodePort";
		current.Spec.Ports[0] = new V1ServicePort { Name = "debug", Port = 9000, TargetPort = 9000, Protocol = "TCP", NodePort = 31000 };
		Assert.That(Ownership.ApplyRouting(current, Resources.Service(server)), Is.True);
		Assert.That(current.Spec.Ports.Single().NodePort, Is.EqualTo(31000));
		Assert.That(current.Spec.Ports.Single().Port, Is.EqualTo(8000));
	}
}
