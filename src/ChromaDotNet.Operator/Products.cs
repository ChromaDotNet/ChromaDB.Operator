using ChromaDotNet.Operator.Entities;

namespace ChromaDotNet.Operator;

/// <summary>
/// The products a single-node Chroma can run: Chroma, and VecturaDB, a fork of Chroma with the same API, the same image layout and the
/// same data directory. The kind is the same; the image says which product runs. The server cannot say it: /api/v2/version answers
/// "1.0.0" for both.
/// </summary>
internal static class Products
{
	public const string Chroma = "chroma";
	public const string VecturaDB = "vecturadb";

	/// <summary>The repositories whose product the operator knows.</summary>
	public static readonly IReadOnlyDictionary<string, string> Repositories = new Dictionary<string, string>
	{
		["docker.io/chromadb/chroma"] = Chroma,
		["ghcr.io/chroma-core/chroma"] = Chroma,
		["ghcr.io/vecturadb/vecturadb"] = VecturaDB,
	};

	public static string Name(string product) => product == VecturaDB ? "VecturaDB" : "Chroma";

	/// <summary>A product written in an annotation or a status, in any case; null for anything else.</summary>
	public static string? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
	{
		Chroma => Chroma,
		VecturaDB => VecturaDB,
		_ => null,
	};

	/// <summary>The product of a repository the operator knows, or null.</summary>
	public static string? OfRepository(string image) => Repositories.GetValueOrDefault(OperatorOptions.Repository(image));

	/// <summary>
	/// The product of an image of the Chroma: the one of its repository, if the operator knows it; for the repository of spec.image,
	/// like a mirror, spec.product; otherwise null.
	/// </summary>
	public static string? Of(V1Alpha1Chroma server, string? image)
	{
		if (image is null)
			return null;
		if (OfRepository(image) is { } known)
			return known;
		return server.Spec.Product is { Length: > 0 } product && server.Spec.Image is { } spec
			&& OperatorOptions.Repository(spec) == OperatorOptions.Repository(image) ? product : null;
	}

	/// <summary>Null when the product of spec.image is known and agrees with spec.product; otherwise the reason and why.</summary>
	public static (string Reason, string Message)? Error(V1Alpha1Chroma server)
	{
		var image = server.Spec.Image!;
		var repository = OperatorOptions.Repository(image);
		var known = OfRepository(image);
		var declared = server.Spec.Product is { Length: > 0 } p ? p : null;
		if (known is not null && declared is not null && known != declared)
			return ("ProductMismatch", $"spec.product is {declared}, but {repository} is the repository of {Name(known)}: remove spec.product or change the image.");
		if (known is null && declared is null)
			return ("UnknownProduct", $"The operator does not know which product {repository} is of: set spec.product to {Chroma} or {VecturaDB}, as for a mirror.");
		return null;
	}
}

/// <summary>A passage of the data from one product and version to another, allowed after a trial on a volume.</summary>
/// <param name="Trial">The trial that admitted it.</param>
internal sealed record Passage(string FromProduct, string FromVersion, string ToProduct, string ToVersion, string Trial)
{
	public ImageVersion From { get; } = ImageVersion.Parse(FromVersion) ?? throw new ArgumentException($"{FromVersion} is not a version.", nameof(FromVersion));

	public ImageVersion To { get; } = ImageVersion.Parse(ToVersion) ?? throw new ArgumentException($"{ToVersion} is not a version.", nameof(ToVersion));

	public override string ToString() => $"{Products.Name(FromProduct)} {FromVersion} to {Products.Name(ToProduct)} {ToVersion}";
}

/// <summary>Whether an image may run on the data, and why.</summary>
internal sealed record PassageCheck(bool Allowed, string Reason, string Message);

/// <summary>
/// The data as the operator knows it: the product that opened it last, and the highest version of each product that has opened it.
/// The highest versions stay through passages from one product to the other: Chroma 1.0.0 is refused on data that Chroma 1.5.9
/// opened, also after VecturaDB opened it in between.
/// </summary>
internal sealed record DataState(string Product, IReadOnlyDictionary<string, ImageVersion> Highest)
{
	public ImageVersion? Version => Highest.GetValueOrDefault(Product);
}

/// <summary>The annotations of the Chroma that force a passage.</summary>
internal sealed record Flags(bool AllowDowngrade, bool AllowProductChange);

/// <summary>
/// Which images may run on the data. Never one older than the highest version of its product that has opened the data, without the
/// annotation <see cref="Labels.AllowDowngrade"/>. Within Chroma, any newer version, as before. Between products, and from a VecturaDB
/// to a newer VecturaDB, only the passages of <see cref="Known"/>: each one was tried on a volume first, and anything else is refused,
/// without deductions from the versions. VecturaDB to Chroma keeps the data but not all of it is usable on Chroma: it needs the
/// annotation <see cref="Labels.AllowProductChange"/>, and the status keeps saying so while Chroma runs on it.
/// </summary>
internal static class Passages
{
	/// <summary>The passages the operator allows. A new release of VecturaDB adds its row after a trial of the passage of the data.</summary>
	public static readonly IReadOnlyList<Passage> Known =
	[
		new(Products.Chroma, "1.5.9", Products.VecturaDB, "1.0.0-preview.2",
			"25/10/2026, k3d, a local-path volume opened by Chroma 1.5.9 with 200 dense records, then by VecturaDB 1.0.0-preview.2: the dense " +
			"records intact, with the same ids and results; BM25 and sparse vectors written and searched; two runs with the same outcome."),
		new(Products.Chroma, "1.5.9", Products.VecturaDB, "1.0.0-preview.3",
			"25/10/2026, k3d, a local-path volume opened by Chroma 1.5.9 with 200 dense records, then by VecturaDB 1.0.0-preview.3: the dense " +
			"records intact, with the same ids and results; BM25 and sparse vectors written and searched; two runs with the same outcome, the " +
			"same as with 1.0.0-preview.2."),
		new(Products.VecturaDB, "1.0.0-preview.2", Products.VecturaDB, "1.0.0-preview.3",
			"25/10/2026, k3d, a local-path volume with 200 dense records, a BM25 collection and explicit sparse vectors written by VecturaDB " +
			"1.0.0-preview.2, also on data of Chroma 1.5.9, then opened by VecturaDB 1.0.0-preview.3: the records intact, the BM25 and sparse " +
			"searches with the same results, new records written and found; two runs with the same outcome."),
	];

	/// <summary>
	/// What a passage to Chroma does to data opened by VecturaDB, as measured with Chroma 1.5.9 and as the guide of VecturaDB says: the
	/// records stay, the sparse vectors too, but Chroma does not read them, and its writes leave records without their BM25 vectors.
	/// </summary>
	public const string ToChromaEffect =
		"Chroma reads the records, documents and metadata, but not the sparse vectors, which stay in the data, and sparse, BM25 and Search queries fail. " +
		"Writes from Chroma to collections with BM25 fail with the Python client, and other clients write records without their BM25 vectors, or keep " +
		"the old ones, until their documents are written again with VecturaDB. Do not write with Chroma to data of VecturaDB that uses sparse vectors " +
		"or BM25: https://vecturadb.org/leaving-chroma-cloud/to-vecturadb.html";

	/// <param name="image">The image, for the messages.</param>
	public static PassageCheck Check(DataState data, string product, ImageVersion version, string image, Flags flags)
	{
		// Data nothing has opened yet takes any image.
		if (data.Highest.Count == 0)
			return new(true, "None", "");
		if (data.Highest.GetValueOrDefault(product) is { } highest && version < highest && !flags.AllowDowngrade)
			return new(false, "Downgrade", $"{image} is older than the data, opened by {highest}: {Products.Name(product)} does not open data of a newer version.");
		// The version of the product that opened the data last can be unknown, after an image without a version: no passage starts from it.
		var current = data.Version;
		var from = current is null ? Products.Name(data.Product) : $"{Products.Name(data.Product)} {current}";
		if (product == data.Product)
		{
			if (product == Products.VecturaDB && (current is null || (version > current && !IsKnown(data.Product, current, product, version))))
			{
				return new(false, "UpgradeNotKnown",
					$"The data was last opened by {from}. The operator updates VecturaDB only along the passages it knows, each tried on a volume first: {List()}. " +
					$"{image} on {from} is not one of them.");
			}
			// Chroma on data that VecturaDB opened before: allowed by the passage that brought it there, and the warning stays.
			if (product == Products.Chroma && data.Highest.GetValueOrDefault(Products.VecturaDB) is { } vectura)
				return new(true, "ProductChangeAllowed", $"The data was opened by VecturaDB {vectura} before {from}. {ToChromaEffect}");
			return new(true, "None", "");
		}
		if (product == Products.Chroma)
		{
			var text = $"The data was last opened by {from}. {ToChromaEffect}";
			return flags.AllowProductChange
				? new(true, "ProductChangeAllowed", $"{text} The annotation {Labels.AllowProductChange} allows it.")
				: new(false, "ProductChange", $"{text} To switch anyway, annotate the Chroma with {Labels.AllowProductChange}: \"true\".");
		}
		return current is not null && IsKnown(data.Product, current, product, version)
			? new(true, "None", "")
			: new(false, "ProductChangeNotKnown",
				$"The data was last opened by {from}. The operator moves data from Chroma to VecturaDB only along the passages it knows, each tried on a volume first: {List()}. " +
				$"{image} on {from} is not one of them.");
	}

	private static bool IsKnown(string fromProduct, ImageVersion fromVersion, string toProduct, ImageVersion toVersion) =>
		Known.Any(p => p.FromProduct == fromProduct && p.ToProduct == toProduct && p.From == fromVersion && p.To == toVersion);

	private static string List() => string.Join("; ", Known);
}
