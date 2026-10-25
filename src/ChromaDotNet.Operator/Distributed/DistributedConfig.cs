using System.Reflection;

namespace ChromaDotNet.Operator.Distributed;

/// <summary>
/// The config files of the services of distributed Chroma: the sample files of the repository of Chroma at the commit of the version,
/// rust/frontend/sample_configs/distributed.yaml and rust/worker/chroma_config.yaml, as Tilt and the tests of Chroma use them, with
/// the corrections below and nothing else. build/chart.sh makes the same corrections with sed for the reference.
/// They hold no secrets: the credentials come from Secrets through the environment.
/// </summary>
internal static class DistributedConfig
{
	private const string ChartNamespace = "chroma";

	public static string Frontend(DistributedVersion version, string ns, string bucket) =>
		Configure(Read(version, "frontend.yaml"), ns, bucket, blocksWithoutCredentials: 0);

	public static string Worker(DistributedVersion version, string ns, string bucket) =>
		Configure(Read(version, "worker.yaml"), ns, bucket, blocksWithoutCredentials: 1);

	/// <summary>
	/// <list type="number">
	/// <item>Every storage block from the Minio mode, with the fixed development keys and endpoint of rust/storage, to the AWS mode, which
	/// reads AWS_* from the environment; a block without credentials, the one of the garbage collector, would be Minio by default, so it
	/// gets them.</item>
	/// <item>The namespace of the Chroma in the names of the services and in kube_namespace, for the MemberLists.</item>
	/// <item>The bucket of the spec.</item>
	/// </list>
	/// </summary>
	/// <param name="blocksWithoutCredentials">The storage blocks of the file without credentials: the file is not the expected one otherwise.</param>
	internal static string Configure(string file, string ns, string bucket, int blocksWithoutCredentials)
	{
		var lines = file.Replace("credentials: \"Minio\"", "credentials: \"AWS\"", StringComparison.Ordinal).Split('\n').ToList();
		var added = 0;
		for (var i = 2; i < lines.Count; i++)
		{
			if (lines[i - 2] == "  storage_config:" && lines[i - 1] == "    s3:" && lines[i].StartsWith("      bucket: ", StringComparison.Ordinal))
			{
				lines.Insert(i + 1, "      credentials: \"AWS\"");
				added++;
			}
		}
		if (added != blocksWithoutCredentials)
			throw new InvalidOperationException($"The config file has {added} storage blocks without credentials instead of {blocksWithoutCredentials}: check DistributedConfig against the config files of the version.");
		// The namespace first: a bucket of the spec ending in .chroma must stay as it is.
		return string.Join('\n', lines)
			.Replace($".{ChartNamespace}\"", $".{ns}\"", StringComparison.Ordinal)
			.Replace($"kube_namespace: \"{ChartNamespace}\"", $"kube_namespace: \"{ns}\"", StringComparison.Ordinal)
			.Replace($".{ChartNamespace}.svc.cluster.local", $".{ns}.svc.cluster.local", StringComparison.Ordinal)
			.Replace("bucket: \"chroma-storage\"", $"bucket: \"{bucket}\"", StringComparison.Ordinal);
	}

	private static string Read(DistributedVersion version, string file)
	{
		var name = $"Distributed/{version.Tag}/{file}";
		var assembly = Assembly.GetExecutingAssembly();
		var resource = assembly.GetManifestResourceNames().FirstOrDefault(r => r.Replace('\\', '/') == name)
			?? throw new InvalidOperationException($"The config file {name} is not embedded in the operator.");
		using var stream = assembly.GetManifestResourceStream(resource)!;
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
