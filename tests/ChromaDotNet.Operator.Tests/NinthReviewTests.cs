using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The cases of the ninth review: what the Pod shows is recorded also on the passes that fail for another reason, a refused write or
/// an invalid image, and a later pass without the Pod still refuses the older image.
/// </summary>
public class NinthReviewTests
{
	[Test]
	public void FailedPassRecordsWhatThePodShows()
	{
		var server = NewChroma(Image153, dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(NewChroma(Image153), Image159);
		var failed = Reconciliation.Conflict(server, "The API refused the Service demo-rw", Now, "ServiceRefused", pod, null, Defaults);
		Assert.That(failed.Status.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(failed.Status.RecoveryImage, Is.EqualTo(Image159));

		server.Status = failed.Status;
		var next = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(next.DesiredPod?.Spec.Containers[0].Image, Is.EqualTo(Image159), "not 1.5.3 on data of 1.5.9");
	}

	[Test]
	public void InvalidImagePassRecordsWhatThePodShows()
	{
		var server = NewChroma("chromadb/chroma:2147483648.0.0", dataVersion: "1.5.3", runningImage: Image153);
		var pod = RunningPod(NewChroma(Image153), Image159);
		var invalid = Reconciliation.Decide(server, pod, null, Now, Defaults);
		Assert.That(invalid.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(invalid.Status.DataVersion, Is.EqualTo("1.5.9"));

		var corrected = NewChroma(Image153);
		corrected.Status = invalid.Status;
		var next = Reconciliation.Decide(corrected, null, null, Now, Defaults);
		Assert.That(next.DesiredPod?.Spec.Containers[0].Image, Is.EqualTo(Image159));
	}

	[Test]
	public void InvalidNamePassRecordsWhatThePodShows()
	{
		var server = NewChroma(Image153, dataVersion: "1.5.3", runningImage: Image153);
		server.Metadata.Name = "team.db";
		var outcome = Reconciliation.Decide(server, RunningPod(NewChroma(Image153), Image159), null, Now, Defaults);
		Assert.That(outcome.Status.DataVersion, Is.EqualTo("1.5.9"));
	}
}
