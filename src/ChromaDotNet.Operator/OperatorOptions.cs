using Microsoft.Extensions.Configuration;

namespace ChromaDotNet.Operator;

/// <summary>
/// The configuration of the operator, from the environment of its Deployment.
/// <list type="bullet">
/// <item><c>CHROMA_DEFAULT_IMAGE</c>: the image of a ChromaServer without spec.image, written into its spec at the first pass, so that
/// a new version of the operator does not change the version of the servers already created. As CloudNativePG does with
/// POSTGRES_IMAGE_NAME.</item>
/// <item><c>CHROMA_ALLOWED_IMAGES</c>: the repositories a ChromaServer can use, separated by commas, or <c>*</c> for any. Who can
/// create a ChromaServer chooses its image, and the operator runs it: without a list, a ChromaServer could run any image.</item>
/// </list>
/// </summary>
public sealed class OperatorOptions
{
	public const string DefaultImageVariable = "CHROMA_DEFAULT_IMAGE";
	public const string AllowedImagesVariable = "CHROMA_ALLOWED_IMAGES";
	public const string BuiltInDefaultImage = "docker.io/chromadb/chroma:1.5.9";
	public const string BuiltInAllowedImages = "docker.io/chromadb/chroma,ghcr.io/chroma-core/chroma";

	private readonly HashSet<string> _allowed;
	private readonly bool _anyImage;

	public string DefaultImage { get; }

	public IReadOnlyCollection<string> AllowedRepositories => _allowed;

	public OperatorOptions(string? defaultImage = null, string? allowedImages = null)
	{
		DefaultImage = string.IsNullOrWhiteSpace(defaultImage) ? BuiltInDefaultImage : defaultImage.Trim();
		var entries = (string.IsNullOrWhiteSpace(allowedImages) ? BuiltInAllowedImages : allowedImages)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		_anyImage = entries.Contains("*");
		_allowed = entries.Where(e => e != "*").Select(Repository).ToHashSet(StringComparer.Ordinal);
		if (ChromaImage.VersionOf(DefaultImage) is null)
			throw new InvalidOperationException($"{DefaultImageVariable} is {DefaultImage}: it needs an exact version tag, like {BuiltInDefaultImage}.");
		if (!IsAllowed(DefaultImage))
			throw new InvalidOperationException($"{DefaultImageVariable} is {DefaultImage}, which is not in {AllowedImagesVariable}.");
	}

	public static OperatorOptions From(IConfiguration configuration) =>
		new(configuration[DefaultImageVariable], configuration[AllowedImagesVariable]);

	public bool IsAllowed(string image) => _anyImage || _allowed.Contains(Repository(image));

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
		var first = image[..slash];
		var isRegistry = first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";
		return isRegistry ? image : "docker.io/" + image;
	}
}
