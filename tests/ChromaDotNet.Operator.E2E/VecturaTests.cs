using System.Net;
using ChromaDotNet.Operator.Entities;
using k8s.Autorest;
using k8s.Models;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// VecturaDB on the same claim as Chroma: the passages of the table, Chroma 1.5.9 to VecturaDB 1.0.0-preview.3 and VecturaDB
/// 1.0.0-preview.2 to 1.0.0-preview.3, the refused ones, and the passage back to Chroma with the annotation. The records stay through
/// all of them.
/// </summary>
public class VecturaTests
{
	private const string Image153 = "docker.io/chromadb/chroma:1.5.3";
	private const string Image159 = "docker.io/chromadb/chroma:1.5.9";
	private const string VecturaPrevious = "ghcr.io/vecturadb/vecturadb:1.0.0-preview.2";
	private const string Vectura1 = "ghcr.io/vecturadb/vecturadb:1.0.0-preview.3";

	private TestCluster _cluster = null!;

	[SetUp]
	public async Task SetUp() => _cluster = await TestCluster.CreateAsync();

	[TearDown]
	public void TearDown() => _cluster?.Dispose();

	private static V1Condition? Condition(V1Alpha1Chroma chroma, string type) => chroma.Status?.Conditions.FirstOrDefault(c => c.Type == type);

	private async Task<int> CountAsync(string name)
	{
		using var forward = await _cluster.ForwardAsync(name);
		using var chroma = new ChromaHttp(forward.Uri);
		return await chroma.CountAsync("dense");
	}

	[Test]
	public async Task ChromaToVecturaAndBack()
	{
		await _cluster.CreateAsync(TestCluster.NewChroma("vectura", Image159));
		await _cluster.WaitReadyAsync("vectura", Image159);
		using (var forward = await _cluster.ForwardAsync("vectura"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("dense"), 0, 300);

		// Along the table: VecturaDB opens the data of Chroma 1.5.9, and the data is of VecturaDB, on the claim too.
		await _cluster.UpdateAsync("vectura", s => s.Spec.Image = Vectura1);
		var vectura = await _cluster.WaitReadyAsync("vectura", Vectura1);
		Assert.That((vectura.Status!.DataProduct, vectura.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
		var claim = await _cluster.Client.GetAsync<V1PersistentVolumeClaim>("vectura-1", _cluster.Namespace);
		Assert.That(claim!.Metadata.Annotations[Labels.ClaimDataProduct], Is.EqualTo("vecturadb"));
		Assert.That(await CountAsync("vectura"), Is.EqualTo(300));

		// Back to Chroma: refused, and VecturaDB keeps running.
		await _cluster.UpdateAsync("vectura", s => s.Spec.Image = Image159);
		var refused = await _cluster.WaitAsync("vectura", c => Condition(c, "UpgradeBlocked")?.Reason == "ProductChange", TimeSpan.FromMinutes(1), "ProductChange");
		Assert.That(Condition(refused, "UpgradeBlocked")!.Message, Does.Contain("but not the sparse vectors, which stay in the data"));
		Assert.That((await _cluster.PodAsync("vectura-1"))!.Spec.Containers[0].Image, Is.EqualTo(Vectura1));

		// With the annotation, Chroma runs on the data, with the warning; the dense records are there.
		await _cluster.UpdateAsync("vectura", s => s.Metadata.Annotations = new Dictionary<string, string> { [Labels.AllowProductChange] = "true" });
		var forced = await _cluster.WaitReadyAsync("vectura", Image159);
		Assert.That(Condition(forced, "UpgradeBlocked")!.Reason, Is.EqualTo("ProductChangeAllowed"));
		Assert.That((forced.Status!.DataProduct, forced.Status.DataVersion), Is.EqualTo(("chroma", "1.5.9")));
		Assert.That(await CountAsync("vectura"), Is.EqualTo(300));

		// And VecturaDB again, along the table.
		await _cluster.UpdateAsync("vectura", s => s.Spec.Image = Vectura1);
		await _cluster.WaitReadyAsync("vectura", Vectura1);
		Assert.That(await CountAsync("vectura"), Is.EqualTo(300));
	}

	// Along the table within VecturaDB: the release before opens the data first, then the latest one, with the records.
	[Test]
	public async Task PreviousVecturaToTheLatest()
	{
		await _cluster.CreateAsync(TestCluster.NewChroma("newer", VecturaPrevious));
		var previous = await _cluster.WaitReadyAsync("newer", VecturaPrevious);
		Assert.That((previous.Status!.DataProduct, previous.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.2")));
		using (var forward = await _cluster.ForwardAsync("newer"))
		using (var chroma = new ChromaHttp(forward.Uri))
			await chroma.AddAsync(await chroma.CollectionAsync("dense"), 0, 300);

		await _cluster.UpdateAsync("newer", s => s.Spec.Image = Vectura1);
		var latest = await _cluster.WaitReadyAsync("newer", Vectura1);
		Assert.That((latest.Status!.DataProduct, latest.Status.DataVersion), Is.EqualTo(("vecturadb", "1.0.0-preview.3")));
		Assert.That(await CountAsync("newer"), Is.EqualTo(300));
	}

	// Chroma 1.5.3 is not in the table: VecturaDB does not start on its data, and Chroma keeps running.
	[Test]
	public async Task PassageOutsideTheTableIsRefused()
	{
		await _cluster.CreateAsync(TestCluster.NewChroma("older", Image153));
		await _cluster.WaitReadyAsync("older", Image153);
		await _cluster.UpdateAsync("older", s => s.Spec.Image = Vectura1);
		var refused = await _cluster.WaitAsync("older", c => Condition(c, "UpgradeBlocked")?.Reason == "ProductChangeNotKnown", TimeSpan.FromMinutes(1), "ProductChangeNotKnown");
		Assert.That(Condition(refused, "UpgradeBlocked")!.Message, Does.Contain("Chroma 1.5.9 to VecturaDB 1.0.0-preview.3"));
		Assert.That((await _cluster.PodAsync("older-1"))!.Spec.Containers[0].Image, Is.EqualTo(Image153));
	}

	// spec.product takes only the two products, and only in single-node.
	[Test]
	public void ApiChecksTheProduct()
	{
		var other = TestCluster.NewChroma("product", Image159);
		other.Spec.Product = "milvus";
		var e = Assert.ThrowsAsync<HttpOperationException>(() => _cluster.CreateAsync(other));
		Assert.That(e!.Response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), e.Response.Content);
		Assert.That(e.Response.Content, Does.Contain("spec.product"));

		var distributed = TestCluster.NewDistributed("product", "http://10.0.0.1:8333");
		distributed.Spec.Product = "vecturadb";
		e = Assert.ThrowsAsync<HttpOperationException>(() => _cluster.CreateAsync(distributed));
		Assert.That(e!.Response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity), e.Response.Content);
	}
}
