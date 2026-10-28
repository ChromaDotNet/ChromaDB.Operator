using System.Globalization;
using System.Text.RegularExpressions;

namespace ChromaDotNet.Operator;

/// <summary>
/// The version in an image reference. The operator cannot ask the server: /api/v2/version answers "1.0.0" for every 1.x, of Chroma
/// and of VecturaDB alike. Only exact versions are accepted, like docker.io/chromadb/chroma:1.5.9 or ...:1.5.9@sha256:..., and the
/// previews of VecturaDB with their post-releases, like ghcr.io/vecturadb/vecturadb:1.0.0-preview.3.post1; latest, preview and the
/// development images, like 1.5.10.dev313, are refused, since the version of the data must be known.
/// </summary>
internal static partial class ChromaImage
{
	public const string Pattern = @"^\S+:" + ImageVersion.Pattern + @"(@sha256:[0-9a-f]{64})?$";

	[GeneratedRegex(@":(?<version>" + ImageVersion.Pattern + @")(@sha256:[0-9a-f]{64})?$")]
	private static partial Regex VersionRegex();

	[GeneratedRegex(Pattern)]
	private static partial Regex PatternRegex();

	public static bool IsValid(string? image) => image is not null && PatternRegex().IsMatch(image);

	public static ImageVersion? VersionOf(string? image) =>
		IsValid(image) ? ImageVersion.Parse(VersionRegex().Match(image!).Groups["version"].Value) : null;

	public static ImageVersion? Parse(string? version) => ImageVersion.Parse(version);
}

/// <summary>
/// A version x.y.z, x.y.z-preview.N as VecturaDB publishes its previews, or x.y.z-preview.N.postM, a post-release of a preview. As in
/// SemVer 2.0, a preview comes before its release and a post-release after its preview: 1.0.0-preview.3 &lt; 1.0.0-preview.3.post1 &lt;
/// 1.0.0-preview.4 &lt; 1.0.0. Post-releases compare by number, as in PEP 440: SemVer would compare post10 and post2 as text. Versions
/// of two products are never compared: the caller compares those of one product.
/// </summary>
internal sealed partial record ImageVersion(int Major, int Minor, int Patch, int? Preview, int? Post = null) : IComparable<ImageVersion>
{
	/// <summary>x.y.z, x.y.z-preview.N or x.y.z-preview.N.postM, the only versions of an image the operator accepts.</summary>
	public const string Pattern = @"[0-9]+\.[0-9]+\.[0-9]+(-preview\.[0-9]+(\.post[0-9]+)?)?";

	[GeneratedRegex("^" + Pattern + "$")]
	private static partial Regex VersionRegex();

	/// <summary>The version, or null for anything else; a part that does not fit an int makes it invalid.</summary>
	public static ImageVersion? Parse(string? text)
	{
		if (text is null || !VersionRegex().IsMatch(text))
			return null;
		var dash = text.IndexOf('-', StringComparison.Ordinal);
		var release = (dash < 0 ? text : text[..dash]).Split('.');
		int? preview = null;
		int? post = null;
		if (dash >= 0)
		{
			var parts = text[(dash + "-preview.".Length)..].Split(".post");
			if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
				return null;
			preview = n;
			if (parts.Length > 1)
			{
				if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
					return null;
				post = m;
			}
		}
		return int.TryParse(release[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
			&& int.TryParse(release[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
			&& int.TryParse(release[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
				? new ImageVersion(major, minor, patch, preview, post)
				: null;
	}

	public int CompareTo(ImageVersion? other)
	{
		if (other is null)
			return 1;
		var release = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
		if (release != 0)
			return release;
		return (Preview, other.Preview) switch
		{
			(null, null) => 0,
			(null, _) => 1,
			(_, null) => -1,
			var (a, b) when a != b => a!.Value.CompareTo(b!.Value),
			// The same preview: without a post-release it comes first.
			_ => (Post ?? -1).CompareTo(other.Post ?? -1),
		};
	}

	public static bool operator <(ImageVersion? a, ImageVersion? b) => a is not null && a.CompareTo(b) < 0;

	public static bool operator >(ImageVersion? a, ImageVersion? b) => a is not null && a.CompareTo(b) > 0;

	public static bool operator <=(ImageVersion? a, ImageVersion? b) => a is not null && a.CompareTo(b) <= 0;

	public static bool operator >=(ImageVersion? a, ImageVersion? b) => a is not null && a.CompareTo(b) >= 0;

	public override string ToString() => (Preview, Post) switch
	{
		({ } preview, { } post) => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-preview.{preview}.post{post}"),
		({ } preview, null) => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-preview.{preview}"),
		_ => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}"),
	};
}
