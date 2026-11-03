using NUnit.Framework;

namespace ChromaDotNet.Operator.Tests;

public class ChromaImageTests
{
	[TestCase("docker.io/chromadb/chroma:1.5.9", "1.5.9")]
	[TestCase("chromadb/chroma:1.0.0", "1.0.0")]
	[TestCase("ghcr.io/chroma-core/chroma:1.5.10", "1.5.10")]
	[TestCase("registry.local:5055/chromadb/chroma:1.5.8", "1.5.8")]
	[TestCase("chromadb/chroma:1.5.9@sha256:1e0b73a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9", "1.5.9")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.1", "1.0.0-preview.1")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.12@sha256:1e0b73a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9", "1.0.0-preview.12")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.3.post1", "1.0.0-preview.3.post1")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.3.post12@sha256:1e0b73a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9", "1.0.0-preview.3.post12")]
	public void ExactVersions(string image, string version)
	{
		Assert.That(ChromaImage.IsValid(image), Is.True);
		Assert.That(ChromaImage.VersionOf(image)?.ToString(), Is.EqualTo(version));
	}

	// latest and the development images of main have no version the data can be compared with.
	[TestCase("chromadb/chroma")]
	[TestCase("chromadb/chroma:latest")]
	[TestCase("chromadb/chroma:1.5.10.dev313")]
	[TestCase("chromadb/chroma:1.5")]
	[TestCase("chromadb/chroma:1.5.9-arm64")]
	[TestCase("ghcr.io/vecturadb/vecturadb:preview")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-rc.1")]
	// A post-release only of a preview, with its number, as VecturaDB tags them: vX.Y.Z-preview.N.postM.
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.3.post")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.3-post1")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0-preview.3.post1.post2")]
	[TestCase("ghcr.io/vecturadb/vecturadb:1.0.0.post1")]
	[TestCase("chromadb/chroma@sha256:1e0b73a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9")]
	[TestCase("registry.local:5055/chromadb/chroma")]
	[TestCase("")]
	public void RefusedImages(string image)
	{
		Assert.That(ChromaImage.IsValid(image), Is.False);
		Assert.That(ChromaImage.VersionOf(image), Is.Null);
	}

	[Test]
	public void VersionsCompareAsNumbers() =>
		Assert.That(ChromaImage.VersionOf("chromadb/chroma:1.5.10"), Is.GreaterThan(ChromaImage.VersionOf("chromadb/chroma:1.5.9")!));

	// As in SemVer 2.0: a preview comes before its release, and the previews in their order.
	[TestCase("1.0.0-preview.1", "1.0.0-preview.2")]
	[TestCase("1.0.0-preview.2", "1.0.0-preview.10")]
	[TestCase("1.0.0-preview.9", "1.0.0")]
	[TestCase("0.9.9", "1.0.0-preview.1")]
	public void PreviewsComeBeforeTheirRelease(string lower, string higher) =>
		Assert.That(ImageVersion.Parse(lower), Is.LessThan(ImageVersion.Parse(higher)!));

	// A post-release comes after its preview and before the next one, as in SemVer 2.0; the post-releases by number, as in PEP 440.
	[TestCase("1.0.0-preview.3", "1.0.0-preview.3.post1")]
	[TestCase("1.0.0-preview.3.post1", "1.0.0-preview.3.post2")]
	[TestCase("1.0.0-preview.3.post2", "1.0.0-preview.3.post10")]
	[TestCase("1.0.0-preview.3.post1", "1.0.0-preview.4")]
	[TestCase("1.0.0-preview.3.post1", "1.0.0")]
	public void PostReleasesComeAfterTheirPreview(string lower, string higher) =>
		Assert.That(ImageVersion.Parse(lower), Is.LessThan(ImageVersion.Parse(higher)!));

	[TestCase("1.5.9", "1.5.10", "1.5.10")]
	[TestCase("1.5.9", null, "1.5.9")]
	[TestCase(null, "1.0.0", "1.0.0")]
	[TestCase(null, null, null)]
	[TestCase("1.5.9", "not a version", "1.5.9")]
	[TestCase("1.0.0-preview.1", "1.0.0-preview.2", "1.0.0-preview.2")]
	[TestCase("1.0.0-preview.3.post1", "1.0.0-preview.3", "1.0.0-preview.3.post1")]
	public void MaxOfDataVersions(string? a, string? b, string? max) => Assert.That(Reconciliation.Max(a, b), Is.EqualTo(max));
}
