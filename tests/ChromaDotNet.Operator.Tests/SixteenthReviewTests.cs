using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the sixteenth review: the declared Pod after a round trip of its image, and two different incidents.</summary>
public class SixteenthReviewTests
{
	// The declared Pod goes to a versioned image, then back to latest: the second run without a version is a new incident.
	[Test]
	public void DeclarationEndsWhenThePodChangesImage()
	{
		var server = Server(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Metadata.Uid = "pod-1";
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:latest";
		var live = Reconciliation.Decide(server, pod, null, Now, Defaults);
		server.Status = live.Status;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/" + live.Status.DataVersionUnknownIncident };
		server.Status = Reconciliation.Decide(server, pod, null, Now.AddMinutes(1), Defaults).Status;
		Assert.That(server.Status.DataVersionDeclaredPod, Is.EqualTo("pod-1/docker.io/chromadb/chroma:latest"));

		pod.Spec.Containers[0].Image = Image159;
		server.Status = Reconciliation.Decide(server, pod, null, Now.AddMinutes(2), Defaults).Status;
		Assert.That(server.Status.DataVersionDeclaredPod, Is.Null, "the Pod changed its image");

		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:latest";
		var again = Reconciliation.Decide(server, pod, null, Now.AddMinutes(3), Defaults);
		Assert.That(again.Status.DataVersionUnknown, Is.True);
	}

	// Incident A in the status, a different incident B on the claim, only A declared: nothing is resolved and no Pod starts.
	[Test]
	public void DeclarationOfOneIncidentDoesNotClearAnother()
	{
		var server = Server(Image159, dataVersion: "1.5.3", runningImage: Image159);
		server.Status!.DataVersionUnknown = true;
		server.Status.DataVersionUnknownIncident = "aaaa1111";
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/aaaa1111" };
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults, pvcDataVersionUnknownIncident: "bbbb2222");
		Assert.That(outcome.Status.DataVersionUnknown, Is.True);
		Assert.That(outcome.Status.DataVersionUnknownIncident, Is.Not.EqualTo("aaaa1111").And.Not.EqualTo("bbbb2222"), "one code for both");
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));

		server.Status = outcome.Status;
		var same = Reconciliation.Decide(server, null, null, Now.AddMinutes(1), Defaults, pvcDataVersionUnknownIncident: outcome.Status.DataVersionUnknownIncident);
		Assert.That(same.Status.DataVersionUnknownIncident, Is.EqualTo(outcome.Status.DataVersionUnknownIncident), "stable once on the claim");
	}
}
