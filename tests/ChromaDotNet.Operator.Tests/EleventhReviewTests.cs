using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The case of the eleventh review: a declaration resolves only the incident it names.</summary>
public class EleventhReviewTests
{
	// First incident resolved with its declaration; the annotation stays. The Pod is edited again to an image without a version: the old
	// declaration does not resolve the new incident.
	[Test]
	public void OldDeclarationDoesNotResolveANewIncident()
	{
		var server = Server(Image159, dataVersion: "1.5.9", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:latest";
		var first = Reconciliation.Decide(server, pod, null, Now, Defaults);
		var incident = first.Status.DataVersionUnknownIncident!;
		server.Status = first.Status;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/" + incident };
		var resolved = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now.AddMinutes(1), Defaults);
		Assert.That(resolved.Status.DataVersionUnknown, Is.False);

		server.Status = resolved.Status;
		var again = Reconciliation.Decide(server, pod, null, Now.AddMinutes(2), Defaults);
		Assert.That(again.Status.DataVersionUnknown, Is.True, "a new incident");
		Assert.That(again.Status.DataVersionUnknownIncident, Is.Not.EqualTo(incident));
		Assert.That(again.Action, Is.EqualTo(PodAction.None), "the Pod is not replaced");
	}

	// A declaration without the incident resolves nothing.
	[Test]
	public void DeclarationWithoutTheIncidentResolvesNothing()
	{
		var server = Server(Image159, dataVersion: "1.0.0", runningImage: Image159);
		server.Status!.DataVersionUnknown = true;
		server.Status.DataVersionUnknownIncident = "a1b2c3d4";
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9" };
		Assert.That(Reconciliation.Decide(server, null, null, Now, Defaults).Status.DataVersionUnknown, Is.True);
	}
}
