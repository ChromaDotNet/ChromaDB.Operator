using Microsoft.Extensions.Configuration;

namespace ChromaDotNet.Operator;

/// <summary>
/// The configuration of the operator, from the environment of its Deployment.
/// <list type="bullet">
/// <item><c>CHROMA_DEFAULT_IMAGE</c>: the image of a Chroma without spec.image, written into its spec at the first pass, so that
/// a new version of the operator does not change the version of the servers already created. As CloudNativePG does with
/// POSTGRES_IMAGE_NAME.</item>
/// <item><c>CHROMA_ALLOWED_IMAGES</c>: the repositories a Chroma can use, separated by commas; <c>&lt;path&gt;/*</c> for every
/// repository under a path, like a mirror of the images of distributed Chroma; or <c>*</c> for any. Who can create a Chroma chooses
/// its image, and the operator runs it: without a list, a Chroma could run any image. By default the image of single-node Chroma and
/// the nine images of distributed Chroma that Chroma publishes.</item>
/// </list>
/// </summary>
public sealed class OperatorOptions
{
	public const string DefaultImageVariable = "CHROMA_DEFAULT_IMAGE";
	public const string AllowedImagesVariable = "CHROMA_ALLOWED_IMAGES";
	public const string BuiltInDefaultImage = "docker.io/chromadb/chroma:1.5.9";
	public const string BuiltInAllowedImages = "docker.io/chromadb/chroma,ghcr.io/chroma-core/chroma," +
		"docker.io/chromadb/rust-frontend-service-oss,docker.io/chromadb/sysdb-service,docker.io/chromadb/sysdb-migration," +
		"docker.io/chromadb/rust-log-service,docker.io/chromadb/query-service,docker.io/chromadb/compactor-service," +
		"docker.io/chromadb/garbage-collector-service,docker.io/chromadb/work-queue-service,docker.io/chromadb/fn-consumer-service";

	private readonly HashSet<string> _allowed;
	private readonly List<string> _allowedPaths;
	private readonly bool _anyImage;

	public string DefaultImage { get; }

	public IReadOnlyCollection<string> AllowedRepositories => [.. _allowed, .. _allowedPaths.Select(p => p + "*")];

	public OperatorOptions(string? defaultImage = null, string? allowedImages = null)
	{
		DefaultImage = string.IsNullOrWhiteSpace(defaultImage) ? BuiltInDefaultImage : defaultImage.Trim();
		var entries = (string.IsNullOrWhiteSpace(allowedImages) ? BuiltInAllowedImages : allowedImages)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		_anyImage = entries.Contains("*");
		_allowed = entries.Where(e => e != "*" && !e.EndsWith("/*", StringComparison.Ordinal)).Select(Repository).ToHashSet(StringComparer.Ordinal);
		// A path with /*, which stands for the repositories under it.
		_allowedPaths = entries.Where(e => e != "*" && e.EndsWith("/*", StringComparison.Ordinal)).Select(e => PathOf(e[..^2]) + "/").ToList();
		if (ChromaImage.VersionOf(DefaultImage) is null)
			throw new InvalidOperationException($"{DefaultImageVariable} is {DefaultImage}: it needs an exact version tag, like {BuiltInDefaultImage}.");
		if (!IsAllowed(DefaultImage))
			throw new InvalidOperationException($"{DefaultImageVariable} is {DefaultImage}, which is not in {AllowedImagesVariable}.");
	}

	public static OperatorOptions From(IConfiguration configuration) =>
		new(configuration[DefaultImageVariable], configuration[AllowedImagesVariable]);

	public bool IsAllowed(string image)
	{
		var repository = Repository(image);
		return _anyImage || _allowed.Contains(repository) || _allowedPaths.Any(p => repository.StartsWith(p, StringComparison.Ordinal));
	}

	/// <summary>
	/// A path of repositories with the registry written out as Docker writes it: chromadb is docker.io/chromadb; registry.example.com,
	/// localhost:5000 and registry.example.com/mirror stay as they are. Unlike an image, a path has no tag to remove.
	/// </summary>
	public static string PathOf(string path)
	{
		var slash = path.IndexOf('/', StringComparison.Ordinal);
		var first = slash < 0 ? path : path[..slash];
		return IsRegistry(first) ? path : "docker.io/" + path;
	}

	private static bool IsRegistry(string first) =>
		first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";

	/// <summary>
	/// The repository of an image, without tag and digest, with the registry written out as Docker writes it: chromadb/chroma is
	/// docker.io/chromadb/chroma, and an image without a slash is in docker.io/library.
	/// </summary>
	public static string Repository(string image)
	{
		var at = image.IndexOf('@', StringComparison.Ordinal);
		if (at >= 0)
			image = image[..at];
		var colon = image.LastIndexOf(':');
		if (colon > image.LastIndexOf('/'))
			image = image[..colon];
		var slash = image.IndexOf('/', StringComparison.Ordinal);
		if (slash < 0)
			return "docker.io/library/" + image;
		return IsRegistry(image[..slash]) ? image : "docker.io/" + image;
	}
}
