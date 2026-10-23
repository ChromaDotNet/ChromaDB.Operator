using System.Text.RegularExpressions;

namespace ChromaDotNet.Operator;

/// <summary>
/// The version of Chroma in an image reference. The operator cannot ask the server: /api/v2/version answers "1.0.0" for every 1.x.
/// Only exact versions are accepted, like docker.io/chromadb/chroma:1.5.9 or ...:1.5.9@sha256:...; latest and the development
/// images, like 1.5.10.dev313, are refused, since the version of the data must be known.
/// </summary>
internal static partial class ChromaImage
{
	public const string Pattern = @"^\S+:[0-9]+\.[0-9]+\.[0-9]+(@sha256:[0-9a-f]{64})?$";

	[GeneratedRegex(@":(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(@sha256:[0-9a-f]{64})?$")]
	private static partial Regex VersionRegex();

	[GeneratedRegex(Pattern)]
	private static partial Regex PatternRegex();

	public static bool IsValid(string? image) => image is not null && PatternRegex().IsMatch(image);

	public static Version? VersionOf(string? image)
	{
		if (!IsValid(image))
			return null;
		// The pattern accepts any number of digits: a part that does not fit an int makes the image invalid.
		var match = VersionRegex().Match(image!);
		return Parse($"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}");
	}

	public static Version? Parse(string? version) =>
		version is not null && Version.TryParse(version, out var parsed) && parsed.Build >= 0 && parsed.Revision < 0 ? parsed : null;
}
