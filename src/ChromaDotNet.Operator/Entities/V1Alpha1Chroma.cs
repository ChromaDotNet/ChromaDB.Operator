using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace ChromaDotNet.Operator.Entities;

/// <summary>
/// A Chroma. In mode single-node: one Pod, its PersistentVolumeClaim for /data, a ConfigMap with the configuration and a Service. In
/// mode distributed: the objects of the Helm chart distributed-chroma, on an external Postgres and an external S3 bucket.
/// </summary>
[KubernetesEntity(Group = Labels.Group, ApiVersion = Version, Kind = KindName, PluralName = "chromas")]
[GenericAdditionalPrinterColumn(".spec.mode", "Mode", "string")]
[GenericAdditionalPrinterColumn(".status.phase", "Phase", "string")]
[GenericAdditionalPrinterColumn(".status.image", "Image", "string")]
[GenericAdditionalPrinterColumn(".status.dataProduct", "Product", "string")]
[GenericAdditionalPrinterColumn(".status.dataVersion", "Data", "string")]
[GenericAdditionalPrinterColumn(".status.version", "Version", "string")]
[GenericAdditionalPrinterColumn(".metadata.creationTimestamp", "Age", "date")]
// The Service <name>-rw is a DNS label of at most 63 characters: the API refuses names that would not give one.
[ValidationRule("self.metadata.name.size() <= 60 && self.metadata.name.matches('^[a-z]([-a-z0-9]*[a-z0-9])?$')",
	message: "The name of a Chroma is a DNS label of at most 60 characters: lowercase letters, digits and '-', starting with a letter and ending with a letter or a digit.")]
// Each mode has its own fields: storage for single-node, distributed for distributed. The other fields of single-node do not apply to
// the distributed mode, whose components have theirs under spec.distributed.
[ValidationRule("self.spec.mode == 'distributed' ? has(self.spec.distributed) && !has(self.spec.storage) && !has(self.spec.image) && !has(self.spec.imagePullPolicy) && !has(self.spec.resources) && !has(self.spec.config) && !has(self.spec.service) && !has(self.spec.pvcDeletionPolicy) && !has(self.spec.nodeSelector) && !has(self.spec.tolerations) && !has(self.spec.priorityClassName) && !has(self.spec.product) : has(self.spec.storage) && !has(self.spec.distributed)",
	message: "With mode single-node, spec.storage is required and spec.distributed is not used. With mode distributed, spec.distributed is required, and the fields of single-node (storage, image, product, imagePullPolicy, resources, config, service, pvcDeletionPolicy, nodeSelector, tolerations, priorityClassName) are not used: the components have theirs under spec.distributed.")]
public sealed partial class V1Alpha1Chroma
	: CustomKubernetesEntity<V1Alpha1Chroma.ChromaSpec, V1Alpha1Chroma.ChromaStatus>
{
	public const string KindName = "Chroma";
	public const string Version = "v1alpha1";

	public sealed class ChromaSpec
	{
		public const string SingleNodeMode = "single-node";
		public const string DistributedMode = "distributed";

		/// <summary>
		/// single-node, the default, or distributed. It does not change after the creation, as in the operator of Milvus: the data of the
		/// two modes are not the same.
		/// </summary>
		[Description("single-node (default): one Chroma server, with its Pod and its PersistentVolumeClaim. distributed: the components of the Helm chart distributed-chroma of Chroma, one distributed Chroma per namespace. It cannot change after the creation.")]
		[Pattern("^(single-node|distributed)$")]
		[DefaultValue(SingleNodeMode)]
		[ValidationRule("self == oldSelf", message: "spec.mode cannot change after creation: the data of single-node and distributed Chroma are not the same; create a new Chroma and copy the data with chroma copy.")]
		public string Mode { get; set; } = SingleNodeMode;

		/// <summary>The image of Chroma or VecturaDB with an exact version tag, like docker.io/chromadb/chroma:1.5.9; a digest can follow it.</summary>
		[Pattern(ChromaImage.Pattern)]
		[Description("The image of Chroma, or of VecturaDB, with an exact version tag, like docker.io/chromadb/chroma:1.5.9 or ghcr.io/vecturadb/vecturadb:1.0.0-preview.1, optionally followed by @sha256:<digest>. latest, preview and development tags are refused. Empty: the default image of the operator, written here at the first pass. It must be in the repositories the operator allows.")]
		public string? Image { get; set; }

		[Pattern("^(chroma|vecturadb)$")]
		[Description("The product of the image, chroma or vecturadb, for a repository whose product the operator does not know, like a mirror. The operator knows docker.io/chromadb/chroma and ghcr.io/chroma-core/chroma (chroma) and ghcr.io/vecturadb/vecturadb (vecturadb). Empty for those.")]
		public string? Product { get; set; }

		[Description("The pull policy of the image: Always, IfNotPresent or Never. IfNotPresent by default.")]
		[Pattern("^(Always|IfNotPresent|Never)$")]
		public string? ImagePullPolicy { get; set; }

		[Description("Single-node, required: the PersistentVolumeClaim of /data.")]
		public StorageSpec? Storage { get; set; }

		[Description("Requests and limits of the Chroma container.")]
		public V1ResourceRequirements? Resources { get; set; }

		[Description("The configuration of Chroma, written into the ConfigMap mounted at /config.yaml. A change restarts the Pod.")]
		public ConfigSpec? Config { get; set; }

		[Description("The Service in front of the Pod.")]
		public ServiceSpec? Service { get; set; }

		[Description("Retain (default) keeps the PersistentVolumeClaim when the Chroma is deleted; Delete deletes it with the data.")]
		[Pattern("^(Retain|Delete)$")]
		public string? PvcDeletionPolicy { get; set; }

		[Description("Node selector of the Pod.")]
		public IDictionary<string, string>? NodeSelector { get; set; }

		[Description("Tolerations of the Pod.")]
		public IList<V1Toleration>? Tolerations { get; set; }

		[Description("Priority class of the Pod.")]
		public string? PriorityClassName { get; set; }

		[Description("Distributed, required: the version of Chroma, the external Postgres and S3 bucket, and the components.")]
		public DistributedSpec? Distributed { get; set; }
	}

	/// <summary>
	/// A distributed Chroma: the components of the chart distributed-chroma, with the values the operator derives from these fields. The
	/// operator creates the objects of helm template for the chart of <see cref="Version"/>, and only those.
	/// </summary>
	public sealed class DistributedSpec
	{
		[Required]
		[Pattern("^[0-9a-f]{7,40}$")]
		[Description("The commit of Chroma whose images run, as Chroma tags them: its first 7 characters, like 52ce844, or the whole hash. The operator runs only the versions it knows, with the chart and the config files of the same commit. Going back to a version migrated before the last one is a downgrade, refused without the annotation chroma.chromadotnet.org/allow-downgrade: \"true\".")]
		public string Version { get; set; } = string.Empty;

		[Pattern(@"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?(:[0-9]+)?(/[a-z0-9]([-a-z0-9._]*[a-z0-9])?)*$")]
		[Description("The registry and the path of the images, like a mirror of docker.io/chromadb, the default. The images keep the names Chroma publishes: rust-frontend-service-oss, sysdb-service, sysdb-migration, rust-log-service, query-service, compactor-service, garbage-collector-service, work-queue-service, fn-consumer-service.")]
		public string? ImageRegistry { get; set; }

		[Required]
		[Description("The external Postgres of the sysdb. The operator checks it before it starts anything; it never creates or deletes it.")]
		public PostgresSpec Postgres { get; set; } = new();

		[Required]
		[Description("The external object storage. The operator checks the bucket before it starts anything; it never creates, empties or deletes it.")]
		public ObjectStorageSpec ObjectStorage { get; set; } = new();

		[Description("rust-frontend-service, the HTTP API on port 8000. The chart has no affinity for it.")]
		[ValidationRule("!has(self.affinity)", message: "The chart has no affinity for the frontend.")]
		public ComponentSpec? Frontend { get; set; }

		[Description("The sysdb, which keeps the catalog in Postgres and the MemberLists of the other components, with one replica. The chart has no affinity for it.")]
		[ValidationRule("!has(self.affinity) && !has(self.replicas)", message: "The chart has no affinity for the sysdb, and the operator runs one replica of it.")]
		public ComponentSpec? Sysdb { get; set; }

		[Description("rust-log-service, the write-ahead log.")]
		public ComponentSpec? LogService { get; set; }

		[Description("query-service. Its images need AVX-512.")]
		public ComponentSpec? QueryService { get; set; }

		[Description("compaction-service. Its images need AVX-512.")]
		public ComponentSpec? CompactionService { get; set; }

		[Description("garbage-collector, with one replica.")]
		[ValidationRule("!has(self.replicas)", message: "The operator runs one replica of the garbage collector.")]
		public ComponentSpec? GarbageCollector { get; set; }

		[Description("work-queue-service, with one replica. The chart has no placement for it.")]
		[ValidationRule("!has(self.replicas) && !has(self.nodeSelector) && !has(self.tolerations) && !has(self.affinity)", message: "The operator runs one replica of the work queue, and the chart has no placement for it.")]
		public ComponentSpec? WorkQueue { get; set; }

		[Description("fn-consumer, with one replica.")]
		[ValidationRule("!has(self.replicas)", message: "The operator runs one replica of fn-consumer.")]
		public ComponentSpec? FnConsumer { get; set; }
	}

	public sealed class PostgresSpec
	{
		[Required]
		[Pattern(@"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$")]
		[Description("The host name of Postgres, like postgres.databases.svc.cluster.local.")]
		public string Host { get; set; } = string.Empty;

		[RangeMinimum(1)]
		[RangeMaximum(65535)]
		[Description("The port, 5432 by default.")]
		public int? Port { get; set; }

		[Pattern("^[A-Za-z_][A-Za-z0-9_]*$")]
		[Description("The database, sysdb by default. It must exist: the migration Job creates the tables in it.")]
		public string? Database { get; set; }

		[Pattern("^(disable|allow|prefer|require|verify-ca|verify-full)$")]
		[Description("The sslmode of libpq, disable by default; require for Azure Database for PostgreSQL.")]
		public string? SslMode { get; set; }

		[Required]
		[Description("The Secret with the keys username and password, in the namespace of the Chroma. Both may contain only letters, digits and . _ ~ -: the chart passes them unquoted to a shell and into a URL.")]
		public SecretReference CredentialsSecret { get; set; } = new();
	}

	public sealed class ObjectStorageSpec
	{
		[Required]
		[Description("A bucket of AWS S3 or of a service with its API, like MinIO or SeaweedFS.")]
		public S3Spec S3 { get; set; } = new();
	}

	public sealed class S3Spec
	{
		[Pattern("^https?://[^/]+$")]
		[Description("The endpoint, like http://seaweedfs.storage.svc.cluster.local:8333; empty for AWS S3.")]
		public string? Endpoint { get; set; }

		[Pattern("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
		[Description("The bucket, chroma-storage by default. It must exist.")]
		public string? Bucket { get; set; }

		[Pattern("^[a-z0-9-]+$")]
		[Description("The region, us-east-1 by default.")]
		public string? Region { get; set; }

		[Required]
		[Description("The Secret with the keys accessKeyId and secretAccessKey, in the namespace of the Chroma.")]
		public SecretReference CredentialsSecret { get; set; } = new();
	}

	public sealed class SecretReference
	{
		[Required]
		[Description("The name of the Secret.")]
		public string Name { get; set; } = string.Empty;
	}

	/// <summary>A component of the chart: what its values let change. A field left out keeps the value of the chart.</summary>
	public sealed class ComponentSpec
	{
		[RangeMinimum(0)]
		[Description("The replicas.")]
		public int? Replicas { get; set; }

		[Description("CPU and memory, merged over the values of the chart: a quantity left out keeps the one of the chart.")]
		public ComponentResources? Resources { get; set; }

		[Description("Node selector of the Pods.")]
		public IDictionary<string, string>? NodeSelector { get; set; }

		[Description("Tolerations of the Pods.")]
		public IList<V1Toleration>? Tolerations { get; set; }

		[Description("Affinity of the Pods.")]
		public V1Affinity? Affinity { get; set; }
	}

	public sealed class ComponentResources
	{
		[Description("Limits: cpu and memory.")]
		public ResourceAmounts? Limits { get; set; }

		[Description("Requests: cpu and memory.")]
		public ResourceAmounts? Requests { get; set; }
	}

	public sealed class ResourceAmounts
	{
		[Pattern(@"^[0-9]+(\.[0-9]+)?m?$")]
		[Description("CPU, like 500m or 2.")]
		public string? Cpu { get; set; }

		[Pattern(@"^[0-9]+(\.[0-9]+)?(Ki|Mi|Gi|Ti|Pi|Ei|k|M|G|T|P|E)?$")]
		[Description("Memory, like 512Mi.")]
		public string? Memory { get; set; }
	}

	/// <summary>A component of a distributed Chroma in the status.</summary>
	public sealed class ComponentStatus
	{
		[Description("The name of its Deployment or StatefulSet.")]
		public string Name { get; set; } = string.Empty;

		[Description("The replicas of the spec.")]
		public int Replicas { get; set; }

		[Description("The replicas ready.")]
		public int Ready { get; set; }

		[Description("The members in its MemberList, for the components the sysdb lists.")]
		public int? Members { get; set; }
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

	public sealed class ChromaStatus
	{
		[Description("Creating, Migrating (distributed), Ready, Updating or Failed.")]
		public string? Phase { get; set; }

		[Description("The generation of the spec this status describes.")]
		public long? ObservedGeneration { get; set; }

		[Description("The image of the running Pod, once it is ready.")]
		public string? Image { get; set; }

		[Description("The newest image of a Pod of the server that is allowed and not older than the data: the image a server whose spec is refused starts again with.")]
		public string? RecoveryImage { get; set; }

		[Description("The highest version of dataProduct that has opened the data. An image older than this is refused, since Chroma does not open data migrated by a newer version.")]
		public string? DataVersion { get; set; }

		[Description("The product that opened the data last, chroma or vecturadb. The data passes from one product to the other only along the passages the operator knows.")]
		public string? DataProduct { get; set; }

		[Description("Once both products have opened the data, the highest version of each, like chroma: 1.5.9 and vecturadb: 1.0.0-preview.1. An image older than the one of its product is refused, also after the data passed to the other product.")]
		public IDictionary<string, string>? DataVersions { get; set; }

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

		[Description("Distributed: the version that runs on all the components.")]
		public string? Version { get; set; }

		[Description("Distributed: the version whose migration Job completed last.")]
		public string? MigratedVersion { get; set; }

		[Description("Distributed: the versions migrated on the Postgres of the sysdb, in the order of their last migration. Going back to one before the last is a downgrade: the commits of Chroma have no order, so the last one migrated counts as the newest.")]
		public IList<string>? VersionHistory { get; set; }

		[Description("Distributed: the components, with their replicas, the ready ones and the members of their MemberList.")]
		public IList<ComponentStatus>? Components { get; set; }

		[Description("Ready and UpgradeBlocked; distributed: also PostgresReady, ObjectStorageReady, Migrated and MembersReady.")]
		public IList<V1Condition> Conditions { get; set; } = [];
	}
}
