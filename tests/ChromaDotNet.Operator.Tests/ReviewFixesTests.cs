using k8s.Models;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The cases of the first review: objects of others, the claim, the downgrade by a fallback, versions, annotations, names.</summary>
public class ReviewFixesTests
{
	// 1.5.9 has run on the data without becoming ready, so dataVersion is 1.5.9 while status.image is still 1.5.3. Its Pod is gone and
	// the spec says 1.5.3 again: the fallback image, 1.5.3, is older than the data, and no Pod of it may start.
	[Test]
	public void FallbackOlderThanTheDataStartsNothing()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image153);
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Status, Is.EqualTo("True"));
	}

	// The same with a running Pod of the fallback: it is kept, nothing is created or replaced.
	[Test]
	public void FallbackOlderThanTheDataKeepsARunningPod()
	{
		var server = Server(Image153, dataVersion: "1.5.9", runningImage: Image153);
		server.Spec.Config = new() { MaxPayloadSizeBytes = 1000 };
		var outcome = Reconciliation.Decide(server, RunningPod(Server(Image153), Image153), null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// A fallback outside the repositories allowed now starts nothing either.
	[Test]
	public void FallbackNoLongerAllowedStartsNothing()
	{
		var server = Server("docker.io/someone/x:1.5.9", dataVersion: "1.5.9", runningImage: "docker.io/someone/x:1.5.9");
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
	}

	// The pattern of the CRD accepts any number of digits: a version that does not fit an int is an invalid image, not an exception.
	[TestCase("chromadb/chroma:2147483648.0.0")]
	[TestCase("chromadb/chroma:1.99999999999.0")]
	public void OversizedVersionIsInvalid(string image)
	{
		Assert.That(ChromaImage.VersionOf(image), Is.Null);
		var outcome = Reconciliation.Decide(Server(image), null, null, Now, Defaults);
		Assert.That(outcome.Status.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("InvalidImage"));
	}

	[Test]
	public void ObjectsOfOthersAreConflicts()
	{
		var server = Server();
		var ours = Resources.ConfigMap(server).Metadata;
		Assert.That(Ownership.Conflict("ConfigMap", ours, server), Is.Null);
		var unowned = new V1ObjectMeta { Name = "demo-config" };
		Assert.That(Ownership.Conflict("ConfigMap", unowned, server), Does.Contain("demo-config"));
		var other = new V1ObjectMeta { Name = "demo-rw", OwnerReferences = [new V1OwnerReference { Kind = "Deployment", Name = "web", Uid = "uid-9", Controller = true, ApiVersion = "apps/v1" }] };
		Assert.That(Ownership.Conflict("Service", other, server), Does.Contain("Deployment web"));
	}

	[Test]
	public void ClaimsThatCanBeUsed()
	{
		var server = Server();
		Assert.That(Ownership.ClaimConflict(Resources.Pvc(server), server), Is.Null, "retained claim of this name: labels, no owner");
		server.Spec.PvcDeletionPolicy = "Delete";
		Assert.That(Ownership.ClaimConflict(Resources.Pvc(server), server), Is.Null, "owned claim");

		var foreign = new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = "demo-1" } };
		Assert.That(Ownership.ClaimConflict(foreign, server), Does.Contain("label"));
		var otherServer = Resources.Pvc(Server());
		otherServer.Metadata.Labels[Labels.Server] = "other";
		Assert.That(Ownership.ClaimConflict(otherServer, server), Is.Not.Null);
		var controlled = new V1PersistentVolumeClaim
		{
			Metadata = new V1ObjectMeta
			{
				Name = "demo-1",
				Labels = Labels.For("demo"),
				OwnerReferences = [new V1OwnerReference { Kind = "StatefulSet", Name = "db", Uid = "uid-9", Controller = true, ApiVersion = "apps/v1" }],
			},
		};
		Assert.That(Ownership.ClaimConflict(controlled, server), Does.Contain("StatefulSet db"));
	}

	[Test]
	public void ClaimOwnerFollowsThePolicy()
	{
		var server = Server();
		var pvc = Resources.Pvc(server);
		var unrelated = new V1OwnerReference { Kind = "Backup", Name = "b", Uid = "uid-7", ApiVersion = "x/v1" };
		pvc.Metadata.OwnerReferences = [unrelated];
		server.Spec.PvcDeletionPolicy = "Delete";
		Assert.That(Ownership.ReconcileClaimOwner(pvc, server), Is.True);
		Assert.That(pvc.Metadata.OwnerReferences.Select(o => o.Uid), Is.EquivalentTo(new[] { "uid-7", "uid-1" }));
		Assert.That(Ownership.ReconcileClaimOwner(pvc, server), Is.False, "nothing more to change");
		server.Spec.PvcDeletionPolicy = "Retain";
		Assert.That(Ownership.ReconcileClaimOwner(pvc, server), Is.True);
		Assert.That(pvc.Metadata.OwnerReferences.Select(o => o.Uid), Is.EquivalentTo(new[] { "uid-7" }));
	}

	// The annotations of the spec are applied, those removed from the spec are removed, those of others stay.
	[Test]
	public void ServiceAnnotations()
	{
		var metadata = new V1ObjectMeta { Annotations = new Dictionary<string, string> { ["other/key"] = "kept" } };
		Assert.That(Ownership.ApplyAnnotations(metadata, new Dictionary<string, string> { ["lb/a"] = "1", ["lb/b"] = "2" }), Is.True);
		Assert.That(metadata.Annotations["lb/a"], Is.EqualTo("1"));
		Assert.That(Ownership.ApplyAnnotations(metadata, new Dictionary<string, string> { ["lb/a"] = "1", ["lb/b"] = "2" }), Is.False);
		Assert.That(Ownership.ApplyAnnotations(metadata, new Dictionary<string, string> { ["lb/a"] = "3" }), Is.True);
		Assert.That(metadata.Annotations.ContainsKey("lb/b"), Is.False);
		Assert.That(metadata.Annotations["lb/a"], Is.EqualTo("3"));
		Assert.That(Ownership.ApplyAnnotations(metadata, null), Is.True);
		Assert.That(metadata.Annotations.Keys, Is.EquivalentTo(new[] { "other/key" }));
	}

	// The Service <name>-rw is a DNS label of at most 63 characters: the name of the server is a label of at most 60.
	[TestCase("demo", true)]
	[TestCase("a", true)]
	[TestCase("team-db-2", true)]
	[TestCase("team.db", false)]
	[TestCase("1db", false)]
	[TestCase("db-", false)]
	[TestCase("Demo", false)]
	public void ServerNames(string name, bool valid) => Assert.That(Resources.NameError(name) is null, Is.EqualTo(valid));

	[Test]
	public void ServerNameLength()
	{
		Assert.That(Resources.NameError(new string('a', 60)), Is.Null);
		Assert.That(Resources.NameError(new string('a', 61)), Does.Contain("60"));
	}

	[Test]
	public void InvalidNameFailsWithoutObjects()
	{
		var server = Server();
		server.Metadata.Name = "team.db";
		var outcome = Reconciliation.Decide(server, null, null, Now, Defaults);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		Assert.That(Condition(outcome, "Ready").Reason, Is.EqualTo("InvalidName"));
	}
}
