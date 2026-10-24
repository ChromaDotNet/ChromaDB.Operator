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
/// not change after the creation. The distributed mode is in the schema, and the operator refuses it until it is implemented.
/// </summary>
public class ModeTests
{
	private static readonly V1CustomResourceDefinition Crd = KubernetesYaml.LoadAllFromString(File.ReadAllText(ManifestPath()))
		.OfType<V1CustomResourceDefinition>().Single();

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

	// Until the distributed mode is implemented, a Chroma that asks for it gets nothing, and the status says why.
	[Test]
	public async Task DistributedIsRefusedForNow()
	{
		using var client = Substitute.For<IKubernetesClient>();
		V1Alpha1Chroma.ChromaStatus? written = null;
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			written = call.Arg<V1Alpha1Chroma>().Status;
			return call.Arg<V1Alpha1Chroma>();
		});
		// Without an image too: the default image of the operator is not written into the spec of a distributed Chroma.
		var chroma = NewChroma();
		chroma.Spec.Image = null;
		chroma.Spec.Mode = "distributed";
		var result = await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(chroma, CancellationToken.None);
		Assert.That(written?.Phase, Is.EqualTo("Failed"));
		Assert.That(written!.Conditions.Single(c => c.Type == "Ready").Reason, Is.EqualTo("DistributedNotImplemented"));
		Assert.That(result.RequeueAfter, Is.Null, "the mode does not change: no requeue");
		Assert.That(client.ReceivedCalls().Select(c => c.GetMethodInfo().Name), Has.None.EqualTo(nameof(IKubernetesClient.CreateAsync)));
		Assert.That(client.ReceivedCalls().Select(c => c.GetMethodInfo().Name), Has.None.EqualTo(nameof(IKubernetesClient.UpdateAsync)));
	}

	private static string ManifestPath()
	{
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "ChromaDB.Operator.slnx")))
				return Path.Combine(directory.FullName, "deploy", "chroma-operator.yaml");
		}
		throw new FileNotFoundException("ChromaDB.Operator.slnx not found above " + TestContext.CurrentContext.TestDirectory);
	}
}
