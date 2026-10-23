using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The case of the thirteenth review: the incident kept on the claim, while the claim cannot be cleared.</summary>
public class ThirteenthReviewTests
{
	// The declaration resolves incident A, but the claim keeps its code: clearing it was refused. The next pass sees incident A again,
	// not a new one, and the same declaration still resolves it. Once the claim is cleared, the version is known.
	[Test]
	public void ClaimThatKeepsTheIncidentKeepsItsCode()
	{
		var server = Server(Image159, dataVersion: "1.5.3", runningImage: Image159);
		server.Status!.DataVersionUnknown = true;
		server.Status.DataVersionUnknownIncident = "a1b2c3d4";
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.DeclaredDataVersion] = "1.5.9/a1b2c3d4" };
		var resolved = Reconciliation.Decide(server, null, null, Now, Defaults, pvcDataVersionUnknownIncident: "a1b2c3d4");
		Assert.That(resolved.Status.DataVersionUnknown, Is.False);

		server.Status = resolved.Status;
		var refusedClear = Reconciliation.Decide(server, null, null, Now.AddMinutes(1), Defaults, pvcDataVersionUnknownIncident: "a1b2c3d4");
		Assert.That(refusedClear.Status.DataVersionUnknown, Is.False, "the same incident, resolved by the same declaration");
		Assert.That(refusedClear.Action, Is.EqualTo(PodAction.Create));

		server.Status = refusedClear.Status;
		var cleared = Reconciliation.Decide(server, null, null, Now.AddMinutes(2), Defaults);
		Assert.That(cleared.Status.DataVersionUnknown, Is.False);
	}

	// Without a declaration, the code on the claim is the code of the incident.
	[Test]
	public void IncidentOfTheClaimIsKept()
	{
		var outcome = Reconciliation.Decide(Server(Image159), null, "1.5.3", Now, Defaults, pvcDataVersionUnknownIncident: "c0ffee00");
		Assert.That(outcome.Status.DataVersionUnknownIncident, Is.EqualTo("c0ffee00"));
	}
}
