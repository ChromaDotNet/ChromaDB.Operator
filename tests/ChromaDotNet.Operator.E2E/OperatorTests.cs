using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// The operator itself: with two replicas only the holder of the Lease works, and when it goes, the other one takes the Lease and goes
/// on. A webhook that rewrites the image of the Pod does not make the operator replace the Pod again and again.
/// </summary>
public class OperatorTests
{
	private const string Image159 = "docker.io/chromadb/chroma:1.5.9";

	private TestCluster _cluster = null!;

	[SetUp]
	public async Task SetUp() => _cluster = await TestCluster.CreateAsync();

	[TearDown]
	public void TearDown() => _cluster?.Dispose();

	[Test]
	public async Task OnlyTheLeaderWorksAndAnotherTakesOver()
	{
		try
		{
			await _cluster.ScaleOperatorAsync(2);
			var leader = await _cluster.WaitLeaseHolderAsync(except: null);
			await _cluster.CreateAsync(TestCluster.NewServer("lead", Image159));
			await _cluster.WaitReadyAsync("lead", Image159);
			var creators = await _cluster.OperatorPodsThatLoggedAsync("lead: creating Pod lead-1");
			Assert.That(creators, Is.EqualTo(new[] { leader }), "only the leader created the Pod");

			// The leader goes: the other replica takes the Lease and replaces a Pod deleted by hand.
			await _cluster.Api.CoreV1.DeleteNamespacedPodAsync(leader, TestCluster.OperatorNamespace);
			var next = await _cluster.WaitLeaseHolderAsync(except: leader);
			var pod = (await _cluster.PodAsync("lead-1"))!;
			await _cluster.Client.DeleteAsync(pod);
			await _cluster.WaitAsync("lead", s => s.Status?.Phase != "Ready", TimeSpan.FromSeconds(60), "not Ready");
			await _cluster.WaitReadyAsync("lead", Image159);
			Assert.That((await _cluster.PodAsync("lead-1"))!.Uid(), Is.Not.EqualTo(pod.Uid()));
			Assert.That(await _cluster.OperatorPodsThatLoggedAsync("lead: creating Pod lead-1"), Does.Contain(next));
		}
		finally
		{
			await _cluster.ScaleOperatorAsync(1);
		}
	}

	// A mutating webhook, here a policy of Kyverno, rewrites the image of the Pods of the operator at their creation, as a mirror or a
	// policy that adds a digest does: docker.io/chromadb/chroma:1.5.9 becomes chromadb/chroma:1.5.9, the same image. The Pod that runs is
	// the one the operator created: it stays. An edit by hand after that is still replaced (OwnershipTests.PodImageChangedByHandIsReplaced).
	// Kyverno must be on the cluster: build/e2e.sh installs it.
	[Test]
	public async Task PodRewrittenByAWebhookStays()
	{
		await CreateRewritePolicyAsync();
		await _cluster.CreateAsync(TestCluster.NewServer("mirrored", Image159));
		var ready = await _cluster.WaitAsync("mirrored", s => s.Status?.Phase == "Ready", what: "Ready");
		var pod = (await _cluster.PodAsync("mirrored-1"))!;
		Assert.That(pod.Spec.Containers.Single(c => c.Name == "chroma").Image, Is.EqualTo("chromadb/chroma:1.5.9"), "rewritten by the webhook");
		Assert.That(ready.Status!.DataVersion, Is.EqualTo("1.5.9"));

		// Passes go on: every 30 seconds once ready, and at each change of the Pod.
		await Task.Delay(TimeSpan.FromSeconds(40));
		Assert.That((await _cluster.PodAsync("mirrored-1"))!.Uid(), Is.EqualTo(pod.Uid()), "the same Pod");
		Assert.That((await _cluster.GetAsync("mirrored"))!.Status!.Phase, Is.EqualTo("Ready"));
	}

	private async Task CreateRewritePolicyAsync()
	{
		var policy = JsonNode.Parse(@"{
			""apiVersion"": ""kyverno.io/v1"",
			""kind"": ""Policy"",
			""metadata"": { ""name"": ""rewrite-image"" },
			""spec"": { ""rules"": [ {
				""name"": ""short-name"",
				""match"": { ""any"": [ { ""resources"": { ""kinds"": [""Pod""], ""selector"": { ""matchLabels"": { ""app.kubernetes.io/managed-by"": ""chroma-operator"" } } } } ] },
				""mutate"": { ""foreach"": [ {
					""list"": ""request.object.spec.containers"",
					""patchStrategicMerge"": { ""spec"": { ""containers"": [ { ""name"": ""{{ element.name }}"", ""image"": ""{{ replace_all(element.image, 'docker.io/', '') }}"" } ] } }
				} ] }
			} ] }
		}")!;
		try
		{
			await _cluster.Api.CustomObjects.CreateNamespacedCustomObjectAsync(policy, "kyverno.io", "v1", _cluster.Namespace, "policies");
		}
		catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			Assert.Fail("Kyverno is not on the cluster: build/e2e.sh installs it.");
		}
		// Kyverno takes the policy in its webhook a moment after it is ready.
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (watch.Elapsed < TimeSpan.FromMinutes(1))
		{
			var current = (JsonElementOf(await _cluster.Api.CustomObjects.GetNamespacedCustomObjectAsync("kyverno.io", "v1", _cluster.Namespace, "policies", "rewrite-image")));
			if (current?["status"]?["conditions"]?.AsArray().Any(c => c?["type"]?.GetValue<string>() == "Ready" && c?["status"]?.GetValue<string>() == "True") == true)
			{
				await Task.Delay(TimeSpan.FromSeconds(3));
				return;
			}
			await Task.Delay(500);
		}
		Assert.Fail("the policy of Kyverno is not ready");
	}

	private static JsonNode? JsonElementOf(object value) => JsonNode.Parse(KubernetesJson.Serialize(value));
}
