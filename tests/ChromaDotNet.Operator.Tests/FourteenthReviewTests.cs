using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The case of the fourteenth review: a stale incident on the claim does not cover another image without a version.</summary>
public class FourteenthReviewTests
{
	// Incident A resolved for a live latest Pod; clearing the claim is refused, so the claim keeps A. The Pod is then edited to nightly:
	// that is a new incident with a fresh code, which the declaration of A does not resolve.
	[Test]
	public void StaleClaimIncidentDoesNotCoverAnotherImage()
	{
		var server = Server(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Metadata.Uid = "pod-1";
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:latest";
		var live = Reconciliation.Decide(server, pod, null, Now, Defaults);
		var incident = live.Status.DataVersionUnknownIncident!;
		server.Status = live.Status;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/" + incident };
		var resolved = Reconciliation.Decide(server, pod, null, Now.AddMinutes(1), Defaults, pvcDataVersionUnknownIncident: incident);
		Assert.That(resolved.Status.DataVersionUnknown, Is.False);

		server.Status = resolved.Status;
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:nightly";
		var edited = Reconciliation.Decide(server, pod, null, Now.AddMinutes(2), Defaults, pvcDataVersionUnknownIncident: incident);
		Assert.That(edited.Status.DataVersionUnknown, Is.True);
		Assert.That(edited.Status.DataVersionUnknownIncident, Is.Not.EqualTo(incident), "a fresh code");
		Assert.That(edited.Action, Is.EqualTo(PodAction.None));
	}
}
