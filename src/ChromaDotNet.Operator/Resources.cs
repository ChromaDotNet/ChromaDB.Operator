using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;

namespace ChromaDotNet.Operator;

/// <summary>
/// The objects of a Chroma, as the operator wants them. Names follow CloudNativePG: the Pod and its PersistentVolumeClaim are
/// named after the instance, <c>&lt;name&gt;-1</c>; there is no StatefulSet. The Pod is what the measures of the design showed to
/// work with chromadb/chroma 1.x: a non-root user, a read-only root filesystem with /tmp as an emptyDir (without a writable /tmp
/// SQLite fails at the start with a disk I/O error), and HTTP probes, since the image has no curl.
/// </summary>
internal static partial class Resources
{
	public const int Port = 8000;
	public const int User = 1000;
	public const string DataPath = "/data";
	public const string ConfigFile = "config.yaml";
	public const string ContainerName = "chroma";

	/// <summary>
	/// Null when the name of a Chroma gives valid names to its objects; otherwise why it does not. The Service &lt;name&gt;-rw must be a
	/// DNS label of at most 63 characters, so the name is a label of at most 60: lowercase letters, digits and '-', starting with a
	/// letter and ending with a letter or a digit.
	/// </summary>
	public static string? NameError(string name)
	{
		if (name.Length > MaxNameLength)
			return $"The name {name} has {name.Length} characters, and a Chroma has at most {MaxNameLength}: its Service is {ServiceName(name)}, a DNS label of at most 63.";
		return NameRegex().IsMatch(name)
			? null
			: $"The name {name} is not a DNS label: lowercase letters, digits and '-', starting with a letter and ending with a letter or a digit.";
	}

	public const int MaxNameLength = 60;

	[System.Text.RegularExpressions.GeneratedRegex("^[a-z]([-a-z0-9]*[a-z0-9])?$")]
	private static partial System.Text.RegularExpressions.Regex NameRegex();


	public static string InstanceName(string server) => server + "-1";

	public static string ConfigMapName(string server) => server + "-config";

	public static string ServiceName(string server) => server + "-rw";

	public static string Endpoint(V1Alpha1Chroma server) =>
		$"http://{ServiceName(server.Name())}.{server.Namespace()}.svc:{Port.ToString(CultureInfo.InvariantCulture)}";

	/// <summary>
	/// The configuration file of Chroma. Only the file: Chroma lets CHROMA_* environment variables override it, and the operator sets
	/// none, so that the file is the whole configuration.
	/// </summary>
	public static string ConfigYaml(V1Alpha1Chroma.ConfigSpec? config)
	{
		var yaml = new StringBuilder();
		yaml.Append(CultureInfo.InvariantCulture, $"persist_path: \"{DataPath}\"\n");
		yaml.Append(CultureInfo.InvariantCulture, $"port: {Port}\n");
		yaml.Append("listen_address: \"0.0.0.0\"\n");
		if (config?.MaxPayloadSizeBytes is { } max)
			yaml.Append(CultureInfo.InvariantCulture, $"max_payload_size_bytes: {max}\n");
		if (config?.CorsAllowOrigins is { Count: > 0 } origins)
		{
			yaml.Append("cors_allow_origins:\n");
			foreach (var origin in origins)
				yaml.Append(CultureInfo.InvariantCulture, $"  - \"{origin.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"\n");
		}
		return yaml.ToString();
	}

	public static V1ConfigMap ConfigMap(V1Alpha1Chroma server) => new V1ConfigMap
	{
		Metadata = Metadata(server, ConfigMapName(server.Name())),
		Data = new Dictionary<string, string> { [ConfigFile] = ConfigYaml(server.Spec.Config) },
	}.Initialize();

	public static V1Service Service(V1Alpha1Chroma server) => new V1Service
	{
		Metadata = Metadata(server, ServiceName(server.Name())),
		Spec = new V1ServiceSpec
		{
			Type = server.Spec.Service?.Type ?? "ClusterIP",
			Selector = new Dictionary<string, string> { [Labels.Chroma] = server.Name() },
			Ports = [new V1ServicePort { Name = "http", Port = Port, TargetPort = "http", Protocol = "TCP" }],
		},
	}.Initialize();

	public static V1PersistentVolumeClaim Pvc(V1Alpha1Chroma server)
	{
		var pvc = new V1PersistentVolumeClaim
		{
			Metadata = Metadata(server, InstanceName(server.Name()), owned: server.Spec.PvcDeletionPolicy == "Delete"),
			Spec = new V1PersistentVolumeClaimSpec
			{
				AccessModes = ["ReadWriteOnce"],
				Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(server.Spec.Storage!.Size) } },
				StorageClassName = string.IsNullOrEmpty(server.Spec.Storage!.StorageClass) ? null : server.Spec.Storage.StorageClass,
			},
		}.Initialize();
		return pvc;
	}

	/// <summary>The Pod, with the hash of the configuration and the hash of itself in its annotations.</summary>
	public static V1Pod Pod(V1Alpha1Chroma server, string image)
	{
		var name = InstanceName(server.Name());
		var configHash = Hash(ConfigYaml(server.Spec.Config));
		var probe = (string path, int period, int failures) => new V1Probe
		{
			HttpGet = new V1HTTPGetAction { Path = path, Port = "http" },
			PeriodSeconds = period,
			FailureThreshold = failures,
			TimeoutSeconds = 2,
		};
		var pod = new V1Pod
		{
			Metadata = Metadata(server, name),
			Spec = new V1PodSpec
			{
				TerminationGracePeriodSeconds = 30,
				AutomountServiceAccountToken = false,
				EnableServiceLinks = false,
				NodeSelector = server.Spec.NodeSelector,
				Tolerations = server.Spec.Tolerations,
				PriorityClassName = server.Spec.PriorityClassName,
				SecurityContext = new V1PodSecurityContext
				{
					RunAsUser = User,
					RunAsGroup = User,
					RunAsNonRoot = true,
					FsGroup = User,
					SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
				},
				Containers =
				[
					new V1Container
					{
						Name = ContainerName,
						Image = image,
						ImagePullPolicy = server.Spec.ImagePullPolicy ?? "IfNotPresent",
						Ports = [new V1ContainerPort { Name = "http", ContainerPort = Port, Protocol = "TCP" }],
						// The heartbeat answers as soon as the port is open, after the migrations of SQLite; the healthcheck says whether the
						// executor and the log are ready, which on a single node they always are once it answers.
						StartupProbe = probe("/api/v2/heartbeat", 1, 600),
						ReadinessProbe = probe("/api/v2/healthcheck", 5, 3),
						LivenessProbe = probe("/api/v2/heartbeat", 10, 3),
						Resources = server.Spec.Resources,
						SecurityContext = new V1SecurityContext
						{
							ReadOnlyRootFilesystem = true,
							AllowPrivilegeEscalation = false,
							Capabilities = new V1Capabilities { Drop = ["ALL"] },
						},
						VolumeMounts =
						[
							new V1VolumeMount { Name = "data", MountPath = DataPath },
							new V1VolumeMount { Name = "tmp", MountPath = "/tmp" },
							new V1VolumeMount { Name = "config", MountPath = "/" + ConfigFile, SubPath = ConfigFile, ReadOnlyProperty = true },
						],
					},
				],
				Volumes =
				[
					new V1Volume { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = name } },
					new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
					new V1Volume { Name = "config", ConfigMap = new V1ConfigMapVolumeSource { Name = ConfigMapName(server.Name()) } },
				],
			},
		}.Initialize();
		pod.Metadata.Annotations = new Dictionary<string, string> { [Labels.ConfigHash] = configHash };
		pod.Metadata.Annotations[Labels.PodSpecHash] = Hash(KubernetesJson.Serialize(pod));
		return pod;
	}

	public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

	private static V1ObjectMeta Metadata(V1Alpha1Chroma server, string name, IDictionary<string, string>? annotations = null, bool owned = true)
	{
		var metadata = new V1ObjectMeta
		{
			Name = name,
			NamespaceProperty = server.Namespace(),
			Labels = Labels.For(server.Name()),
			Annotations = annotations is null ? null : new Dictionary<string, string>(annotations),
		};
		if (owned)
			metadata.OwnerReferences = [OwnerReference(server)];
		return metadata;
	}

	public static V1OwnerReference OwnerReference(V1Alpha1Chroma server) => new()
	{
		ApiVersion = server.ApiVersion,
		Kind = server.Kind,
		Name = server.Name(),
		Uid = server.Uid(),
		Controller = true,
		BlockOwnerDeletion = true,
	};
}
