using System.Text.Json.Nodes;
using ChromaDotNet.Operator.Controllers;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;

namespace ChromaDotNet.Operator.Tests;

/// <summary>The passes on a distributed Chroma (OPERATOR-DESIGN.md §7.3, §7.4), on a fake namespace.</summary>
public class DistributedTests
{
	private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
	private const string Tag = "52ce844";

	private FakeCluster _cluster = null!;

	[SetUp]
	public void SetUp() => _cluster = new FakeCluster();

	private static V1Alpha1Chroma NewDistributed(long generation = 1)
	{
		var chroma = ChartTests.LoadChroma("default");
		chroma.Metadata.Generation = generation;
		chroma.Metadata.CreationTimestamp = Now.AddHours(-1);
		return chroma;
	}

	private Task<KubeOps.Abstractions.Reconciliation.ReconciliationResult<V1Alpha1Chroma>> PassAsync(V1Alpha1Chroma chroma, DateTime? now = null, OperatorOptions? options = null)
	{
		chroma.Status = _cluster.Status ?? new V1Alpha1Chroma.ChromaStatus();
		return new DistributedReconciler(_cluster.Client, options ?? Fixtures.Defaults, _cluster.Checks, NullLogger.Instance).PassAsync(chroma, now ?? Now, CancellationToken.None);
	}

	private V1Condition Condition(string type) => _cluster.Status!.Conditions.Single(c => c.Type == type);

	[TearDown]
	public void TearDown() => DistributedReconciler.Forget("uid-chart");

	[Test]
	public async Task UnknownVersionCreatesNothing()
	{
		var chroma = NewDistributed();
		chroma.Spec.Distributed!.Version = "abcdef0";
		var result = await PassAsync(chroma);
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition("Ready").Reason, Is.EqualTo("VersionNotSupported"));
		Assert.That(Condition("Ready").Message, Does.Contain("52ce844 (chart 0.1.94)"));
		Assert.That(_cluster.Applied.Concat(_cluster.Created), Is.Empty);
		Assert.That(result.RequeueAfter, Is.EqualTo(DistributedReconciler.SteadyRequeue));
	}

	[Test]
	public async Task MirrorNotAllowedCreatesNothing()
	{
		var chroma = NewDistributed();
		chroma.Spec.Distributed!.ImageRegistry = "registry.example.com/mirror";
		await PassAsync(chroma);
		Assert.That(Condition("Ready").Reason, Is.EqualTo("ImageNotAllowed"));
		Assert.That(Condition("Ready").Message, Does.Contain("registry.example.com/mirror/sysdb-service:52ce844"));
		Assert.That(_cluster.Applied, Is.Empty);

		// A path with /* allows the repositories under it.
		await PassAsync(chroma, options: new OperatorOptions(allowedImages: "docker.io/chromadb/chroma,registry.example.com/mirror/*"));
		Assert.That(Condition("Ready").Reason, Is.Not.EqualTo("ImageNotAllowed"));
	}

	[Test]
	public async Task OneDistributedChromaPerNamespace()
	{
		var chroma = NewDistributed();
		var older = NewDistributed();
		older.Metadata.Name = "first";
		older.Metadata.Uid = "uid-first";
		older.Metadata.CreationTimestamp = Now.AddDays(-1);
		var singleNode = Fixtures.NewChroma();
		singleNode.Metadata.CreationTimestamp = Now.AddDays(-2);
		_cluster.Chromas.AddRange([older, chroma, singleNode]);
		await PassAsync(chroma);
		Assert.That(Condition("Ready").Reason, Is.EqualTo("DistributedNamespaceTaken"));
		Assert.That(Condition("Ready").Message, Does.Contain("first"));
		Assert.That(_cluster.Applied, Is.Empty);

		// The older one is not stopped by the newer one.
		_cluster.Chromas.Clear();
		_cluster.Chromas.AddRange([older, chroma]);
		older.Status = new V1Alpha1Chroma.ChromaStatus();
		await new DistributedReconciler(_cluster.Client, Fixtures.Defaults, _cluster.Checks, NullLogger.Instance).PassAsync(older, Now, CancellationToken.None);
		Assert.That(Condition("Ready").Reason, Is.Not.EqualTo("DistributedNamespaceTaken"));
	}

	[TestCase("chroma-postgres", "SecretNotFound", "PostgresReady")]
	[TestCase("chroma-storage", "SecretNotFound", "ObjectStorageReady")]
	public async Task MissingSecretCreatesNothing(string secret, string reason, string condition)
	{
		_cluster.Secrets.RemoveAll(s => s.Name() == secret);
		await PassAsync(NewDistributed());
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(condition).Status, Is.EqualTo("False"));
		Assert.That(Condition(condition).Reason, Is.EqualTo(reason));
		Assert.That(Condition("Ready").Reason, Is.EqualTo(reason));
		Assert.That(_cluster.Applied.Concat(_cluster.Created), Is.Empty);
	}

	[Test]
	public async Task MissingKeyCreatesNothing()
	{
		_cluster.AddSecret("chroma-postgres", ("username", "chroma"));
		await PassAsync(NewDistributed());
		Assert.That(Condition("PostgresReady").Reason, Is.EqualTo("KeyMissing"));
		Assert.That(Condition("PostgresReady").Message, Does.Contain("password"));
		Assert.That(_cluster.Applied, Is.Empty);
	}

	// The chart passes the credentials unquoted to a shell and into a URL: a password with @ would break the URL of the migration.
	[TestCase("p@ss")]
	[TestCase("a b")]
	[TestCase("x;rm")]
	public async Task UnsafeCredentialsAreRefused(string password)
	{
		_cluster.AddSecret("chroma-postgres", ("username", "chroma"), ("password", password));
		await PassAsync(NewDistributed());
		Assert.That(Condition("PostgresReady").Reason, Is.EqualTo("CredentialsNotSupported"));
		Assert.That(Condition("PostgresReady").Message, Does.Not.Contain(password), "the password is never in the status");
		await _cluster.Checks.DidNotReceive().PostgresAsync(Arg.Any<PostgresTarget>(), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task FailedChecksCreateNothingAndSayWhy()
	{
		_cluster.Checks.PostgresAsync(Arg.Any<PostgresTarget>(), Arg.Any<CancellationToken>()).Returns(new CheckResult(false, "AuthenticationFailed", "refused"));
		_cluster.Checks.BucketAsync(Arg.Any<BucketTarget>(), Arg.Any<CancellationToken>()).Returns(new CheckResult(false, "VirtualHostNotResolvable", "use the IP"));
		await PassAsync(NewDistributed());
		Assert.That(Condition("PostgresReady").Reason, Is.EqualTo("AuthenticationFailed"));
		Assert.That(Condition("ObjectStorageReady").Reason, Is.EqualTo("VirtualHostNotResolvable"));
		Assert.That(Condition("Ready").Reason, Is.EqualTo("AuthenticationFailed"));
		Assert.That(_cluster.Applied.Concat(_cluster.Created), Is.Empty);
	}

	// The checks get what the spec and the Secrets say, with the defaults of the operator.
	[Test]
	public async Task ChecksGetTheSpecAndTheSecrets()
	{
		await PassAsync(NewDistributed());
		await _cluster.Checks.Received().PostgresAsync(new PostgresTarget("chroma", "postgres", 5432, "sysdb", "disable", "chroma", "s3cret"), Arg.Any<CancellationToken>());
		await _cluster.Checks.Received().BucketAsync(new BucketTarget("chroma", "http://minio.chroma.svc.cluster.local:9000", "chroma-storage", "us-east-1", "key", "secret"), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task ObjectOfSomeoneElseIsNeverChanged()
	{
		_cluster.Objects[("ConfigMap", "query-service-config")] = new V1ConfigMap { Metadata = new V1ObjectMeta { Name = "query-service-config", NamespaceProperty = "chroma" } };
		await PassAsync(NewDistributed());
		Assert.That(Condition("Ready").Reason, Is.EqualTo("NameConflict"));
		Assert.That(Condition("Ready").Message, Does.Contain("ConfigMap query-service-config"));
		Assert.That(_cluster.Applied.Concat(_cluster.Created), Is.Empty);
	}

	// First the objects without Pods, then the migration Job, and nothing else until it completes.
	[Test]
	public async Task FirstPassCreatesTheFoundationAndTheMigration()
	{
		var result = await PassAsync(NewDistributed());
		Assert.That(_cluster.Applied, Has.Count.EqualTo(8 + 8 + 23 + 7 + 6 + 1));
		Assert.That(_cluster.Created, Is.EquivalentTo(DistributedResources.MemberLists.Select(m => "MemberList/" + m.MemberList)));
		Assert.That(_cluster.Applied, Does.Contain("Job/sysdb-migration-" + Tag));
		Assert.That(_cluster.Applied, Has.None.StartWith("Deployment/").And.None.StartWith("StatefulSet/"));
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Migrating"));
		Assert.That(Condition("Migrated").Reason, Is.EqualTo("JobRunning"));
		Assert.That(Condition("PostgresReady").Status, Is.EqualTo("True"));
		Assert.That(result.RequeueAfter, Is.EqualTo(DistributedReconciler.BusyRequeue));
		await _cluster.Client.Received().PatchAsync<V1ConfigMap>(Arg.Any<V1Patch>(), "query-service-config", "chroma", DistributedReconciler.FieldManager, true, null, Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task FailedMigrationStartsNoComponent()
	{
		var chroma = NewDistributed();
		await PassAsync(chroma);
		_cluster.CompleteJob("sysdb-migration-" + Tag, "Failed");
		await PassAsync(chroma);
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition("Ready").Reason, Is.EqualTo("MigrationFailed"));
		Assert.That(Condition("Migrated").Message, Does.Contain("kubectl logs -n chroma job/sysdb-migration-52ce844"));
		Assert.That(_cluster.Applied, Has.None.StartWith("Deployment/").And.None.StartWith("StatefulSet/"));
	}

	// The workloads tier by tier: each waits for the one before to be updated and ready; the frontend comes last.
	[Test]
	public async Task ComponentsStartTierByTier()
	{
		var chroma = NewDistributed();
		await PassAsync(chroma);
		_cluster.CompleteJob("sysdb-migration-" + Tag);

		await PassAsync(chroma);
		Assert.That(Condition("Migrated").Status, Is.EqualTo("True"));
		Assert.That(_cluster.Status!.VersionHistory, Is.EqualTo(new[] { Tag }));
		Assert.That(_cluster.Applied, Does.Contain("Deployment/sysdb").And.No.Contain("StatefulSet/rust-log-service"));
		Assert.That(_cluster.Status.Phase, Is.EqualTo("Creating"));

		_cluster.RollOut("sysdb", 1);
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Does.Contain("StatefulSet/rust-log-service").And.No.Contain("StatefulSet/query-service"));

		_cluster.RollOut("rust-log-service", 1);
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Does.Contain("StatefulSet/query-service").And.Contain("Deployment/fn-consumer").And.No.Contain("Deployment/rust-frontend-service"));

		foreach (var c in new[] { "query-service", "compaction-service", "garbage-collector", "work-queue-service", "fn-consumer" })
			_cluster.RollOut(c, DistributedResources.Replicas(chroma, c));
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Does.Contain("Deployment/rust-frontend-service"));
		Assert.That(_cluster.Status.Phase, Is.EqualTo("Creating"));

		_cluster.AllReady(chroma);
		var result = await PassAsync(chroma);
		Assert.That(_cluster.Status.Phase, Is.EqualTo("Ready"));
		Assert.That(_cluster.Status.Version, Is.EqualTo(Tag));
		Assert.That(_cluster.Status.Endpoint, Is.EqualTo("http://rust-frontend-service.chroma.svc:8000"));
		Assert.That(Condition("Ready").Reason, Is.EqualTo("AllComponentsReady"));
		Assert.That(Condition("MembersReady").Status, Is.EqualTo("True"));
		Assert.That(_cluster.Status.Components!.Single(c => c.Name == "query-service"), Has.Property("Replicas").EqualTo(2).And.Property("Ready").EqualTo(2).And.Property("Members").EqualTo(1));
		Assert.That(_cluster.Status.Components!.Single(c => c.Name == "sysdb").Members, Is.Null);
		Assert.That(result.RequeueAfter, Is.EqualTo(DistributedReconciler.SteadyRequeue));
	}

	// Once everything is applied for a generation, a pass writes only what differs; a new generation writes everything again, so that a
	// field removed from the spec leaves the objects too.
	[Test]
	public async Task OnlyWhatDiffersIsWrittenAgain()
	{
		var chroma = await ReadyAsync();
		_cluster.Applied.Clear();
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Is.Empty, "nothing differs");

		_cluster.Get<V1ConfigMap>("ConfigMap", "query-service-config").Data["config.yaml"] = "edited";
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Is.EqualTo(new[] { "ConfigMap/query-service-config" }));

		_cluster.Applied.Clear();
		chroma.Metadata.Generation = 2;
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Has.Count.GreaterThan(50));
	}

	// The sysdb writes the members: an existing MemberList is never written again, not even at a new generation.
	[Test]
	public async Task MemberListsAreOnlyCreated()
	{
		var chroma = await ReadyAsync();
		chroma.Metadata.Generation = 2;
		_cluster.Created.Clear();
		await PassAsync(chroma);
		Assert.That(_cluster.Created, Is.Empty);
		Assert.That(_cluster.Applied, Has.None.StartWith("MemberList/"));
		Assert.That(_cluster.Get<V1MemberList>("MemberList", "query-service-memberlist").Spec.Members, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task DowngradeIsRefusedWithoutTheAnnotation()
	{
		var chroma = await ReadyAsync();
		_cluster.Status!.VersionHistory = [Tag, "abcdef0"];
		_cluster.Applied.Clear();
		chroma.Metadata.Generation = 2;
		await PassAsync(chroma);
		Assert.That(Condition("UpgradeBlocked").Status, Is.EqualTo("True"));
		Assert.That(Condition("UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
		Assert.That(Condition("UpgradeBlocked").Message, Does.Contain(Labels.AllowDowngrade));
		Assert.That(_cluster.Applied, Is.Empty);
		Assert.That(_cluster.Status.Phase, Is.EqualTo("Ready"), "the components run as they were");

		chroma.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowDowngrade] = "true" };
		await PassAsync(chroma);
		Assert.That(Condition("UpgradeBlocked").Status, Is.EqualTo("False"));
		Assert.That(_cluster.Status.VersionHistory, Is.EqualTo(new[] { "abcdef0", Tag }));
	}

	[Test]
	public async Task OldMigrationJobsAreDeleted()
	{
		var chroma = NewDistributed();
		_cluster.Objects[("Job", "sysdb-migration-abcdef0")] = new V1Job
		{
			Metadata = new V1ObjectMeta
			{
				Name = "sysdb-migration-abcdef0",
				NamespaceProperty = "chroma",
				OwnerReferences = [new V1OwnerReference { Controller = true, Uid = "uid-chart", Kind = "Chroma", Name = "chroma" }],
			},
		};
		await PassAsync(chroma);
		Assert.That(DeletedJobs(), Is.Zero, "not before the new one completes");
		_cluster.CompleteJob("sysdb-migration-" + Tag);
		await PassAsync(chroma);
		Assert.That(DeletedJobs(), Is.EqualTo(1));
	}

	private int DeletedJobs() =>
		_cluster.Client.ApiClient.BatchV1.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IBatchV1Operations.DeleteNamespacedJobWithHttpMessagesAsync));

	// The work queue stays NOT_SERVING when it starts before the storage: after 180 s its Pod is deleted.
	[Test]
	public async Task WorkQueueNotReadyIsRestarted()
	{
		var chroma = await ReadyAsync();
		var statefulSet = _cluster.Get<V1StatefulSet>("StatefulSet", "work-queue-service");
		_cluster.Pods.Add(WorkloadPod("work-queue-service-0", "work-queue-service", statefulSet.Uid(), ready: false, since: Now.AddSeconds(-100)));
		await PassAsync(chroma);
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.Zero, "not before 180 s");

		await PassAsync(chroma, Now.AddSeconds(90));
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.EqualTo(1));
		Assert.That(Condition("Repaired").Reason, Is.EqualTo("WorkQueueRestarted"));
	}

	// The sysdb runs for leader only once: MemberLists missing ready Pods for 180 s, with the sysdb ready as long, restart it.
	[Test]
	public async Task SysdbThatLostTheLeaseIsRestarted()
	{
		var chroma = await ReadyAsync();
		_cluster.Pods.Add(SysdbPod("sysdb-abc", ready: true, since: Now.AddMinutes(-10)));
		_cluster.Get<V1MemberList>("MemberList", "query-service-memberlist").Spec.Members = [];
		await PassAsync(chroma);
		Assert.That(Condition("MembersReady").Status, Is.EqualTo("False"));
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Updating"));
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.Zero, "not before 180 s");

		await PassAsync(chroma, Now.AddSeconds(200));
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.EqualTo(1));
		Assert.That(Condition("Repaired").Reason, Is.EqualTo("SysdbLeaderLost"));
	}

	// A sysdb that has just started is left the time to win the Lease.
	[Test]
	public async Task NewSysdbIsLeftTime()
	{
		var chroma = await ReadyAsync();
		_cluster.Pods.Add(SysdbPod("sysdb-abc", ready: true, since: Now.AddSeconds(170)));
		_cluster.Get<V1MemberList>("MemberList", "query-service-memberlist").Spec.Members = [];
		await PassAsync(chroma);
		await PassAsync(chroma, Now.AddSeconds(300));
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.Zero);
	}

	// The sysdb writes the members with metadata of the name only: the MemberList loses its owner, and is still the one of this Chroma.
	// The other objects of the chart tell a distributed Chroma that is not this one.
	[Test]
	public async Task MemberListStrippedBySysdbIsNotAConflict()
	{
		var chroma = await ReadyAsync();
		var stripped = _cluster.Get<V1MemberList>("MemberList", "rust-log-service-memberlist");
		stripped.Metadata.OwnerReferences = null;
		stripped.Metadata.Labels = null;
		await PassAsync(chroma);
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Ready"));

		stripped.Metadata.OwnerReferences = [new V1OwnerReference { Controller = true, Uid = "uid-other", Kind = "Other", Name = "other" }];
		await PassAsync(chroma);
		Assert.That(Condition("Ready").Reason, Is.EqualTo("NameConflict"));
	}

	[Test]
	public async Task DeletionDeletesTheMemberListsTheSysdbStripped()
	{
		var chroma = await ReadyAsync();
		_cluster.Get<V1MemberList>("MemberList", "rust-log-service-memberlist").Metadata.OwnerReferences = null;
		_cluster.Get<V1MemberList>("MemberList", "fn-consumer-memberlist").Metadata.OwnerReferences =
			[new V1OwnerReference { Controller = true, Uid = "uid-other", Kind = "Other", Name = "other" }];
		await new DistributedReconciler(_cluster.Client, Fixtures.Defaults, _cluster.Checks, NullLogger.Instance).DeletedAsync(chroma, CancellationToken.None);
		var deleted = _cluster.Client.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(KubeOps.KubernetesClient.IKubernetesClient.DeleteAsync))
			.Select(c => ((V1MemberList)c.GetArguments()[0]!).Name()).ToList();
		Assert.That(deleted, Is.EquivalentTo(DistributedResources.MemberLists.Select(m => m.MemberList).Where(m => m != "fn-consumer-memberlist")));
	}

	// Another Pod with the label of the sysdb, not of its Deployment, is never deleted.
	[Test]
	public async Task OnlyThePodOfTheSysdbIsRestarted()
	{
		var chroma = await ReadyAsync();
		_cluster.Pods.Add(WorkloadPod("other", "sysdb", "uid-other", ready: true, since: Now.AddMinutes(-10)));
		_cluster.Get<V1MemberList>("MemberList", "query-service-memberlist").Spec.Members = [];
		await PassAsync(chroma);
		await PassAsync(chroma, Now.AddSeconds(200));
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.Zero);
	}

	// A work queue already stopping is not deleted again at each pass.
	[Test]
	public async Task StoppingWorkQueueIsLeftAlone()
	{
		var chroma = await ReadyAsync();
		var pod = WorkloadPod("work-queue-service-0", "work-queue-service", _cluster.Get<V1StatefulSet>("StatefulSet", "work-queue-service").Uid(), ready: false, since: Now.AddMinutes(-10));
		pod.Metadata.DeletionTimestamp = Now;
		_cluster.Pods.Add(pod);
		await PassAsync(chroma);
		Assert.That(Fixtures.PodDeletes(_cluster.Client), Is.Zero);
	}

	// Once the version is migrated, Postgres or the bucket out of reach from the operator do not stop the components or the repairs:
	// the status says it, and the components reach them on their own.
	[Test]
	public async Task ChecksDoNotStopARunningChroma()
	{
		var chroma = await ReadyAsync();
		_cluster.Checks.PostgresAsync(Arg.Any<PostgresTarget>(), Arg.Any<CancellationToken>()).Returns(new CheckResult(false, "Unreachable", "no answer"));
		await PassAsync(chroma);
		Assert.That(Condition("PostgresReady").Reason, Is.EqualTo("Unreachable"));
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Ready"));
	}

	// The deletion of a distributed Chroma that was refused, since an older one has the namespace, leaves the MemberLists of the other.
	[Test]
	public async Task DeletionOfARefusedChromaLeavesTheMemberLists()
	{
		var chroma = await ReadyAsync();
		foreach (var (memberList, _, _) in DistributedResources.MemberLists)
			_cluster.Get<V1MemberList>("MemberList", memberList).Metadata.OwnerReferences = null;
		var newer = NewDistributed();
		newer.Metadata.Name = "second";
		newer.Metadata.Uid = "uid-second";
		newer.Metadata.CreationTimestamp = Now;
		_cluster.Chromas.AddRange([chroma, newer]);
		await new DistributedReconciler(_cluster.Client, Fixtures.Defaults, _cluster.Checks, NullLogger.Instance).DeletedAsync(newer, CancellationToken.None);
		Assert.That(_cluster.Client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(KubeOps.KubernetesClient.IKubernetesClient.DeleteAsync)), Is.Zero);
	}

	// An old Job deleted by someone else between the list and the delete does not fail the pass.
	[Test]
	public async Task OldJobDeletedMeanwhileIsNoFailure()
	{
		var chroma = NewDistributed();
		_cluster.Objects[("Job", "sysdb-migration-abcdef0")] = new V1Job
		{
			Metadata = new V1ObjectMeta
			{
				Name = "sysdb-migration-abcdef0",
				NamespaceProperty = "chroma",
				OwnerReferences = [new V1OwnerReference { Controller = true, Uid = "uid-chart", Kind = "Chroma", Name = "chroma" }],
			},
		};
		_cluster.Client.ApiClient.BatchV1.DeleteNamespacedJobWithHttpMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
				Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.Returns<Task<k8s.Autorest.HttpOperationResponse<V1Status>>>(_ => throw new k8s.Autorest.HttpOperationException { Response = new k8s.Autorest.HttpResponseMessageWrapper(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "") });
		await PassAsync(chroma);
		_cluster.CompleteJob("sysdb-migration-" + Tag);
		await PassAsync(chroma);
		Assert.That(_cluster.Applied, Does.Contain("Deployment/sysdb"));
	}

	[Test]
	public void CoversIgnoresWhatTheApiAdds()
	{
		var desired = JsonNode.Parse("""{"spec":{"ports":[{"port":50051,"targetPort":50051}],"selector":{"app":"x"}}}""");
		Assert.That(DistributedReconciler.Covers(JsonNode.Parse("""{"spec":{"clusterIP":"10.0.0.1","ports":[{"port":50051,"targetPort":50051,"protocol":"TCP"}],"selector":{"app":"x"}}}"""), desired), Is.True);
		Assert.That(DistributedReconciler.Covers(JsonNode.Parse("""{"spec":{"ports":[{"port":50052,"targetPort":50051}],"selector":{"app":"x"}}}"""), desired), Is.False);
		Assert.That(DistributedReconciler.Covers(JsonNode.Parse("""{"spec":{"ports":[],"selector":{"app":"x"}}}"""), desired), Is.False);
		Assert.That(DistributedReconciler.Covers(JsonNode.Parse("""{"spec":{"ports":[{"port":50051,"targetPort":50051}]}}"""), desired), Is.False);
	}

	private static V1Pod SysdbPod(string name, bool ready, DateTime since)
	{
		var pod = WorkloadPod(name, "sysdb", "uid-rs", ready, since);
		pod.Metadata.OwnerReferences = [new V1OwnerReference { Controller = true, Uid = "uid-rs", Kind = "ReplicaSet", Name = "sysdb-5d8f7c" }];
		return pod;
	}

	private static V1Pod WorkloadPod(string name, string app, string ownerUid, bool ready, DateTime since) => new()
	{
		Metadata = new V1ObjectMeta
		{
			Name = name,
			NamespaceProperty = "chroma",
			Uid = "uid-" + name,
			ResourceVersion = "1",
			Labels = new Dictionary<string, string> { ["app"] = app },
			OwnerReferences = [new V1OwnerReference { Controller = true, Uid = ownerUid, Kind = "StatefulSet", Name = app }],
		},
		Status = new V1PodStatus
		{
			Phase = "Running",
			Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False", LastTransitionTime = since }],
		},
	};

	/// <summary>A distributed Chroma whose components are all ready.</summary>
	private async Task<V1Alpha1Chroma> ReadyAsync()
	{
		var chroma = NewDistributed();
		await PassAsync(chroma);
		_cluster.CompleteJob("sysdb-migration-" + Tag);
		for (var i = 0; i < DistributedResources.Tiers.Count; i++)
		{
			await PassAsync(chroma);
			foreach (var component in DistributedResources.Tiers[i])
				_cluster.RollOut(component, DistributedResources.Replicas(chroma, component));
		}
		_cluster.AllReady(chroma);
		await PassAsync(chroma);
		Assert.That(_cluster.Status!.Phase, Is.EqualTo("Ready"));
		return chroma;
	}
}
