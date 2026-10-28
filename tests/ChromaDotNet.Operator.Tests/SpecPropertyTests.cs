using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.NUnit;

// FsCheck generates the arguments of a [Property]: the analyzer of NUnit sees none supplied by attributes.
#pragma warning disable NUnit1027

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// Properties of what a Chroma spec may hold, checked by FsCheck on generated inputs, random text included: the version of an image and
/// the image, the name and the names it gives to the objects, the versions kept on the claim, and the passages of the data between
/// versions and products. The cases written by hand are in the other tests; these look for the inputs nobody wrote.
/// </summary>
public partial class SpecPropertyTests
{
	private static readonly Gen<int?> Previews = Gen.OneOf(Gen.Constant<int?>(null), Gen.Choose(0, 4).Select(n => (int?)n));

	private static readonly Gen<int?> Posts = Gen.OneOf(Gen.Constant<int?>(null), Gen.Choose(0, 3).Select(n => (int?)n));

	// Small parts, so that equal versions, a preview next to its release, and a post-release next to its preview, come up often. A
	// post-release is only of a preview.
	private static readonly Gen<ImageVersion> Versions =
		from major in Gen.Choose(0, 2)
		from minor in Gen.Choose(0, 6)
		from patch in Gen.Choose(0, 10)
		from preview in Previews
		from post in Posts
		select new ImageVersion(major, minor, patch, preview, preview is null ? null : post);

	// The versions of the table of the passages, half of the time, so that its rows come up.
	private static readonly Gen<ImageVersion> VersionsNearTheTable =
		Gen.OneOf(Gen.Elements(Passages.Known.SelectMany(p => new[] { p.From, p.To }).ToArray()), Versions);

	private static readonly Gen<string> ProductNames = Gen.Elements(Products.Chroma, Products.VecturaDB);

	private static readonly Gen<string> Repositories =
		Gen.Elements("docker.io/chromadb/chroma", "ghcr.io/vecturadb/vecturadb", "registry.example.com:5000/mirror/chroma", "chroma");

	private static readonly Gen<string> Digests =
		Gen.Elements("0123456789abcdef".ToCharArray()).ArrayOf(64).Select(c => "@sha256:" + new string(c));

	// Names from the letters of a DNS label, of any length around the limit of 60.
	private static readonly Gen<string> LabelLikeNames =
		from length in Gen.Choose(1, 70)
		from chars in Gen.Elements("abz09-".ToCharArray()).ArrayOf(length)
		select new string(chars);

	private static readonly Flags NoAnnotation = new(AllowDowngrade: false, AllowProductChange: false);

	private static DataState OpenedBy(string product, ImageVersion version) =>
		new(product, new Dictionary<string, ImageVersion> { [product] = version });

	[GeneratedRegex("^[a-z]([-a-z0-9]*[a-z0-9])?$")]
	private static partial Regex DnsLabel();

	// Any text: reading a version never throws, and a version it reads prints as a text that reads back as the same version.
	[Property(MaxTest = 1000)]
	public bool VersionFromAnyText(string? text) =>
		ImageVersion.Parse(text) is not { } version || ImageVersion.Parse(version.ToString()) == version;

	[Property]
	public bool VersionPrintsAndReadsBack(NonNegativeInt major, NonNegativeInt minor, NonNegativeInt patch, bool isPreview, NonNegativeInt preview,
		bool isPost, NonNegativeInt post)
	{
		var version = new ImageVersion(major.Get, minor.Get, patch.Get, isPreview ? preview.Get : null, isPreview && isPost ? post.Get : null);
		return ImageVersion.Parse(version.ToString()) == version;
	}

	// The order of SemVer 2.0 for these versions: major, minor and patch first, then a preview before its release, previews by number,
	// and a post-release after its preview, post-releases by number.
	[Property]
	public Property VersionsSortAsInSemVer() =>
		Prop.ForAll(Versions.ListOf().ToArbitrary(), versions =>
			versions.Order().SequenceEqual(versions.OrderBy(v => (v.Major, v.Minor, v.Patch, v.Preview ?? long.MaxValue, v.Post ?? -1))));

	// An image with an exact version, with or without a digest, on any repository, mirrors with a port included, is valid, and its
	// version is that version.
	[Property]
	public Property ImageWithAnExactVersionIsValid() =>
		Prop.ForAll(
			(from repository in Repositories
			 from version in Versions
			 from digest in Gen.OneOf(Gen.Constant(""), Digests)
			 select (Image: $"{repository}:{version}{digest}", Version: version)).ToArbitrary(),
			t => ChromaImage.IsValid(t.Image) && ChromaImage.VersionOf(t.Image) == t.Version);

	// A tag that is not an exact version is refused: an architecture, a development build, a release candidate, latest, a bare preview,
	// a post-release of a release or without its number.
	[Property]
	public Property TagThatIsNotAnExactVersionIsRefused() =>
		Prop.ForAll(
			Gen.OneOf(
				from repository in Repositories
				from version in Versions
				from suffix in Gen.Elements("-arm64", ".dev313", "-rc.1", "-preview", "rc", ".1")
				select $"{repository}:{version}{suffix}",
				from repository in Repositories
				from tag in Gen.Elements("latest", "preview", "1.5", "1", "", "1.5.9.post1", "1.0.0-post1", "1.0.0-preview.3.post", "1.0.0-preview.3-post1")
				select $"{repository}:{tag}",
				from version in Versions
				select version.ToString()).ToArbitrary(),
			image => !ChromaImage.IsValid(image) && ChromaImage.VersionOf(image) is null);

	// Any text: an image with a version is a valid image.
	[Property(MaxTest = 1000)]
	public bool VersionOnlyOfAValidImage(string? text) => ChromaImage.VersionOf(text) is null || ChromaImage.IsValid(text);

	// A name the operator accepts is a DNS label of at most 60 characters, so that its Service <name>-rw is a DNS label of at most 63.
	[Property(MaxTest = 1000)]
	public Property AcceptedNameGivesAServiceThatIsADnsLabel() =>
		Prop.ForAll(Gen.OneOf(LabelLikeNames, ArbMap.Default.GeneratorFor<string>().Where(s => s is not null)).ToArbitrary(), name =>
		{
			if (Resources.NameError(name) is not null)
				return !(name.Length <= Resources.MaxNameLength && DnsLabel().IsMatch(name));
			var service = Resources.ServiceName(name);
			return name.Length <= Resources.MaxNameLength && DnsLabel().IsMatch(name) && service.Length <= 63 && DnsLabel().IsMatch(service);
		});

	// The highest versions on the claim print as product=version and read back as the same versions.
	[Property]
	public Property VersionsOnTheClaimReadBack() =>
		Prop.ForAll(
			(from chroma in Versions
			 from vectura in Versions
			 from both in ArbMap.Default.GeneratorFor<bool>()
			 select both
				 ? new Dictionary<string, ImageVersion> { [Products.Chroma] = chroma, [Products.VecturaDB] = vectura }
				 : new Dictionary<string, ImageVersion> { [Products.VecturaDB] = vectura }).ToArbitrary(),
			highest => Reconciliation.ParseVersions(Reconciliation.FormatVersions(highest)).OrderBy(h => h.Key).SequenceEqual(highest.OrderBy(h => h.Key)));

	// Any text in the annotation of the claim: reading it never throws, and what it reads prints and reads back the same.
	[Property(MaxTest = 1000)]
	public bool VersionsOnTheClaimFromAnyText(string? text)
	{
		var highest = Reconciliation.ParseVersions(text);
		return Reconciliation.ParseVersions(Reconciliation.FormatVersions(highest)).OrderBy(h => h.Key).SequenceEqual(highest.OrderBy(h => h.Key));
	}

	// Data nothing has opened takes any image.
	[Property]
	public Property NewDataTakesAnyImage() =>
		Prop.ForAll((from product in ProductNames from version in Versions select (product, version)).ToArbitrary(), t =>
			Passages.Check(new DataState(t.product, new Dictionary<string, ImageVersion>()), t.product, t.version, "image", NoAnnotation).Allowed);

	// An image older than the highest version of its product that opened the data is a downgrade, without the annotation, whatever the
	// table says; with the annotation it is not refused as a downgrade.
	[Property]
	public Property OlderImageOfTheSameProductIsADowngrade() =>
		Prop.ForAll(
			(from product in ProductNames
			 from highest in VersionsNearTheTable
			 from version in VersionsNearTheTable
			 where version < highest
			 select (product, highest, version)).ToArbitrary(),
			t =>
			{
				var refused = Passages.Check(OpenedBy(t.product, t.highest), t.product, t.version, "image", NoAnnotation);
				var forced = Passages.Check(OpenedBy(t.product, t.highest), t.product, t.version, "image", new Flags(AllowDowngrade: true, AllowProductChange: false));
				return !refused.Allowed && refused.Reason == "Downgrade" && forced.Reason != "Downgrade";
			});

	// Chroma on data of Chroma: any version from the highest up runs.
	[Property]
	public Property NewerChromaRuns() =>
		Prop.ForAll(
			(from highest in Versions from version in Versions where version >= highest select (highest, version)).ToArbitrary(),
			t => Passages.Check(OpenedBy(Products.Chroma, t.highest), Products.Chroma, t.version, "image", NoAnnotation) is { Allowed: true, Reason: "None" });

	// VecturaDB runs on data of Chroma, or on data of an older VecturaDB, only along a row of the table, also with both annotations: they
	// do not force a passage that was not tried. The same VecturaDB, or an older one with the annotation, runs as before.
	[Property(MaxTest = 500)]
	public Property VecturaRunsOnlyAlongTheTable() =>
		Prop.ForAll(
			(from product in ProductNames
			 from highest in VersionsNearTheTable
			 from version in VersionsNearTheTable
			 select (product, highest, version)).ToArbitrary(),
			t =>
			{
				var check = Passages.Check(OpenedBy(t.product, t.highest), Products.VecturaDB, t.version, "image", new Flags(AllowDowngrade: true, AllowProductChange: true));
				var row = Passages.Known.Any(p => p.FromProduct == t.product && p.From == t.highest && p.ToProduct == Products.VecturaDB && p.To == t.version);
				return t.product == Products.VecturaDB && t.version <= t.highest ? check.Allowed : check.Allowed == row;
			});
}
