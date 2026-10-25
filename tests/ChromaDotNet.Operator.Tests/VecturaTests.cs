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
/// VecturaDB in a single-node Chroma: the product of an image, of the data, and the passages from one to the other. Only the passages
/// of the table run, each tried on a volume first; VecturaDB to Chroma needs the annotation, with what Chroma does not see of the data.
/// </summary>
public class VecturaTests
{
	private const string VecturaPrevious = "ghcr.io/vecturadb/vecturadb:1.0.0-preview.2";
	private const string Vectura1 = "ghcr.io/vecturadb/vecturadb:1.0.0-preview.3";
	private const string Vectura2 = "ghcr.io/vecturadb/vecturadb:1.0.0-preview.4";

	private static V1Alpha1Chroma OnData(string image, string? product, string? dataVersion, string? runningImage = null)
	{
		var server = NewChroma(image, dataVersion: dataVersion, runningImage: runningImage);
		server.Status.DataProduct = product;
		return server;
	}

	private static Outcome Decide(V1Alpha1Chroma server, V1Pod? pod) => Reconciliation.Decide(server, pod, null, Now, Defaults);

	[TestCase("docker.io/chromadb/chroma:1.5.9", "chroma")]
	[TestCase("chromadb/chroma:1.5.9", "chroma")]
	[TestCase("ghcr.io/chroma-core/chroma:1.5.9", "chroma")]
	[TestCase(Vectura1, "vecturadb")]
	[TestCase("registry.example.com/vecturadb:1.0.0-preview.3", null)]
	public void ProductOfTheRepository(string image, string? product) => Assert.That(Products.OfRepository(image), Is.EqualTo(product));

	// A mirror: spec.product says the product of the repository of spec.image, and of no other.
	[Test]
	public void ProductOfAMirror()
	{
		var server = NewChroma("registry.example.com/vecturadb:1.0.0-preview.3");
		server.Spec.Product = "vecturadb";
		Assert.That(Products.Of(server, "registry.example.com/vecturadb:1.0.0-preview.4"), Is.EqualTo("vecturadb"));
		Assert.That(Products.Of(server, "registry.example.com/other:1.0.0"), Is.Null);
		Assert.That(Products.Of(server, Image159), Is.EqualTo("chroma"), "a known repository keeps its product");
		Assert.That(Products.Error(server), Is.Null);
	}

	[Test]
	public void UnknownRepositoryWithoutProductIsRefused()
	{
		var server = OnData("registry.example.com/vecturadb:1.0.0-preview.3", "chroma", "1.5.9", Image159);
		var outcome = Reconciliation.Decide(server, RunningPod(server, Image159), null, Now, new OperatorOptions(allowedImages: "*"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("UnknownProduct"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("set spec.product"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None), "the running Pod stays");
	}

	[Test]
	public void ProductAgainstItsRepositoryIsRefused()
	{
		var server = NewChroma(Image159);
		server.Spec.Product = "vecturadb";
		var outcome = Decide(server, null);
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("ProductMismatch"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// A row of the table: Chroma 1.5.9 to VecturaDB 1.0.0-preview.3. 1.0.0 is not compared with 1.5.9: it is not a downgrade.
	[Test]
	public void ChromaToVecturaAlongTheTable()
	{
		var server = OnData(Vectura1, "chroma", "1.5.9", Image159);
		var replaced = Decide(server, RunningPod(server, Image159));
		Assert.That(replaced.Action, Is.EqualTo(PodAction.Delete));
		Assert.That(Condition(replaced, "UpgradeBlocked").Status, Is.EqualTo("False"));

		var created = Decide(server, null);
		Assert.That(created.Action, Is.EqualTo(PodAction.Create));
		Assert.That(created.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Vectura1));
		Assert.That((created.Status.DataProduct, created.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")), "written before the Pod");
	}

	// A row of the table within VecturaDB: from the release before, 1.0.0-preview.2, to 1.0.0-preview.3.
	[Test]
	public void PreviousVecturaToTheLatestAlongTheTable()
	{
		var server = OnData(Vectura1, "vecturadb", "1.0.0-preview.2", VecturaPrevious);
		var replaced = Decide(server, RunningPod(server, VecturaPrevious));
		Assert.That(replaced.Action, Is.EqualTo(PodAction.Delete));
		Assert.That(Condition(replaced, "UpgradeBlocked").Status, Is.EqualTo("False"));

		var created = Decide(server, null);
		Assert.That(created.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Vectura1));
		Assert.That((created.Status.DataProduct, created.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
	}

	// Chroma 1.5.3 is not in the table: VecturaDB may not know what Chroma did to the data before 1.5.9, or after.
	[TestCase("1.5.3")]
	[TestCase("1.5.10")]
	public void ChromaToVecturaOutsideTheTableIsRefused(string chroma)
	{
		var running = "docker.io/chromadb/chroma:" + chroma;
		var server = OnData(Vectura1, "chroma", chroma, running);
		var outcome = Decide(server, RunningPod(server, running));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("ProductChangeNotKnown"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("Chroma 1.5.9 to VecturaDB 1.0.0-preview.3").And.Contain("The running image stays."));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// VecturaDB to Chroma keeps the data, but Chroma does not see all of it: refused without the annotation, and the message says
	// exactly what happens, never that the data is lost.
	[Test]
	public void VecturaToChromaNeedsTheAnnotation()
	{
		var server = OnData(Image159, "vecturadb", "1.0.0-preview.3", Vectura1);
		var outcome = Decide(server, RunningPod(server, Vectura1));
		var blocked = Condition(outcome, "UpgradeBlocked");
		Assert.That(blocked.Reason, Is.EqualTo("ProductChange"));
		Assert.That(blocked.Message, Does.StartWith("The data was last opened by VecturaDB 1.0.0-preview.3. Chroma reads the records, documents and metadata, " +
			"but not the sparse vectors, which stay in the data, and sparse, BM25 and Search queries fail. Writes from Chroma to collections with BM25 fail " +
			"with the Python client, and other clients write records without their BM25 vectors, or keep the old ones, until their documents are written " +
			"again with VecturaDB. Do not write with Chroma to data of VecturaDB that uses sparse vectors or BM25: " +
			"https://vecturadb.org/leaving-chroma-cloud/to-vecturadb.html To switch anyway, annotate the Chroma with chroma.chromadotnet.org/allow-product-change: \"true\"."));
		Assert.That(blocked.Message, Does.Not.Contain("loss").IgnoreCase.And.Not.Contain("lost").IgnoreCase);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// Forced, Chroma runs, the warning stays in the status, and the data is of Chroma: back to VecturaDB along the table again.
	[Test]
	public void VecturaToChromaForcedAndBack()
	{
		var server = OnData(Image159, "vecturadb", "1.0.0-preview.3", Vectura1);
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowProductChange] = "true" };
		var created = Decide(server, null);
		Assert.That(created.Action, Is.EqualTo(PodAction.Create));
		var warning = Condition(created, "UpgradeBlocked");
		Assert.That((warning.Status, warning.Reason), Is.EqualTo(("False", "ProductChangeAllowed")));
		Assert.That(warning.Message, Does.Contain("but not the sparse vectors, which stay in the data").And.Contain("vecturadb.org"));
		Assert.That((created.Status.DataProduct, created.Status.DataVersion), Is.EqualTo(("chroma", "1.5.9")));

		Assert.That(created.Status.DataVersions, Is.EquivalentTo(new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" }));

		var back = OnData(Vectura1, created.Status.DataProduct, created.Status.DataVersion, Image159);
		back.Status.DataVersions = created.Status.DataVersions;
		Assert.That(Decide(back, null).Action, Is.EqualTo(PodAction.Create));
	}

	// The highest version of Chroma stays through VecturaDB: Chroma 1.0.0 on data that Chroma 1.5.9 opened is a downgrade, also with the
	// annotation of the passage.
	[Test]
	public void HighestChromaStaysThroughVectura()
	{
		var start = OnData(Vectura1, "chroma", "1.5.9", Image159);
		var created = Decide(start, null);
		Assert.That(created.Status.DataVersions, Is.EquivalentTo(new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" }));

		var server = OnData("docker.io/chromadb/chroma:1.0.0", "vecturadb", "1.0.0-preview.3", Vectura1);
		server.Status.DataVersions = created.Status.DataVersions;
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowProductChange] = "true" };
		var outcome = Decide(server, RunningPod(server, Vectura1));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("opened by 1.5.9"));
	}

	// The highest version of VecturaDB stays through a forced Chroma: 1.0.0-preview.2 on data that 1.0.0-preview.3 opened is a downgrade,
	// although Chroma 1.5.9 to 1.0.0-preview.2 is a row of the table.
	[Test]
	public void HighestVecturaStaysThroughChroma()
	{
		var vectura = OnData(Image159, "vecturadb", "1.0.0-preview.3", Vectura1);
		vectura.Status.DataVersions = new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" };
		vectura.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowProductChange] = "true" };
		var forced = Decide(vectura, null);
		Assert.That((forced.Status.DataProduct, forced.Status.DataVersion), Is.EqualTo(("chroma", "1.5.9")));
		Assert.That(forced.Status.DataVersions, Is.EquivalentTo(new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" }));

		var server = OnData(VecturaPrevious, "chroma", "1.5.9", Image159);
		server.Status.DataVersions = forced.Status.DataVersions;
		var outcome = Decide(server, RunningPod(server, Image159));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("1.0.0-preview.3"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	// Chroma on data that VecturaDB opened keeps the warning while it runs, not only at the pass that starts it.
	[Test]
	public void WarningStaysWhileChromaRuns()
	{
		var server = OnData(Image159, "chroma", "1.5.9", Image159);
		server.Status.DataVersions = new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" };
		var outcome = Decide(server, RunningPod(server, Image159));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Ready"));
		var warning = Condition(outcome, "UpgradeBlocked");
		Assert.That((warning.Status, warning.Reason), Is.EqualTo(("False", "ProductChangeAllowed")));
		Assert.That(warning.Message, Does.Contain("opened by VecturaDB 1.0.0-preview.3"));
	}

	// The status and the claim disagree on the product, as after a write that failed: VecturaDB, and Chroma needs the annotation.
	[Test]
	public void DisagreementIsVectura()
	{
		var server = OnData(Image159, "chroma", "1.5.9");
		var outcome = Reconciliation.Decide(server, null, "1.0.0-preview.3", Now, Defaults, pvcDataProduct: "vecturadb");
		Assert.That(outcome.Status.DataProduct, Is.EqualTo("vecturadb"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("ProductChange"));
	}

	// A Pod edited in place to VecturaDB while the kubelet reports the Chroma that ran: VecturaDB opened the data.
	[Test]
	public void BothProductsSeenIsVectura()
	{
		var server = OnData(Vectura1, "chroma", "1.5.9");
		var pod = RunningPod(server, Vectura1, ready: false);
		pod.Status.ContainerStatuses[0].Image = Image159;
		var outcome = Decide(server, pod);
		Assert.That(outcome.Status.DataProduct, Is.EqualTo("vecturadb"));
		Assert.That(outcome.Status.DataVersions, Is.EquivalentTo(new Dictionary<string, string> { ["chroma"] = "1.5.9", ["vecturadb"] = "1.0.0-preview.3" }));
	}

	// An image of VecturaDB without a version, put in the Pod by hand: the version is unknown, of VecturaDB, and the declaration too.
	[Test]
	public void UnknownVersionOfVectura()
	{
		var server = OnData(Image159, "chroma", "1.5.9");
		var pod = RunningPod(server, "ghcr.io/vecturadb/vecturadb:preview");
		var outcome = Decide(server, pod);
		Assert.That(outcome.Status.DataVersionUnknown, Is.True);
		Assert.That(outcome.Status.DataProduct, Is.EqualTo("vecturadb"));
		Assert.That(outcome.Status.DataVersions, Is.EquivalentTo(new Dictionary<string, string> { ["chroma"] = "1.5.9" }), "the version of Chroma stays");
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("highest version of VecturaDB"));
	}

	// Annotations written by hand in another case are the same products.
	[TestCase("VecturaDB")]
	[TestCase(" vecturadb ")]
	public void ProductOfTheClaimInAnyCase(string written)
	{
		var outcome = Reconciliation.Decide(NewChroma(Image159), null, "1.0.0-preview.3", Now, Defaults, pvcDataProduct: written);
		Assert.That(outcome.Status.DataProduct, Is.EqualTo("vecturadb"));
	}

	// A newer VecturaDB runs only once its passage is in the table, after a trial: not deduced from the versions, not forced.
	[Test]
	public void NewerVecturaOutsideTheTableIsRefused()
	{
		var server = OnData(Vectura2, "vecturadb", "1.0.0-preview.3", Vectura1);
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowDowngrade] = "true", [Labels.AllowProductChange] = "true" };
		var outcome = Decide(server, RunningPod(server, Vectura1));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("UpgradeNotKnown"));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
	}

	[Test]
	public void OlderVecturaIsADowngrade()
	{
		var server = OnData(Vectura1, "vecturadb", "1.0.0-preview.4", Vectura2);
		var outcome = Decide(server, RunningPod(server, Vectura2));
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
		Assert.That(Condition(outcome, "UpgradeBlocked").Message, Does.Contain("VecturaDB does not open data of a newer version").And.Contain(Labels.AllowDowngrade));
		server.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowDowngrade] = "true" };
		Assert.That(Decide(server, RunningPod(server, Vectura2)).Action, Is.EqualTo(PodAction.Delete));
	}

	// Data nothing has opened takes VecturaDB, which becomes the product of the data.
	[Test]
	public void VecturaOnNewData()
	{
		var outcome = Decide(NewChroma(Vectura1), null);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
		Assert.That((outcome.Status.DataProduct, outcome.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
	}

	// The Pod of VecturaDB that runs makes VecturaDB the product of the data, also seen only from the Pod.
	[Test]
	public void RunningPodSaysTheProduct()
	{
		var server = OnData(Vectura1, null, null);
		var outcome = Decide(server, RunningPod(server, Vectura1));
		Assert.That((outcome.Status.DataProduct, outcome.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
		Assert.That(outcome.Status.Phase, Is.EqualTo("Ready"));
	}

	// A claim retained from VecturaDB: a new Chroma on it knows the product, and does not start Chroma on it without the annotation.
	[Test]
	public void ClaimKeepsTheProduct()
	{
		var outcome = Reconciliation.Decide(NewChroma(Image159), null, "1.0.0-preview.3", Now, Defaults, pvcDataProduct: "vecturadb");
		Assert.That(Condition(outcome, "UpgradeBlocked").Reason, Is.EqualTo("ProductChange"));
		Assert.That((outcome.Status.DataProduct, outcome.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
		Assert.That(outcome.Action, Is.EqualTo(PodAction.None));
		// A claim of v0.1, without the product, is of Chroma.
		var legacy = Reconciliation.Decide(NewChroma(Image153), null, "1.5.9", Now, Defaults);
		Assert.That(Condition(legacy, "UpgradeBlocked").Reason, Is.EqualTo("Downgrade"));
	}

	// The image a refused server keeps is of a passage the data allows: not Chroma on data of VecturaDB.
	[Test]
	public void RecoveryImageFollowsThePassages()
	{
		var server = OnData("docker.io/chromadb/chroma:1.5.10", "vecturadb", "1.0.0-preview.3");
		server.Status.RecoveryImage = Image159;
		server.Status.Image = Vectura1;
		var outcome = Decide(server, null);
		Assert.That(outcome.Action, Is.EqualTo(PodAction.Create));
		Assert.That(outcome.DesiredPod!.Spec.Containers[0].Image, Is.EqualTo(Vectura1));
	}

	// The claim gets the product of the data with its version.
	[Test]
	public async Task ClaimGetsTheProduct()
	{
		using var client = Substitute.For<IKubernetesClient>();
		var server = OnData(Vectura1, "vecturadb", "1.0.0-preview.3", Vectura1);
		server.ApiVersion = "chroma.chromadotnet.org/v1alpha1";
		server.Kind = "Chroma";
		var pod = RunningPod(server, Vectura1);
		var claim = Resources.Pvc(server);
		claim.Metadata.Annotations = new Dictionary<string, string> { [Labels.ClaimDataVersion] = "1.5.9" };
		client.GetAsync<V1ConfigMap>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.ConfigMap(server));
		client.GetAsync<V1Service>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Resources.Service(server));
		client.GetAsync<V1PersistentVolumeClaim>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(claim);
		client.GetAsync<V1Pod>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(pod);
		client.UpdateAsync(Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1PersistentVolumeClaim>());
		client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<V1Alpha1Chroma>());
		await new ChromaController(client, Defaults, NullLogger<ChromaController>.Instance).ReconcileAsync(server, CancellationToken.None);
		await client.Received(1).UpdateAsync(Arg.Is<V1PersistentVolumeClaim>(c => c.Metadata.Annotations[Labels.ClaimDataVersion] == "1.0.0-preview.3"
			&& c.Metadata.Annotations[Labels.ClaimDataProduct] == "vecturadb"
			&& c.Metadata.Annotations[Labels.ClaimDataVersions] == "chroma=1.5.9,vecturadb=1.0.0-preview.3"), Arg.Any<CancellationToken>());
	}

	// The CRD: spec.product, and the images of VecturaDB with their previews.
	[Test]
	public void CrdHasTheProduct()
	{
		var crd = KubernetesYaml.LoadAllFromString(File.ReadAllText(RepositoryFile("deploy", "chroma-operator.yaml")))
			.OfType<V1CustomResourceDefinition>().Single(c => c.Metadata.Name == "chromas.chroma.chromadotnet.org");
		var spec = crd.Spec.Versions.Single().Schema.OpenAPIV3Schema.Properties["spec"];
		Assert.That(spec.Properties["product"].Pattern, Is.EqualTo("^(chroma|vecturadb)$"));
		Assert.That(spec.Properties["image"].Pattern, Is.EqualTo(ChromaImage.Pattern));
		Assert.That(Defaults.IsAllowed(Vectura1), Is.True, "allowed by default");
	}
}
