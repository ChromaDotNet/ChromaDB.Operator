using ChromaDotNet.Operator.Controllers;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// One kind, Chroma, with the mode in the spec, as the operator of Milvus does: single-node by default, or distributed. The mode does
/// not change after the creation, and each mode has its own fields.
/// </summary>
public class ModeTests
{
	private static readonly V1CustomResourceDefinition Crd = KubernetesYaml.LoadAllFromString(File.ReadAllText(ManifestPath()))
		.OfType<V1CustomResourceDefinition>().Single(c => c.Metadata.Name == "chromas.chroma.chromadotnet.org");

	[Test]
	public void KindIsChroma()
	{
		Assert.That((Crd.Spec.Group, Crd.Spec.Names.Kind, Crd.Spec.Names.Plural, Crd.Spec.Names.Singular),
			Is.EqualTo(("chroma.chromadotnet.org", "Chroma", "chromas", "chroma")));
		Assert.That(Crd.Metadata.Name, Is.EqualTo("chromas.chroma.chromadotnet.org"));
	}

	[Test]
	public void ModeIsSingleNodeByDefaultAndCannotChange()
	{
		var mode = Crd.Spec.Versions.Single().Schema.OpenAPIV3Schema.Properties["spec"].Properties["mode"];
		Assert.That(KubernetesJson.Serialize(mode.DefaultProperty), Is.EqualTo("\"single-node\""));
		Assert.That(mode.Pattern, Is.EqualTo("^(single-node|distributed)$"));
		var rule = mode.XKubernetesValidations.Single();
		Assert.That(rule.Rule, Is.EqualTo("self == oldSelf"));
		Assert.That(rule.Message, Does.Contain("spec.mode cannot change after creation"));
	}

	// Each mode has its fields: storage for single-node, distributed for distributed, and none of single-node in distributed.
	[Test]
	public void EachModeHasItsFields()
	{
		var rule = Crd.Spec.Versions.Single().Schema.OpenAPIV3Schema.XKubernetesValidations.Single(r => r.Rule.Contains("self.spec.mode", StringComparison.Ordinal));
		Assert.That(rule.Rule, Does.Contain("has(self.spec.distributed) && !has(self.spec.storage) && !has(self.spec.image)"));
		Assert.That(rule.Rule, Does.EndWith(": has(self.spec.storage) && !has(self.spec.distributed)"));
		Assert.That(Crd.Spec.Versions.Single().Schema.OpenAPIV3Schema.Properties["spec"].Required ?? [], Has.No.Member("storage"));
	}

	// A distributed Chroma has its own pass: no Pod, no claim, and the default image of single-node is not written into its spec. Here
	// the Secret of Postgres is missing, and the status says so.
	[Test]
	public async Task DistributedHasItsOwnPass()
	{
		using var client = Substitute.For<IKubernetesClient>();
		V1Alpha1Chroma.ChromaStatus? written = null;
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			written = call.Arg<V1Alpha1Chroma>().Status;
			return call.Arg<V1Alpha1Chroma>();
		});
		var chroma = ChartTests.LoadChroma("default");
		await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(chroma, CancellationToken.None);
		Assert.That(written?.Phase, Is.EqualTo("Failed"));
		Assert.That(written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("SecretNotFound"));
		Assert.That(chroma.Spec.Image, Is.Null);
		Assert.That(client.ReceivedCalls().Select(c => c.GetMethodInfo().Name), Has.None.EqualTo(nameof(IKubernetesClient.CreateAsync)));
		Assert.That(client.ReceivedCalls().Select(c => c.GetMethodInfo().Name), Has.None.EqualTo(nameof(IKubernetesClient.UpdateAsync)));
	}

	private static string ManifestPath() => RepositoryFile("deploy", "chroma-operator.yaml");
}
