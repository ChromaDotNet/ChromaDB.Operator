using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace ChromaDotNet.Operator.Entities;

/// <summary>
/// A single-node Chroma server: one Pod, its PersistentVolumeClaim for /data, a ConfigMap with the configuration and a Service.
/// </summary>
[KubernetesEntity(Group = Labels.Group, ApiVersion = "v1alpha1", Kind = "ChromaServer", PluralName = "chromaservers")]
[KubernetesEntityShortNames("chroma")]
[GenericAdditionalPrinterColumn(".status.phase", "Phase", "string")]
[GenericAdditionalPrinterColumn(".status.image", "Image", "string")]
[GenericAdditionalPrinterColumn(".status.dataVersion", "Data", "string")]
[GenericAdditionalPrinterColumn(".metadata.creationTimestamp", "Age", "date")]
// The Service <name>-rw is a DNS label of at most 63 characters: the API refuses names that would not give one.
[ValidationRule("self.metadata.name.size() <= 60 && self.metadata.name.matches('^[a-z]([-a-z0-9]*[a-z0-9])?$')",
	message: "The name of a ChromaServer is a DNS label of at most 60 characters: lowercase letters, digits and '-', starting with a letter and ending with a letter or a digit.")]
public sealed partial class V1Alpha1ChromaServer
	: CustomKubernetesEntity<V1Alpha1ChromaServer.ServerSpec, V1Alpha1ChromaServer.ServerStatus>
{
	public sealed class ServerSpec
	{
		/// <summary>The image of Chroma with an exact version tag, like docker.io/chromadb/chroma:1.5.9; a digest can follow it.</summary>
		[Pattern(ChromaImage.Pattern)]
		[Description("The image of Chroma with an exact version tag, like docker.io/chromadb/chroma:1.5.9, optionally followed by @sha256:<digest>. latest and development tags are refused. Empty: the default image of the operator, written here at the first pass. It must be in the repositories the operator allows.")]
		public string? Image { get; set; }

		[Description("The pull policy of the image: Always, IfNotPresent or Never. IfNotPresent by default.")]
		[Pattern("^(Always|IfNotPresent|Never)$")]
		public string? ImagePullPolicy { get; set; }

		[Required]
		[Description("The PersistentVolumeClaim of /data.")]
		public StorageSpec Storage { get; set; } = new();

		[Description("Requests and limits of the Chroma container.")]
		public V1ResourceRequirements? Resources { get; set; }

		[Description("The configuration of Chroma, written into the ConfigMap mounted at /config.yaml. A change restarts the Pod.")]
		public ConfigSpec? Config { get; set; }

		[Description("The Service in front of the Pod.")]
		public ServiceSpec? Service { get; set; }

		[Description("Retain (default) keeps the PersistentVolumeClaim when the ChromaServer is deleted; Delete deletes it with the data.")]
		[Pattern("^(Retain|Delete)$")]
		public string? PvcDeletionPolicy { get; set; }

		[Description("Node selector of the Pod.")]
		public IDictionary<string, string>? NodeSelector { get; set; }

		[Description("Tolerations of the Pod.")]
		public IList<V1Toleration>? Tolerations { get; set; }

		[Description("Priority class of the Pod.")]
		public string? PriorityClassName { get; set; }
	}

	public sealed class StorageSpec
	{
		[Required]
		[Description("The size of the volume, like 10Gi.")]
		[Pattern(@"^[0-9]+(\.[0-9]+)?(Ki|Mi|Gi|Ti|Pi|Ei|k|M|G|T|P|E)?$")]
		public string Size { get; set; } = string.Empty;

		[Description("The StorageClass; empty for the default of the cluster.")]
		public string? StorageClass { get; set; }
	}

	public sealed class ConfigSpec
	{
		[Description("max_payload_size_bytes of Chroma: the largest request body. 40 MiB in Chroma by default.")]
		[RangeMinimum(1)]
		public long? MaxPayloadSizeBytes { get; set; }

		[Description("cors_allow_origins of Chroma; [\"*\"] allows any origin.")]
		public IList<string>? CorsAllowOrigins { get; set; }
	}

	public sealed class ServiceSpec
	{
		[Description("ClusterIP (default), NodePort or LoadBalancer.")]
		[Pattern("^(ClusterIP|NodePort|LoadBalancer)$")]
		public string? Type { get; set; }

		[Description("Annotations of the Service.")]
		public IDictionary<string, string>? Annotations { get; set; }
	}

	public sealed class ServerStatus
	{
		[Description("Creating, Ready, Updating or Failed.")]
		public string? Phase { get; set; }

		[Description("The generation of the spec this status describes.")]
		public long? ObservedGeneration { get; set; }

		[Description("The image of the running Pod, once it is ready.")]
		public string? Image { get; set; }

		[Description("The newest image of a Pod of the server that is allowed and not older than the data: the image a server whose spec is refused starts again with.")]
		public string? RecoveryImage { get; set; }

		[Description("The highest version of Chroma that has opened the data. An image older than this is refused, since Chroma does not open data migrated by a newer version.")]
		public string? DataVersion { get; set; }

		[Description("True when an image without a version ran on the data, so that its version is not known: no Pod is replaced or started until the annotation chroma.chromadotnet.org/declared-data-version says it.")]
		public bool? DataVersionUnknown { get; set; }

		[Description("The code of the incident that made the data version unknown: the declaration names it, as <x.y.z>/<code>, so that it resolves this incident only.")]
		public string? DataVersionUnknownIncident { get; set; }

		[Description("The Pod and the image without a version that the last declaration covered, as <uid>/<image>: seen again while it stops, it is not a new incident.")]
		public string? DataVersionDeclaredPod { get; set; }

		[Description("The name of the Pod.")]
		public string? Pod { get; set; }

		[Description("The name of the PersistentVolumeClaim.")]
		public string? Pvc { get; set; }

		[Description("The address of the Service, like http://demo-rw.rag.svc:8000.")]
		public string? Endpoint { get; set; }

		[Description("Ready and UpgradeBlocked.")]
		public IList<V1Condition> Conditions { get; set; } = [];
	}
}
