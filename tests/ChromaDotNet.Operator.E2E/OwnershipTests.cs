using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// Objects with the names of those of a ChromaServer that belong to someone else, the owner of the claim against pvcDeletionPolicy,
/// the annotations of the Service, and names that would not give valid names to the objects.
/// </summary>
public class OwnershipTests
{
	private const string Image159 = "docker.io/chromadb/chroma:1.5.9";

	private TestCluster _cluster = null!;

	[SetUp]
	public async Task SetUp() => _cluster = await TestCluster.CreateAsync();

	[TearDown]
	public void TearDown() => _cluster?.Dispose();

	[TestCase("team.db")]
	[TestCase("Demo")]
	[TestCase("a234567890123456789012345678901234567890123456789012345678901")]
	public void NameThatGivesInvalidNamesIsRefusedByTheApi(string name)
	{
		var e = Assert.ThrowsAsync<HttpOperationException>(() => _cluster.CreateAsync(TestCluster.NewServer(name, Image159)));
		Assert.That(e!.Response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), e.Response.Content);
	}

	// A ConfigMap of someone else with the name <name>-config: the server fails, the ConfigMap stays as it was, no Pod starts.
	[Test]
	public async Task ConfigMapOfSomeoneElseIsNotTouched()
	{
		await _cluster.Client.CreateAsync(new V1ConfigMap
		{
			Metadata = new V1ObjectMeta { Name = "taken-config", NamespaceProperty = _cluster.Namespace },
			Data = new Dictionary<string, string> { ["app.yaml"] = "theirs" },
		}.Initialize());
		await _cluster.CreateAsync(TestCluster.NewServer("taken", Image159));
		var failed = await _cluster.WaitAsync("taken", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		var ready = failed.Status!.Conditions.Single(c => c.Type == "Ready");
		Assert.That(ready.Reason, Is.EqualTo("NameConflict"));
		Assert.That(ready.Message, Does.Contain("taken-config"));
		var configMap = (await _cluster.Client.GetAsync<V1ConfigMap>("taken-config", _cluster.Namespace))!;
		Assert.That(configMap.Data, Is.EqualTo(new Dictionary<string, string> { ["app.yaml"] = "theirs" }));
		Assert.That(await _cluster.PodAsync("taken-1"), Is.Null);
	}

	// A claim not created by the operator is not mounted: its data could belong to another application. Labelled, it is used.
	[Test]
	public async Task ClaimOfSomeoneElseIsUsedOnlyOnceLabelled()
	{
		var claim = new V1PersistentVolumeClaim
		{
			Metadata = new V1ObjectMeta { Name = "found-1", NamespaceProperty = _cluster.Namespace },
			Spec = new V1PersistentVolumeClaimSpec
			{
				AccessModes = ["ReadWriteOnce"],
				Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") } },
			},
		}.Initialize();
		await _cluster.Client.CreateAsync(claim);
		await _cluster.CreateAsync(TestCluster.NewServer("found", Image159));
		var failed = await _cluster.WaitAsync("found", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		Assert.That(failed.Status!.Conditions.Single(c => c.Type == "Ready").Message, Does.Contain("label it"));
		Assert.That(await _cluster.PodAsync("found-1"), Is.Null);

		claim = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("found-1", _cluster.Namespace))!;
		claim.Metadata.Labels = new Dictionary<string, string> { [Labels.Server] = "found", ["app.kubernetes.io/managed-by"] = Labels.ManagedBy };
		await _cluster.Client.UpdateAsync(claim);
		await _cluster.WaitReadyAsync("found", Image159);
	}

	// pvcDeletionPolicy decides the owner of the claim, also when it changes.
	[Test]
	public async Task ClaimOwnerFollowsThePolicy()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("policy", Image159));
		var server = await _cluster.WaitReadyAsync("policy", Image159);
		Assert.That(await ClaimOwnersAsync(), Is.Empty, "Retain");

		await _cluster.UpdateAsync("policy", s => s.Spec.PvcDeletionPolicy = "Delete");
		await WaitClaimOwnersAsync(owners => owners.Count == 1 && owners[0].Uid == server.Uid() && owners[0].Controller == true, "owned with Delete");
		await _cluster.UpdateAsync("policy", s => s.Spec.PvcDeletionPolicy = "Retain");
		await WaitClaimOwnersAsync(owners => owners.Count == 0, "not owned with Retain");

		await _cluster.Client.DeleteAsync<ChromaDotNet.Operator.Entities.V1Alpha1ChromaServer>("policy", _cluster.Namespace);
		await Task.Delay(TimeSpan.FromSeconds(10));
		Assert.That(await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("policy-1", _cluster.Namespace), Is.Not.Null, "Retain keeps the claim");
	}

	// A Service of someone else blocks the server; a change of pvcDeletionPolicy still reaches the claim, so that deleting the server
	// keeps the data.
	[Test]
	public async Task PolicyReachesTheClaimWhileAnotherObjectBlocksTheServer()
	{
		var server = TestCluster.NewServer("blocked", Image159);
		server.Spec.PvcDeletionPolicy = "Delete";
		await _cluster.CreateAsync(server);
		await _cluster.WaitReadyAsync("blocked", Image159);
		await _cluster.Client.DeleteAsync<V1Service>("blocked-rw", _cluster.Namespace);
		await _cluster.Client.CreateAsync(new V1Service
		{
			Metadata = new V1ObjectMeta { Name = "blocked-rw", NamespaceProperty = _cluster.Namespace },
			Spec = new V1ServiceSpec { Selector = new Dictionary<string, string> { ["app"] = "theirs" }, Ports = [new V1ServicePort { Port = 80 }] },
		}.Initialize());
		await _cluster.WaitAsync("blocked", s => s.Status?.Conditions.Any(c => c.Reason == "NameConflict") == true, TimeSpan.FromSeconds(60), "NameConflict");

		await _cluster.UpdateAsync("blocked", s => s.Spec.PvcDeletionPolicy = "Retain");
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while ((await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("blocked-1", _cluster.Namespace))!.Metadata.OwnerReferences is { Count: > 0 })
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(60))
				Assert.Fail("the claim is still owned by the server");
			await Task.Delay(500);
		}
		await _cluster.Client.DeleteAsync<ChromaDotNet.Operator.Entities.V1Alpha1ChromaServer>("blocked", _cluster.Namespace);
		await Task.Delay(TimeSpan.FromSeconds(10));
		Assert.That(await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("blocked-1", _cluster.Namespace), Is.Not.Null);
	}

	// A Service update the API refuses, here an annotation key that is not a qualified name: the server fails with the reason, and a
	// change of pvcDeletionPolicy still reaches the claim. A valid spec makes it ready again.
	[Test]
	public async Task RefusedServiceUpdateDoesNotStopThePolicy()
	{
		var server = TestCluster.NewServer("refused", Image159);
		server.Spec.PvcDeletionPolicy = "Delete";
		await _cluster.CreateAsync(server);
		await _cluster.WaitReadyAsync("refused", Image159);
		await _cluster.UpdateAsync("refused", s =>
		{
			s.Spec.PvcDeletionPolicy = "Retain";
			s.Spec.Service = new() { Annotations = new Dictionary<string, string> { ["bad key"] = "x" } };
		});
		var failed = await _cluster.WaitAsync("refused",
			s => s.Status?.Conditions.Any(c => c.Type == "Ready" && c.Reason == "ServiceRefused") == true, TimeSpan.FromSeconds(60), "ServiceRefused");
		Assert.That(failed.Status!.Phase, Is.EqualTo("Failed"));
		var claim = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("refused-1", _cluster.Namespace))!;
		Assert.That(claim.Metadata.OwnerReferences ?? [], Is.Empty, "Retain applied");

		await _cluster.UpdateAsync("refused", s => s.Spec.Service = null);
		await _cluster.WaitAsync("refused", s => s.Status?.Phase == "Ready" && s.Status.ObservedGeneration == s.Metadata.Generation, what: "Ready again");
	}

	// A claim the API refuses (a size of 0) and a Pod it refuses (a request above its limit): the server fails with the explanation.
	[Test]
	public async Task RefusedClaimFailsTheServer()
	{
		var server = TestCluster.NewServer("zero", Image159);
		server.Spec.Storage.Size = "0Gi";
		await _cluster.CreateAsync(server);
		var failed = await _cluster.WaitAsync("zero", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		var ready = failed.Status!.Conditions.Single(c => c.Type == "Ready");
		Assert.That(ready.Reason, Is.EqualTo("PersistentVolumeClaimRefused"));
		Assert.That(ready.Message, Does.Contain("zero-1"));
	}

	// A ResourceQuota that allows no Pod, then no claim: the API answers 403, and the server fails with the explanation.
	[TestCase("pods", "PodRefused")]
	[TestCase("persistentvolumeclaims", "PersistentVolumeClaimRefused")]
	public async Task QuotaFailsTheServer(string resource, string reason)
	{
		await _cluster.Client.CreateAsync(new V1ResourceQuota
		{
			Metadata = new V1ObjectMeta { Name = "none", NamespaceProperty = _cluster.Namespace },
			Spec = new V1ResourceQuotaSpec { Hard = new Dictionary<string, ResourceQuantity> { [resource] = new("0") } },
		}.Initialize());
		await Task.Delay(TimeSpan.FromSeconds(2));
		await _cluster.CreateAsync(TestCluster.NewServer("quota", Image159));
		var failed = await _cluster.WaitAsync("quota", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		var ready = failed.Status!.Conditions.Single(c => c.Type == "Ready");
		Assert.That(ready.Reason, Is.EqualTo(reason));
		Assert.That(ready.Message, Does.Contain("quota"));
	}

	// A change to resources the API refuses (a request above its limit) on a ready server: the running Pod stays and serves.
	[Test]
	public async Task RefusedReplacementKeepsTheRunningPod()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("keep", Image159));
		await _cluster.WaitReadyAsync("keep", Image159);
		var pod = (await _cluster.PodAsync("keep-1"))!;
		await _cluster.UpdateAsync("keep", s => s.Spec.Resources = new V1ResourceRequirements
		{
			Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("2") },
			Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("1") },
		});
		var failed = await _cluster.WaitAsync("keep",
			s => s.Status?.ObservedGeneration == s.Metadata.Generation && s.Status!.Conditions.Any(c => c.Type == "Ready" && c.Reason == "PodRefused"),
			TimeSpan.FromSeconds(60), "PodRefused");
		Assert.That(failed.Status!.Conditions.Single(c => c.Type == "Ready").Message, Does.Contain("running one stays"));
		await Task.Delay(TimeSpan.FromSeconds(5));
		var still = (await _cluster.PodAsync("keep-1"))!;
		Assert.That(still.Uid(), Is.EqualTo(pod.Uid()), "the running Pod stays");
		Assert.That(still.Metadata.DeletionTimestamp, Is.Null);
		Assert.That(Reconciliation.IsReady(still), Is.True);

		await _cluster.UpdateAsync("keep", s => s.Spec.Resources = TestCluster.NewServer("x", Image159).Spec.Resources);
		await _cluster.WaitAsync("keep", s => s.Status?.Phase == "Ready" && s.Status.ObservedGeneration == s.Metadata.Generation, what: "Ready again");
	}

	[Test]
	public async Task RefusedPodFailsTheServer()
	{
		var server = TestCluster.NewServer("greedy", Image159);
		server.Spec.Resources = new V1ResourceRequirements
		{
			Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("2") },
			Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("1") },
		};
		await _cluster.CreateAsync(server);
		var failed = await _cluster.WaitAsync("greedy", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed");
		Assert.That(failed.Status!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("PodRefused"));
		Assert.That(await _cluster.PodAsync("greedy-1"), Is.Null);
	}

	// While a refused Service fails the server, the Pod is edited by hand from 1.5.3 to 1.5.9: the failed passes record 1.5.9. Once the
	// Service is fixed, 1.5.3 is refused, and after the loss of the Pod the server starts again with 1.5.9, not 1.5.3.
	[Test]
	public async Task FailedPassesRecordTheVersionOfThePod()
	{
		const string image153 = "docker.io/chromadb/chroma:1.5.3";
		await _cluster.CreateAsync(TestCluster.NewServer("record", image153));
		await _cluster.WaitReadyAsync("record", image153);
		await _cluster.UpdateAsync("record", s => s.Spec.Service = new() { Annotations = new Dictionary<string, string> { ["bad key"] = "x" } });
		await _cluster.WaitAsync("record", s => s.Status?.Conditions.Any(c => c.Reason == "ServiceRefused") == true, TimeSpan.FromSeconds(60), "ServiceRefused");

		var pod = (await _cluster.PodAsync("record-1"))!;
		pod.Spec.Containers.Single(c => c.Name == "chroma").Image = Image159;
		await _cluster.Client.UpdateAsync(pod);
		await _cluster.WaitAsync("record", s => s.Status?.DataVersion == "1.5.9", TimeSpan.FromSeconds(60), "1.5.9 recorded while failing");

		await _cluster.UpdateAsync("record", s => s.Spec.Service = null);
		var blocked = await _cluster.WaitAsync("record",
			s => s.Status?.ObservedGeneration == s.Metadata.Generation && s.Status!.Conditions.Any(c => c.Type == "UpgradeBlocked" && c.Status == "True"),
			TimeSpan.FromSeconds(60), "UpgradeBlocked");
		Assert.That(blocked.Status!.RecoveryImage, Is.EqualTo(Image159));

		await _cluster.Client.DeleteAsync((await _cluster.PodAsync("record-1"))!);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		V1Pod? again;
		do
		{
			await Task.Delay(500);
			again = await _cluster.PodAsync("record-1");
		}
		while ((again is null || again.Uid() == pod.Uid()) && watch.Elapsed < TimeSpan.FromSeconds(60));
		Assert.That(again, Is.Not.Null, "a new Pod");
		Assert.That(again!.Uid(), Is.Not.EqualTo(pod.Uid()), "a new Pod, not the edited one");
		Assert.That(again.Spec.Containers.Single(c => c.Name == "chroma").Image, Is.EqualTo(Image159), "not 1.5.3 on data of 1.5.9");
	}

	// The claim cannot be updated (a ValidatingAdmissionPolicy denies it) when a Pod edited to 1.5.9 raises the data version: the status
	// keeps 1.5.9 and the server fails with PersistentVolumeClaimRefused; the Pod, deleted, is not recreated while the claim is refused.
	// Once the policy is gone, the server starts again with 1.5.9, not with the 1.5.3 of its spec.
	[Test]
	public async Task RefusedClaimUpdateKeepsTheVersionInTheStatus()
	{
		const string image153 = "docker.io/chromadb/chroma:1.5.3";
		var policyName = "chroma-e2e-freeze-" + _cluster.Namespace;
		await _cluster.CreateAsync(TestCluster.NewServer("frozen", image153));
		await _cluster.WaitReadyAsync("frozen", image153);
		try
		{
			await _cluster.Client.CreateAsync(new V1ValidatingAdmissionPolicy
			{
				Metadata = new V1ObjectMeta { Name = policyName },
				Spec = new V1ValidatingAdmissionPolicySpec
				{
					FailurePolicy = "Fail",
					MatchConstraints = new V1MatchResources
					{
						ResourceRules = [new V1NamedRuleWithOperations { ApiGroups = [""], ApiVersions = ["v1"], Operations = ["UPDATE"], Resources = ["persistentvolumeclaims"] }],
					},
					Validations = [new V1Validation { Expression = "false", Message = "claims are frozen for the test", Reason = "Forbidden" }],
				},
			}.Initialize());
			await _cluster.Client.CreateAsync(new V1ValidatingAdmissionPolicyBinding
			{
				Metadata = new V1ObjectMeta { Name = policyName },
				Spec = new V1ValidatingAdmissionPolicyBindingSpec
				{
					PolicyName = policyName,
					ValidationActions = ["Deny"],
					MatchResources = new V1MatchResources
					{
						NamespaceSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = _cluster.Namespace } },
					},
				},
			}.Initialize());
			await Task.Delay(TimeSpan.FromSeconds(3));

			var pod = (await _cluster.PodAsync("frozen-1"))!;
			pod.Spec.Containers.Single(c => c.Name == "chroma").Image = Image159;
			await _cluster.Client.UpdateAsync(pod);
			var refused = await _cluster.WaitAsync("frozen",
				s => s.Status?.DataVersion == "1.5.9" && s.Status.Conditions.Any(c => c.Type == "Ready" && c.Reason == "PersistentVolumeClaimRefused"),
				TimeSpan.FromSeconds(60), "1.5.9 in the status, claim refused");
			Assert.That(refused.Status!.Phase, Is.EqualTo("Failed"));

			await _cluster.Client.DeleteAsync((await _cluster.PodAsync("frozen-1"))!);
			var watch = System.Diagnostics.Stopwatch.StartNew();
			while (await _cluster.PodAsync("frozen-1") is { } still && still.Uid() == pod.Uid() && watch.Elapsed < TimeSpan.FromSeconds(60))
				await Task.Delay(500);
			await Task.Delay(TimeSpan.FromSeconds(5));
			Assert.That(await _cluster.PodAsync("frozen-1"), Is.Null, "no Pod while the claim cannot be written");
		}
		finally
		{
			try { await _cluster.Client.DeleteAsync<V1ValidatingAdmissionPolicyBinding>(policyName, null); } catch (k8s.Autorest.HttpOperationException) { }
			try { await _cluster.Client.DeleteAsync<V1ValidatingAdmissionPolicy>(policyName, null); } catch (k8s.Autorest.HttpOperationException) { }
		}
		var watchAgain = System.Diagnostics.Stopwatch.StartNew();
		V1Pod? again;
		do
		{
			await Task.Delay(1000);
			again = await _cluster.PodAsync("frozen-1");
		}
		while (again is null && watchAgain.Elapsed < TimeSpan.FromSeconds(90));
		Assert.That(again, Is.Not.Null, "a new Pod once the claim can be written");
		Assert.That(again!.Spec.Containers.Single(c => c.Name == "chroma").Image, Is.EqualTo(Image159), "not 1.5.3 on data of 1.5.9");
		var claim = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("frozen-1", _cluster.Namespace))!;
		Assert.That(claim.Metadata.Annotations[Labels.ClaimDataVersion], Is.EqualTo("1.5.9"));
	}

	// A Pod edited by hand to an image without a version: the data version is unknown, the Pod is not replaced, and after its loss no
	// Pod starts, until the administrator says the version.
	[Test]
	public async Task PodEditedToAnImageWithoutAVersionStopsTheServer()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("unknown", Image159));
		await _cluster.WaitReadyAsync("unknown", Image159);
		var pod = (await _cluster.PodAsync("unknown-1"))!;
		pod.Spec.Containers.Single(c => c.Name == "chroma").Image = "docker.io/chromadb/chroma:unversioned-for-test";
		await _cluster.Client.UpdateAsync(pod);
		var blocked = await _cluster.WaitAsync("unknown", s => s.Status?.DataVersionUnknown == true, TimeSpan.FromSeconds(60), "DataVersionUnknown");
		Assert.That(blocked.Status!.Conditions.Single(c => c.Type == "UpgradeBlocked").Reason, Is.EqualTo("DataVersionUnknown"));
		var claim = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("unknown-1", _cluster.Namespace))!;
		Assert.That(claim.Metadata.Annotations[Labels.ClaimDataVersionUnknown], Is.EqualTo(blocked.Status.DataVersionUnknownIncident), "the code of the incident");
		await Task.Delay(TimeSpan.FromSeconds(5));
		Assert.That((await _cluster.PodAsync("unknown-1"))!.Uid(), Is.EqualTo(pod.Uid()), "not replaced");

		await _cluster.Client.DeleteAsync((await _cluster.PodAsync("unknown-1"))!);
		await _cluster.WaitAsync("unknown", s => s.Status?.Phase == "Failed", TimeSpan.FromSeconds(60), "Failed without a Pod");
		await Task.Delay(TimeSpan.FromSeconds(5));
		Assert.That(await _cluster.PodAsync("unknown-1"), Is.Null, "no Pod on data of an unknown version");

		var incident = (await _cluster.GetAsync("unknown"))!.Status!.DataVersionUnknownIncident;
		Assert.That(incident, Is.Not.Null);
		await _cluster.UpdateAsync("unknown", s => (s.Metadata.Annotations ??= new Dictionary<string, string>())[Labels.DeclaredDataVersion] = "1.5.9/" + incident);
		var ready = await _cluster.WaitReadyAsync("unknown", Image159);
		Assert.That(ready.Status!.DataVersionUnknown, Is.False);
		claim = (await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("unknown-1", _cluster.Namespace))!;
		Assert.That(claim.Metadata.Annotations.ContainsKey(Labels.ClaimDataVersionUnknown), Is.False);
	}

	// Selector and port of the Service changed by hand are restored.
	[Test]
	public async Task ServiceRoutingIsRestored()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("routes", Image159));
		await _cluster.WaitReadyAsync("routes", Image159);
		var service = (await _cluster.Client.GetAsync<V1Service>("routes-rw", _cluster.Namespace))!;
		service.Spec.Selector = new Dictionary<string, string> { ["app"] = "other" };
		service.Spec.Ports[0].TargetPort = 9000;
		await _cluster.Client.UpdateAsync(service);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		do
		{
			await Task.Delay(1000);
			service = (await _cluster.Client.GetAsync<V1Service>("routes-rw", _cluster.Namespace))!;
		}
		while (service.Spec.Selector?.ContainsKey(Labels.Server) != true && watch.Elapsed < TimeSpan.FromSeconds(90));
		Assert.That(service.Spec.Selector, Is.EqualTo(new Dictionary<string, string> { [Labels.Server] = "routes" }));
		Assert.That(service.Spec.Ports.Single().TargetPort.Value, Is.EqualTo("http"));
		TestContext.Out.WriteLine($"routes: restored after {watch.Elapsed.TotalSeconds:0} s");
	}

	// The label the Service selects the Pod by, removed or changed by hand: the operator puts it back, without restarting the Pod.
	[TestCase(null)]
	[TestCase("other")]
	public async Task LabelOfThePodIsRestored(string? value)
	{
		await _cluster.CreateAsync(TestCluster.NewServer("labels", Image159));
		await _cluster.WaitReadyAsync("labels", Image159);
		var pod = (await _cluster.PodAsync("labels-1"))!;
		if (value is null)
			pod.Metadata.Labels.Remove(Labels.Server);
		else
			pod.Metadata.Labels[Labels.Server] = value;
		await _cluster.Client.UpdateAsync(pod);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		V1Pod now;
		do
		{
			await Task.Delay(500);
			now = (await _cluster.PodAsync("labels-1"))!;
		}
		while ((now.Metadata.Labels?.TryGetValue(Labels.Server, out var label) != true || label != "labels") && watch.Elapsed < TimeSpan.FromSeconds(60));
		Assert.That(now.Metadata.Labels?[Labels.Server], Is.EqualTo("labels"));
		Assert.That(now.Uid(), Is.EqualTo(pod.Uid()), "the same Pod");
		TestContext.Out.WriteLine($"labels: restored after {watch.Elapsed.TotalSeconds:0.0} s");
		Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "the watch of the Pods finds the server by the owner");
	}

	// The image of the Pod changed by hand: the operator replaces the Pod with the image of the spec.
	[Test]
	public async Task PodImageChangedByHandIsReplaced()
	{
		await _cluster.CreateAsync(TestCluster.NewServer("edited", Image159));
		await _cluster.WaitReadyAsync("edited", Image159);
		var pod = (await _cluster.PodAsync("edited-1"))!;
		pod.Spec.Containers[0].Image = "docker.io/chromadb/chroma:1.5.3";
		await _cluster.Client.UpdateAsync(pod);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		V1Pod? now;
		do
		{
			await Task.Delay(1000);
			now = await _cluster.PodAsync("edited-1");
		}
		while ((now is null || now.Uid() == pod.Uid()) && watch.Elapsed < TimeSpan.FromSeconds(90));
		Assert.That(now!.Uid(), Is.Not.EqualTo(pod.Uid()));
		Assert.That(now!.Spec.Containers.Single(c => c.Name == "chroma").Image, Is.EqualTo(Image159));
		await _cluster.WaitReadyAsync("edited", Image159);
	}

	// Annotations removed from the spec are removed from the Service; those of others stay.
	[Test]
	public async Task ServiceAnnotationsFollowTheSpec()
	{
		var server = TestCluster.NewServer("notes", Image159);
		server.Spec.Service = new() { Annotations = new Dictionary<string, string> { ["example.com/a"] = "1", ["example.com/b"] = "2" } };
		await _cluster.CreateAsync(server);
		await _cluster.WaitReadyAsync("notes", Image159);
		var service = (await _cluster.Client.GetAsync<V1Service>("notes-rw", _cluster.Namespace))!;
		service.Metadata.Annotations["other.example.com/kept"] = "yes";
		await _cluster.Client.UpdateAsync(service);

		await _cluster.UpdateAsync("notes", s => s.Spec.Service = new() { Annotations = new Dictionary<string, string> { ["example.com/a"] = "3" } });
		var watch = System.Diagnostics.Stopwatch.StartNew();
		IDictionary<string, string> annotations;
		do
		{
			await Task.Delay(500);
			annotations = (await _cluster.Client.GetAsync<V1Service>("notes-rw", _cluster.Namespace))!.Metadata.Annotations;
		}
		while (annotations.ContainsKey("example.com/b") && watch.Elapsed < TimeSpan.FromSeconds(60));
		Assert.That(annotations.ContainsKey("example.com/b"), Is.False);
		Assert.That(annotations["example.com/a"], Is.EqualTo("3"));
		Assert.That(annotations["other.example.com/kept"], Is.EqualTo("yes"));
	}

	private async Task<List<V1OwnerReference>> ClaimOwnersAsync() =>
		(await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("policy-1", _cluster.Namespace))!.Metadata.OwnerReferences?.ToList() ?? [];

	private async Task WaitClaimOwnersAsync(Func<List<V1OwnerReference>, bool> condition, string what)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition(await ClaimOwnersAsync()))
		{
			if (watch.Elapsed > TimeSpan.FromSeconds(60))
				Assert.Fail($"claim: {what} not reached");
			await Task.Delay(500);
		}
	}
}
