using NUnit.Framework;

namespace ChromaDotNet.Operator.Tests;

public class OperatorOptionsTests
{
	// <path>/* allows the repositories under a path: a registry alone, with a port, or a path of Docker Hub.
	[TestCase("registry.example.com/*", "registry.example.com/sysdb-service:52ce844", true)]
	[TestCase("registry.example.com/*", "registry.example.com/mirror/sysdb-service:52ce844", true)]
	[TestCase("localhost:5000/*", "localhost:5000/sysdb-service:52ce844", true)]
	[TestCase("chromadb/*", "docker.io/chromadb/sysdb-service:52ce844", true)]
	[TestCase("registry.example.com/mirror/*", "registry.example.com/sysdb-service:52ce844", false)]
	[TestCase("registry.example.com/mirror/*", "registry.example.com/mirror-2/sysdb-service:52ce844", false)]
	public void PathAllowsTheRepositoriesUnderIt(string allowed, string image, bool expected) =>
		Assert.That(new OperatorOptions(allowedImages: "docker.io/chromadb/chroma," + allowed).IsAllowed(image), Is.EqualTo(expected));

	// Docker writes the registry out: chromadb/chroma is docker.io/chromadb/chroma, and an image without a slash is in docker.io/library.
	[TestCase("chromadb/chroma:1.5.9", "docker.io/chromadb/chroma")]
	[TestCase("docker.io/chromadb/chroma:1.5.9", "docker.io/chromadb/chroma")]
	[TestCase("chromadb/chroma:1.5.9@sha256:abc", "docker.io/chromadb/chroma")]
	[TestCase("ghcr.io/chroma-core/chroma:1.5.9", "ghcr.io/chroma-core/chroma")]
	[TestCase("registry.local:5055/chromadb/chroma:1.5.9", "registry.local:5055/chromadb/chroma")]
	[TestCase("localhost/chroma:1.5.9", "localhost/chroma")]
	[TestCase("chroma:1.5.9", "docker.io/library/chroma")]
	public void Repositories(string image, string repository) => Assert.That(OperatorOptions.Repository(image), Is.EqualTo(repository));

	[Test]
	public void BuiltInDefaults()
	{
		var options = new OperatorOptions();
		Assert.That(options.DefaultImage, Is.EqualTo("docker.io/chromadb/chroma:1.5.9"));
		Assert.That(options.IsAllowed("chromadb/chroma:1.5.3"), Is.True);
		Assert.That(options.IsAllowed("ghcr.io/chroma-core/chroma:1.5.9"), Is.True);
		Assert.That(options.IsAllowed("docker.io/someone/chroma:1.5.9"), Is.False);
		Assert.That(options.IsAllowed("chromadb/chroma-evil:1.5.9"), Is.False);
	}

	[Test]
	public void AdministratorLists()
	{
		var options = new OperatorOptions("registry.local:5055/chromadb/chroma:1.5.8", " registry.local:5055/chromadb/chroma , chromadb/chroma");
		Assert.That(options.IsAllowed("registry.local:5055/chromadb/chroma:1.5.9"), Is.True);
		Assert.That(options.IsAllowed("docker.io/chromadb/chroma:1.5.9"), Is.True);
		Assert.That(options.IsAllowed("ghcr.io/chroma-core/chroma:1.5.9"), Is.False);
		Assert.That(new OperatorOptions(allowedImages: "*").IsAllowed("anything/at-all:1.0.0"), Is.True);
	}

	// A default that the operator itself would refuse stops the operator at the start, with the reason.
	[TestCase("chromadb/chroma:latest", "exact version")]
	[TestCase("docker.io/someone/chroma:1.5.9", "CHROMA_ALLOWED_IMAGES")]
	public void InvalidDefault(string image, string message)
	{
		var e = Assert.Throws<InvalidOperationException>(() => _ = new OperatorOptions(image));
		Assert.That(e!.Message, Does.Contain(message));
	}
}
