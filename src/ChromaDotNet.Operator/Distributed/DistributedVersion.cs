namespace ChromaDotNet.Operator.Distributed;

/// <summary>
/// A version of distributed Chroma the operator runs: a commit of Chroma, whose images it tags, with the chart whose objects the operator
/// creates and the config files of the same commit, embedded from build/chart.sh.
/// </summary>
internal sealed record DistributedVersion(string Commit, string Chart)
{
	/// <summary>The tag of the images, the first 7 characters of the commit, as Chroma publishes them.</summary>
	public string Tag => Commit[..7];

	/// <summary>
	/// The versions the operator knows. Only these run, as for VecturaDB: a version enters after build/chart.sh, the comparison of
	/// ChartTests and a test of the upgrade from the one before. No version is deduced from another.
	/// </summary>
	public static readonly IReadOnlyList<DistributedVersion> Known =
	[
		new("52ce8448cb38a498f1660d55ea80df25b78f000a", "0.1.94"),
	];

	/// <summary>The known version for spec.distributed.version, a tag or a whole commit; null for another.</summary>
	public static DistributedVersion? Find(string? version) =>
		string.IsNullOrEmpty(version) || version.Length < 7 ? null : Known.FirstOrDefault(v => v.Commit.StartsWith(version, StringComparison.Ordinal));
}
