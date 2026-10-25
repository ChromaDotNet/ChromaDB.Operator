using System.Net;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// Distributed Chroma on the test cluster, with Postgres and SeaweedFS in the namespace of the test (distributed-deps.yaml). On a machine
/// without AVX-512, like the k3d cluster of this repository, only the sysdb, the migration Job and the log service run: the images of
/// query, compaction, garbage collector, work queue and fn-consumer end with SIGILL (exit code 132). The tests check what runs there;
/// the search, the restarts and the upgrades need a cluster with AVX-512.
/// </summary>
public class DistributedTests
{
	private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(6);

	private TestCluster _cluster = null!;

	[SetUp]
	public async Task SetUp() => _cluster = await TestCluster.CreateAsync();

	[TearDown]
	public void TearDown() => _cluster?.Dispose();

	private static V1Condition? Condition(V1Alpha1Chroma chroma, string type) => chroma.Status?.Conditions.FirstOrDefault(c => c.Type == type);

	private static V1Alpha1Chroma.ComponentStatus? Component(V1Alpha1Chroma chroma, string name) => chroma.Status?.Components?.FirstOrDefault(c => c.Name == name);

	// The migration first, then the tiers: sysdb, log service, the workers; the frontend waits for them. The sysdb lists the log service
	// in its MemberList. The work queue, not ready for 180 s, is recreated. At the deletion everything goes, the MemberLists too, and
	// Postgres and the bucket stay.
	[Test]
	public async Task StartsTierByTierAndGoesWithTheChroma()
	{
		var endpoint = await _cluster.CreateDistributedDependenciesAsync();
		await _cluster.CreateAsync(TestCluster.NewDistributed("chroma", endpoint));
		var migrated = await _cluster.WaitAsync("chroma", c => Condition(c, "Migrated")?.Status == "True", StartTimeout, "Migrated");
		Assert.That(migrated.Status!.VersionHistory, Is.EqualTo(new[] { "52ce844" }));
		Assert.That(Condition(migrated, "PostgresReady")?.Status, Is.EqualTo("True"));
		Assert.That(Condition(migrated, "ObjectStorageReady")?.Status, Is.EqualTo("True"));

		var listed = await _cluster.WaitAsync("chroma", c => Component(c, DistributedResources.LogService) is { Ready: 1, Members: 1 }, StartTimeout, "log service ready and listed");
		Assert.That(Component(listed, DistributedResources.Sysdb)!.Ready, Is.EqualTo(1));
		var sysdb = await _cluster.Client.GetAsync<V1Deployment>(DistributedResources.Sysdb, _cluster.Namespace);
		Assert.That(sysdb!.Metadata.OwnerReferences.Single(o => o.Controller == true).Uid, Is.EqualTo(listed.Uid()));

		// The workers come after the log service; the frontend after the workers.
		await _cluster.WaitExistsAsync<V1StatefulSet>(DistributedResources.QueryService, StartTimeout);
		var query = Component((await _cluster.GetAsync("chroma"))!, DistributedResources.QueryService)!;
		if (query.Ready < query.Replicas)
			Assert.That(await _cluster.Client.GetAsync<V1Deployment>(DistributedResources.Frontend, _cluster.Namespace), Is.Null, "the frontend waits for the workers");

		// The work queue: on a CPU without AVX-512 it ends with SIGILL, so it is never ready, and the operator recreates it after 180 s.
		// On a CPU with AVX-512 it starts, and gets ready some seconds after its creation: its repair is then not checked. The work queue
		// was just created with the query service, so the test waits for whichever comes first.
		var settled = await _cluster.WaitAsync("chroma",
			c => Condition(c, "Repaired")?.Reason == "WorkQueueRestarted" || Component(c, DistributedResources.WorkQueue) is { Ready: 1 },
			TimeSpan.FromMinutes(5), "work queue restarted or ready");
		if (Condition(settled, "Repaired")?.Reason == "WorkQueueRestarted")
			Assert.That(Condition(settled, "Repaired")!.Message, Does.Contain("work-queue-service-0"));
		else
			TestContext.Out.WriteLine("The work queue is ready here: its repair is not checked.");

		await _cluster.Client.DeleteAsync(listed);
		await _cluster.WaitGoneAsync<V1Deployment>(DistributedResources.Sysdb);
		await _cluster.WaitGoneAsync<V1StatefulSet>(DistributedResources.LogService);
		await _cluster.WaitGoneAsync<V1ConfigMap>("rust-log-service-config");
		await _cluster.WaitGoneAsync<V1Job>("sysdb-migration-52ce844");
		await _cluster.WaitGoneAsync<V1MemberList>("rust-log-service-memberlist");
		await _cluster.WaitGoneAsync<V1MemberList>("query-service-memberlist");
		Assert.That(await _cluster.Client.GetAsync<V1Deployment>("postgres", _cluster.Namespace), Is.Not.Null, "Postgres stays");
		Assert.That(await _cluster.Client.GetAsync<V1Secret>("chroma-storage", _cluster.Namespace), Is.Not.Null, "the Secrets stay");
	}

	// The sysdb runs for leader once: after it loses the Lease nobody lists the members. With a MemberList missing a ready Pod for 180
	// s, the operator restarts the sysdb, which wins the Lease again and lists it.
	[Test]
	public async Task SysdbThatLostTheLeaseIsRestarted()
	{
		var endpoint = await _cluster.CreateDistributedDependenciesAsync();
		await _cluster.CreateAsync(TestCluster.NewDistributed("chroma", endpoint));
		await _cluster.WaitAsync("chroma", c => Component(c, DistributedResources.LogService) is { Ready: 1, Members: 1 }, StartTimeout, "log service ready and listed");

		// Another holder takes the Lease for 30 s, then the members are removed: the sysdb no longer lists them.
		var lease = await _cluster.Api.CoordinationV1.ReadNamespacedLeaseAsync("sysdb-leader", _cluster.Namespace);
		lease.Spec.HolderIdentity = "e2e-other-holder";
		lease.Spec.AcquireTime = lease.Spec.RenewTime = DateTime.UtcNow;
		lease.Spec.LeaseDurationSeconds = 30;
		await _cluster.Api.CoordinationV1.ReplaceNamespacedLeaseAsync(lease, "sysdb-leader", _cluster.Namespace);
		await Task.Delay(TimeSpan.FromSeconds(20));
		var memberList = (await _cluster.Client.GetAsync<V1MemberList>("rust-log-service-memberlist", _cluster.Namespace))!;
		memberList.Spec.Members = [];
		await _cluster.Client.UpdateAsync(memberList);

		await _cluster.WaitAsync("chroma", c => Condition(c, "MembersReady")?.Status == "False", TimeSpan.FromMinutes(1), "MembersReady False");
		var repaired = await _cluster.WaitAsync("chroma", c => Condition(c, "Repaired")?.Reason == "SysdbLeaderLost", TimeSpan.FromMinutes(5), "sysdb restarted");
		// Repaired can be True already, for the work queue: the seconds are in its message.
		var seconds = int.Parse(System.Text.RegularExpressions.Regex.Match(Condition(repaired, "Repaired")!.Message, @"for (\d+) s").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
		Assert.That(seconds, Is.GreaterThanOrEqualTo(180));
		await _cluster.WaitAsync("chroma", c => Condition(c, "MembersReady")?.Status == "True" && Component(c, DistributedResources.LogService)?.Members == 1,
			StartTimeout, "log service listed again");
	}

	// Nothing starts before the Secrets, Postgres and the bucket are there, and the status says what is missing.
	[Test]
	public async Task DependenciesAreCheckedFirst()
	{
		await _cluster.CreateAsync(TestCluster.NewDistributed("chroma", "http://10.0.0.1:8333"));
		var noSecret = await _cluster.WaitAsync("chroma", c => Condition(c, "PostgresReady")?.Reason == "SecretNotFound", TimeSpan.FromMinutes(1), "SecretNotFound");
		Assert.That(noSecret.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(Condition(noSecret, "ObjectStorageReady")?.Reason, Is.EqualTo("SecretNotFound"));

		var endpoint = await _cluster.CreateDistributedDependenciesAsync(bucket: false);
		await _cluster.UpdateAsync("chroma", c => c.Spec.Distributed!.ObjectStorage.S3.Endpoint = endpoint);
		await _cluster.WaitAsync("chroma", c => Condition(c, "ObjectStorageReady")?.Reason == "BucketNotFound" && Condition(c, "PostgresReady")?.Status == "True",
			TimeSpan.FromMinutes(1), "BucketNotFound, Postgres ready");

		await _cluster.UpdateAsync("chroma", c => c.Spec.Distributed!.ObjectStorage.S3.Endpoint = "http://seaweedfs:8333");
		var byName = await _cluster.WaitAsync("chroma", c => Condition(c, "ObjectStorageReady")?.Reason == "VirtualHostNotResolvable", TimeSpan.FromMinutes(1), "VirtualHostNotResolvable");
		Assert.That(Condition(byName, "ObjectStorageReady")!.Message, Does.Contain("chroma-storage.seaweedfs"));

		var secret = await _cluster.Api.CoreV1.ReadNamespacedSecretAsync("chroma-postgres", _cluster.Namespace);
		secret.Data["password"] = "wrong-password"u8.ToArray();
		await _cluster.Api.CoreV1.ReplaceNamespacedSecretAsync(secret, "chroma-postgres", _cluster.Namespace);
		await _cluster.WaitAsync("chroma", c => Condition(c, "PostgresReady")?.Reason == "AuthenticationFailed", TimeSpan.FromMinutes(1), "AuthenticationFailed");

		Assert.That(await _cluster.Client.GetAsync<V1Job>("sysdb-migration-52ce844", _cluster.Namespace), Is.Null, "no migration");
		Assert.That(await _cluster.Client.GetAsync<V1Deployment>(DistributedResources.Sysdb, _cluster.Namespace), Is.Null, "no component");
	}

	// The fields of a mode are refused in the other one, and what the chart does not let change is refused for the components.
	[Test]
	public void ApiRefusesWhatTheModeDoesNotHave()
	{
		var withStorage = TestCluster.NewDistributed("refused", "http://10.0.0.1:8333");
		withStorage.Spec.Storage = new V1Alpha1Chroma.StorageSpec { Size = "1Gi" };
		AssertRefused(withStorage, "the fields of single-node");

		var withoutDistributed = TestCluster.NewDistributed("refused", "http://10.0.0.1:8333");
		withoutDistributed.Spec.Distributed = null;
		AssertRefused(withoutDistributed, "spec.distributed is required");

		var workQueue = TestCluster.NewDistributed("refused", "http://10.0.0.1:8333");
		workQueue.Spec.Distributed!.WorkQueue = new V1Alpha1Chroma.ComponentSpec { Replicas = 2 };
		AssertRefused(workQueue, "one replica of the work queue");

		var latest = TestCluster.NewDistributed("refused", "http://10.0.0.1:8333");
		latest.Spec.Distributed!.Version = "latest";
		AssertRefused(latest, "spec.distributed.version");
	}

	private void AssertRefused(V1Alpha1Chroma chroma, string message)
	{
		var e = Assert.ThrowsAsync<HttpOperationException>(() => _cluster.CreateAsync(chroma));
		Assert.That(e!.Response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), e.Response.Content);
		Assert.That(e.Response.Content, Does.Contain(message));
	}

	// The objects of the chart have fixed names: a second distributed Chroma in the namespace waits, and says why.
	[Test]
	public async Task OneDistributedChromaPerNamespace()
	{
		await _cluster.CreateAsync(TestCluster.NewDistributed("first", "http://10.0.0.1:8333"));
		await _cluster.WaitAsync("first", c => c.Status?.Phase is not null, TimeSpan.FromMinutes(1), "a status");
		await _cluster.CreateAsync(TestCluster.NewDistributed("second", "http://10.0.0.1:8333"));
		var second = await _cluster.WaitAsync("second", c => Condition(c, "Ready")?.Reason == "DistributedNamespaceTaken", TimeSpan.FromMinutes(1), "DistributedNamespaceTaken");
		Assert.That(Condition(second, "Ready")!.Message, Does.Contain("first"));
		var first = (await _cluster.GetAsync("first"))!;
		Assert.That(Condition(first, "Ready")?.Reason, Is.Not.EqualTo("DistributedNamespaceTaken"));
	}
}
