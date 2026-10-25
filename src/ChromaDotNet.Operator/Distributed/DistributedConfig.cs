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
		Configure(Read(version, "frontend.yaml"), ns, bucket, blocksWithoutCredentials: 0, workQueueClients: false);

	public static string Worker(DistributedVersion version, string ns, string bucket) =>
		Configure(Read(version, "worker.yaml"), ns, bucket, blocksWithoutCredentials: 1, workQueueClients: true);

	/// <summary>
	/// <list type="number">
	/// <item>Every storage block from the Minio mode, with the fixed development keys and endpoint of rust/storage, to the AWS mode, which
	/// reads AWS_* from the environment; a block without credentials, the one of the garbage collector, would be Minio by default, so it
	/// gets them.</item>
	/// <item>The client of the work queue where the services read it. The file writes it as work_queue: grpc: under compaction_service
	/// and under fn_consumer_service, but the compactor reads compaction_service.work_queue with the fields flat, and fn-consumer reads
	/// fn_consumer_service.fn_consumer.work_queue: both ignore the block and use their default host, work-queue-service.chroma, which
	/// is in the namespace chroma only. The compactor gets the block flat; fn-consumer gets it under fn_consumer.</item>
	/// <item>The namespace of the Chroma in the names of the services and in kube_namespace, for the MemberLists.</item>
	/// <item>The bucket of the spec.</item>
	/// </list>
	/// </summary>
	/// <param name="blocksWithoutCredentials">The storage blocks of the file without credentials: the file is not the expected one otherwise.</param>
	/// <param name="workQueueClients">The file has the two clients of the work queue, as the workers' file does.</param>
	internal static string Configure(string file, string ns, string bucket, int blocksWithoutCredentials, bool workQueueClients)
	{
		var lines = file.Replace("credentials: \"Minio\"", "credentials: \"AWS\"", StringComparison.Ordinal).Split('\n').ToList();
		if (workQueueClients)
			lines = PlaceWorkQueueClients(lines);
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

	/// <summary>The two blocks work_queue: grpc: of the workers' file, where the compactor and fn-consumer read them.</summary>
	private static List<string> PlaceWorkQueueClients(List<string> lines)
	{
		var result = new List<string>();
		var section = "";
		var moved = new List<string>();
		for (var i = 0; i < lines.Count; i++)
		{
			var line = lines[i];
			if (line.Length > 0 && !line.StartsWith(' ') && line.EndsWith(':'))
				section = line;
			if (line == "  work_queue:" && i + 1 < lines.Count && lines[i + 1] == "    grpc:" && section is "compaction_service:" or "fn_consumer_service:")
			{
				var flat = section == "compaction_service:";
				if (flat)
				{
					result.Add("  work_queue:");
				}
				else
				{
					result.Add("  fn_consumer:");
					result.Add("    work_queue:");
				}
				for (i += 2; i < lines.Count && lines[i].StartsWith("      ", StringComparison.Ordinal); i++)
					result.Add(flat ? lines[i][2..] : lines[i]);
				i--;
				moved.Add(section);
				continue;
			}
			result.Add(line);
		}
		if (moved is not ["compaction_service:", "fn_consumer_service:"])
			throw new InvalidOperationException($"The config file has the clients of the work queue in {string.Join(", ", moved)} instead of compaction_service and fn_consumer_service: check DistributedConfig against the config files of the version.");
		return result;
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
