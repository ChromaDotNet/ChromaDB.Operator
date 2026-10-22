using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The cases of the tenth review: a Pod edited by hand to an image without a version. The version of the data is then unknown: no Pod
/// is replaced or started until the annotation data-version says it.
/// </summary>
public class TenthReviewTests
{
	private const string Unversioned = "docker.io/chromadb/chroma:latest";

	[Test]
	public void PodWithoutAVersionMakesTheDataVersionUnknown()
	{
		var server = Server(Image159, dataVersion: "1.0.0", runningImage: Image159);
		var pod = RunningPod(server, Image159);
		pod.Spec.Containers[0].Image = Unversioned;
		var outcome = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(outcome.Status.DataVersionUnknown, Is.True);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None), "the Pod is not replaced");
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("DataVersionUnknown"));

		server.Status = outcome.Status;
		var lost = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(lost.Action, Is.EqualTo(PodAction.None), "no Pod starts on data of an unknown version");
		Assert.That(lost.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(lost, "UpgradeBlocked").Message, Does.Contain(Labels.DeclaredDataVersion));
		Assert.That(outcome.Status.DataVersionUnknownIncident, Is.Not.Null.And.Length.EqualTo(8));
		Assert.That(Condition(lost, "UpgradeBlocked").Message, Does.Contain("/" + outcome.Status.DataVersionUnknownIncident));
		Assert.That(lost.Status.DataVersionUnknownIncident, Is.EqualTo(outcome.Status.DataVersionUnknownIncident), "the incident stays until it is resolved");
	}

	// The administrator says the version: the data version becomes it, and the server starts again.
	[Test]
	public void AnnotationResolvesAnUnknownVersion()
	{
		var server = Server(Image159, dataVersion: "1.0.0", runningImage: Image159);
		server.Status!.DataVersionUnknown = true;
		server.Status.DataVersionUnknownIncident = "a1b2c3d4";
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/a1b2c3d4" };
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Status.DataVersionUnknown, Is.False);
		Assert.That(outcome.Status.DataVersionUnknownIncident, Is.Null);
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
	}

	// A retained claim of data of an unknown version: a new ChromaServer on it starts nothing.
	[Test]
	public void RetainedClaimOfAnUnknownVersion()
	{
		var outcome = Reconciliation.Decide(Server(Image159), null, "1.5.3", Now, Defaults, pvcDataVersionUnknownIncident: "a1b2c3d4");
		Assert.That(outcome.Status.DataVersionUnknown, Is.True);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// A version in the annotation that is not a version does not resolve anything.
	[Test]
	public void InvalidAnnotationResolvesNothing()
	{
		var server = Server(Image159, dataVersion: "1.0.0", runningImage: Image159);
		server.Status!.DataVersionUnknown = true;
		server.Status.DataVersionUnknownIncident = "a1b2c3d4";
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "latest/a1b2c3d4" };
		Assert.That(Reconciliation.Decide(server, null, null, Now, Defaults).Status.DataVersionUnknown, Is.True);
	}
}
