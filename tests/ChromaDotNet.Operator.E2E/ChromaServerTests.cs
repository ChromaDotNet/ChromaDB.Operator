using System.Net;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// The v0.1 of the operator on a real cluster: a ChromaServer that answers, a new configuration, an upgrade and a refused downgrade,
/// the rollback of an upgrade that failed, a Pod deleted by hand, an image that cannot be pulled, a container that keeps crashing, and
/// the deletion with both claim policies. The images docker.io/chromadb/chroma:1.5.3 and :1.5.9 must be on the cluster or pullable.
/// </summary>
public class ChromaServerTests
{
	private const string Image153 = "docker.io/chromadb/chroma:1.5.3";
	private const string Image159 = "docker.io/chromadb/chroma:1.5.9";

	private TestCluster _cluster = null!;

	[SetUp]
	public async Task SetUp() => _cluster = await TestCluster.CreateAsync();

	[TearDown]
	public void TearDown() => _cluster?.Dispose();

	[Test]
	public async Task ServerAnswersOnItsService()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("answers", Image159));
		var server = await _cluster.WaitReadyAsync("answers", Image159);

		Assert.That(server.Status!.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(server.Status.Endpoint, Is.EqualTo($"http://answers-rw.{_cluster.Namespace}.svc:8000"));
		Assert.That(server.Status.Conditions.Single(c => c.Type == "Ready").Status, Is.EqualTo("True"));
		var pod = (await _cluster.PodAsync("answers-1"))!;
		Assert.That(pod.Spec.SecurityContext.RunAsNonRoot, Is.True);
		Assert.That(pod.Spec.Containers[0].SecurityContext.ReadOnlyRootFilesystem, Is.True);
		Assert.That(pod.Metadata.OwnerReferences.Single().Kind, Is.EqualTo("ChromaServer"));
		var pvc = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("answers-1", _cluster.Namespace))!;
		Assert.That(pvc.Metadata.Annotations[Labels.Group + "/data-version"], Is.EqualTo("1.5.9"));

		using var forward = await _cluster.ForwardAsync("answers");
		using var chroma = new ChromaHttp(forward.Uri);
		var collection = await chroma.CollectionAsync("answers");
		Assert.That(await chroma.AddAsync(collection, 0, 1000), Is.EqualTo(HttpStatusCode.Created));
		Assert.That(await chroma.CountAsync("answers"), Is.EqualTo(1000));
	}

	// A new configuration goes into the ConfigMap and restarts the Pod; the data stays, and the new limit applies.
	[Test]
	public async Task NewConfigurationRestartsThePodAndKeepsTheData()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("config", Image159));
		await _cluster.WaitReadyAsync("config", Image159);
		var before = (await _cluster.PodAsync("config-1"))!.Uid();
		string[] found;
		using (var forward = await _cluster.ForwardAsync("config"))
		using (var chroma = new ChromaHttp(forward.Uri))
		{
			var collection = await chroma.CollectionAsync("kept");
			await chroma.AddAsync(collection, 0, 2000);
			found = await chroma.QueryAsync("kept", 5);
		}

		await _cluster.UpdateAsync("config", s => s.Spec.Config = new V1Alpha1ChromaServer.ConfigSpec { MaxPayloadSizeBytes = 1000 });
		await _cluster.WaitAsync("config", s => s.Status?.Phase == "Ready" && s.Status.ObservedGeneration == s.Metadata.Generation, what: "Ready after the change");
		var after = (await _cluster.PodAsync("config-1"))!;
		Assert.That(after.Uid(), Is.Not.EqualTo(before), "a new Pod");

		using (var forward = await _cluster.ForwardAsync("config"))
		using (var chroma = new ChromaHttp(forward.Uri))
		{
			Assert.That(await chroma.CountAsync("kept"), Is.EqualTo(2000));
			Assert.That(await chroma.QueryAsync("kept", 5), Is.EqualTo(found));
			var collection = await chroma.CollectionAsync("kept");
			Assert.That(await chroma.AddAsync(collection, 5000, 100), Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), "100 records are about 9 KB: over max_payload_size_bytes 1000");
		}
	}

	// 1.5.3 to 1.5.9: the data version follows. Back to 1.5.3: refused, the Pod of 1.5.9 stays; with the annotation it goes.
	[Test]
	public async Task UpgradeAndRefusedDowngrade()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("versions", Image153));
		await _cluster.WaitReadyAsync("versions", Image153);
		using (var forward = await _cluster.ForwardAsync("versions"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("versions"), 0, 1500);

		await _cluster.UpdateAsync("versions", s => s.Spec.Image = Image159);
		var upgraded = await _cluster.WaitReadyAsync("versions", Image159);
		Assert.That(upgraded.Status!.DataVersion, Is.EqualTo("1.5.9"));
		var pod = (await _cluster.PodAsync("versions-1"))!;

		await _cluster.UpdateAsync("versions", s => s.Spec.Image = Image153);
		var blocked = await _cluster.WaitAsync("versions",
			s => s.Status?.ObservedGeneration == s.Metadata.Generation && s.Status!.Conditions.Any(c => c.Type == "UpgradeBlocked" && c.Status == "True"),
			what: "UpgradeBlocked");
		Assert.That(blocked.Status!.Image, Is.EqualTo(Image159));
		await Task.Delay(TimeSpan.FromSeconds(10));
		var still = (await _cluster.PodAsync("versions-1"))!;
		Assert.That(still.Uid(), Is.EqualTo(pod.Uid()), "the Pod of 1.5.9 stays");
		Assert.That(still.Spec.Containers[0].Image, Is.EqualTo(Image159));

		await _cluster.UpdateAsync("versions", s => (s.Metadata.Annotations ??= new Dictionary<string, string>())[Labels.AllowDowngrade] = "true");
		var downgraded = await _cluster.WaitReadyAsync("versions", Image153);
		Assert.That(downgraded.Status!.DataVersion, Is.EqualTo("1.5.9"), "a downgrade does not lower the data version");
		using (var forward = await _cluster.ForwardAsync("versions"))
		using (var chroma = new ChromaHttp(forward.Uri))
			Assert.That(await chroma.CountAsync("versions"), Is.EqualTo(1500));
	}

	[Test]
	public async Task DeletedPodComesBackWithTheData()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("again", Image159));
		await _cluster.WaitReadyAsync("again", Image159);
		using (var forward = await _cluster.ForwardAsync("again"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("again"), 0, 700);
		var pod = (await _cluster.PodAsync("again-1"))!;

		// The watch of the Pods puts the server in the queue at once: without it the periodic pass, every 30 seconds, found the Pod missing.
		// Only the reaction of the operator is timed, from the Pod gone to the new Pod, not the stop and the start of Chroma.
		await _cluster.Client.DeleteAsync(pod);
		var stopping = System.Diagnostics.Stopwatch.StartNew();
		while (await _cluster.PodAsync("again-1") is { } old && old.Uid() == pod.Uid())
		{
			if (stopping.Elapsed > TimeSpan.FromMinutes(1))
				Assert.Fail("the deleted Pod is still there");
			await Task.Delay(100);
		}
		var reaction = System.Diagnostics.Stopwatch.StartNew();
		V1Pod? created;
		while ((created = await _cluster.PodAsync("again-1")) is null && reaction.Elapsed < Reconciliation.SteadyRequeue)
			await Task.Delay(100);
		TestContext.Out.WriteLine($"again: new Pod {reaction.Elapsed.TotalSeconds:0.0} s after the old one was gone");
		Assert.That(created, Is.Not.Null);
		Assert.That(reaction.Elapsed, Is.LessThan(Reconciliation.SteadyRequeue / 3), "sooner than the periodic pass");
		await _cluster.WaitReadyAsync("again", Image159);
		using (var forward = await _cluster.ForwardAsync("again"))
		using (var chroma = new ChromaHttp(forward.Uri))
			Assert.That(await chroma.CountAsync("again"), Is.EqualTo(700));
	}

	// Without an image, the default of the operator goes into the spec, and stays there.
	[Test]
	public async Task DefaultImageIsWrittenIntoTheSpec()
	{
		var server = TestCluster.NewServer("default", Image159);
		server.Spec.Image = null;
		await _cluster.CreateAsync(server);
		var ready = await _cluster.WaitReadyAsync("default", Image159);
		Assert.That(ready.Spec.Image, Is.EqualTo(Image159));
		Assert.That(ready.Status!.DataVersion, Is.EqualTo("1.5.9"));
	}

	// An image outside the repositories the operator allows starts nothing.
	[Test]
	public async Task ImageOutsideTheAllowedRepositoriesStartsNothing()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("other", "docker.io/library/busybox:1.36.1"));
		var refused = await _cluster.WaitAsync("other", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		Assert.That(refused.Status!.Conditions.Single(c => c.Type == "UpgradeBlocked").Reason, Is.EqualTo("ImageNotAllowed"));
		await Task.Delay(TimeSpan.FromSeconds(5));
		Assert.That(await _cluster.PodAsync("other-1"), Is.Null);
	}

	[TestCase("docker.io/chromadb/chroma:latest")]
	[TestCase("docker.io/chromadb/chroma:1.5.10.dev313")]
	[TestCase("docker.io/chromadb/chroma")]
	public void ImageWithoutAnExactVersionIsRefusedByTheApi(string image)
	{
		var e = Assert.ThrowsAsync<HttpOperationException>(() => _cluster.CreateAsync(TestCluster.NewServer("refused", image)));
		Assert.That(e!.Response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), e.Response.Content);
	}

	// Retain, the default: the claim stays with the data, and a ChromaServer created again on it knows the data version. With the version
	// of the data it starts again, with all the records.
	[Test]
	public async Task DeletionWithRetainKeepsTheClaim()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("retain", Image159));
		await _cluster.WaitReadyAsync("retain", Image159);
		using (var forward = await _cluster.ForwardAsync("retain"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("kept"), 0, 1200);
		await _cluster.Client.DeleteAsync<V1Alpha1ChromaServer>("retain", _cluster.Namespace);
		await _cluster.WaitGoneAsync<V1Alpha1ChromaServer>("retain");
		await _cluster.AssertClaimKeptAsync("retain");

		await _cluster.CreateAsync(TestCluster.NewServer("retain", Image153));
		var refused = await _cluster.WaitAsync("retain", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed on older data");
		Assert.That(refused.Status!.DataVersion, Is.EqualTo("1.5.9"));
		Assert.That(await _cluster.PodAsync("retain-1"), Is.Null, "no Pod of 1.5.3 on data of 1.5.9");

		await _cluster.UpdateAsync("retain", s => s.Spec.Image = Image159);
		await _cluster.WaitReadyAsync("retain", Image159);
		using (var forward = await _cluster.ForwardAsync("retain"))
		using (var chroma = new ChromaHttp(forward.Uri))
			Assert.That(await chroma.CountAsync("kept"), Is.EqualTo(1200));
	}

	// An upgrade to an image that never starts, here one that is not on the node, with the pull policy Never for it alone. The data
	// version went up before the Pod was created, since the operator cannot know whether it will run: going back to 1.5.3 is a downgrade,
	// refused until the annotation allows it. Then 1.5.3 runs again on the same data.
	[Test]
	public async Task RollbackAfterAFailedUpgradeNeedsTheAnnotation()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("rollback", Image153));
		await _cluster.WaitReadyAsync("rollback", Image153);
		using (var forward = await _cluster.ForwardAsync("rollback"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("kept"), 0, 900);

		const string missing = "docker.io/chromadb/chroma:1.5.99";
		await _cluster.UpdateAsync("rollback", s =>
		{
			s.Spec.Image = missing;
			s.Spec.ImagePullPolicy = "Never";
		});
		var failed = await _cluster.WaitAsync("rollback",
			s => s.Status?.Conditions.Any(c => c.Type == "Ready" && c.Reason == "ErrImageNeverPull") == true, TimeSpan.FromSeconds(90), "ErrImageNeverPull");
		Assert.That(failed.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(failed.Status.DataVersion, Is.EqualTo("1.5.99"));

		await _cluster.UpdateAsync("rollback", s =>
		{
			s.Spec.Image = Image153;
			s.Spec.ImagePullPolicy = null;
		});
		var blocked = await _cluster.WaitAsync("rollback",
			s => s.Status?.ObservedGeneration == s.Metadata.Generation && s.Status!.Conditions.Any(c => c.Type == "UpgradeBlocked" && c.Reason == "Downgrade"),
			TimeSpan.FromSeconds(60), "Downgrade");
		Assert.That(blocked.Status!.Phase, Is.EqualTo("Failed"));

		await _cluster.UpdateAsync("rollback", s => (s.Metadata.Annotations ??= new Dictionary<string, string>())[Labels.AllowDowngrade] = "true");
		var back = await _cluster.WaitReadyAsync("rollback", Image153);
		Assert.That(back.Status!.DataVersion, Is.EqualTo("1.5.99"), "never lowered");
		using (var forward = await _cluster.ForwardAsync("rollback"))
		using (var chroma = new ChromaHttp(forward.Uri))
			Assert.That(await chroma.CountAsync("kept"), Is.EqualTo(900));
	}

	// An image the kubelet cannot pull: the server fails with the reason of the kubelet, and the Pod stays to be retried.
	[Test]
	public async Task ImageThatCannotBePulledFailsTheServer()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("pull", "docker.io/chromadb/chroma:9.9.9"));
		var failed = await _cluster.WaitAsync("pull", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(120), "Failed");
		Assert.That(failed.Status!.Conditions.Single(c => c.Type == "Ready").Reason, Is.AnyOf("ErrImagePull", "ImagePullBackOff"));
		Assert.That(await _cluster.PodAsync("pull-1"), Is.Not.Null);
	}

	// Chroma exits at its start on a claim whose chroma.sqlite3 is not a database: the server fails with CrashLoopBackOff, the Pod is
	// not replaced, and the version that ran is recorded.
	[Test]
	public async Task ContainerThatKeepsCrashingFailsTheServer()
	{
		await _cluster.Client.CreateAsync(new V1PersistentVolumeClaim
		{
			Metadata = new V1ObjectMeta
			{
				Name = "broken-1",
				NamespaceProperty = _cluster.Namespace,
				Labels = new Dictionary<string, string> { [Labels.Server] = "broken", ["app.kubernetes.io/managed-by"] = Labels.ManagedBy },
			},
			Spec = new V1PersistentVolumeClaimSpec
			{
				AccessModes = ["ReadWriteOnce"],
				Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") } },
			},
		}.Initialize());
		await _cluster.Client.CreateAsync(new V1Pod
		{
			Metadata = new V1ObjectMeta { Name = "corrupt", NamespaceProperty = _cluster.Namespace },
			Spec = new V1PodSpec
			{
				RestartPolicy = "Never",
				Containers =
				[
					new V1Container
					{
						Name = "write",
						Image = Image159,
						ImagePullPolicy = "IfNotPresent",
						Command = ["sh", "-c", "printf 'not a database' > /data/chroma.sqlite3 && chmod 666 /data/chroma.sqlite3"],
						VolumeMounts = [new V1VolumeMount { Name = "data", MountPath = "/data" }],
					},
				],
				Volumes = [new V1Volume { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "broken-1" } }],
			},
		}.Initialize());
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while ((await _cluster.PodAsync("corrupt"))?.Status?.Phase != "Succeeded")
		{
			if (watch.Elapsed > TimeSpan.FromMinutes(2))
				Assert.Fail("the Pod that writes chroma.sqlite3 did not finish");
			await Task.Delay(500);
		}
		await _cluster.Client.DeleteAsync<V1Pod>("corrupt", _cluster.Namespace);

		await _cluster.CreateAsync(TestCluster.NewServer("broken", Image159));
		var failed = await _cluster.WaitAsync("broken",
			s => s.Status?.Conditions.Any(c => c.Type == "Ready" && c.Reason == "CrashLoopBackOff") == true, TimeSpan.FromSeconds(120), "CrashLoopBackOff");
		Assert.That(failed.Status!.Phase, Is.EqualTo("Failed"));
		Assert.That(failed.Status.DataVersion, Is.EqualTo("1.5.9"));
		var pod = (await _cluster.PodAsync("broken-1"))!;
		Assert.That(pod.Status.ContainerStatuses.Single(c => c.Name == "chroma").RestartCount, Is.GreaterThan(0));
		await Task.Delay(TimeSpan.FromSeconds(5));
		Assert.That((await _cluster.PodAsync("broken-1"))!.Uid(), Is.EqualTo(pod.Uid()), "the kubelet restarts the container: the Pod stays");
	}

	[Test]
	public async Task DeletionWithDeleteDeletesTheClaim()
	{
		var server = TestCluster.NewServer("delete", Image159);
		server.Spec.PvcDeletionPolicy = "Delete";
		await _cluster.CreateAsync(server);
		await _cluster.WaitReadyAsync("delete", Image159);
		await _cluster.Client.DeleteAsync<V1Alpha1ChromaServer>("delete", _cluster.Namespace);
		await _cluster.WaitGoneAsync<V1Pod>("delete-1");
		await _cluster.WaitGoneAsync<V1PersistentVolumeClaim>("delete-1");
	}
}
