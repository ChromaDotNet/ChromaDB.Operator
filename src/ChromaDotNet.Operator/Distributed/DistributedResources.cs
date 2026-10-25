using System.Globalization;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;

namespace ChromaDotNet.Operator.Distributed;

/// <summary>
/// The objects of a distributed Chroma: those of helm template of the chart distributed-chroma of the version, with the values the
/// operator derives from the spec, over values.yaml and values.dev.yaml of the chart (build/chart.sh writes them for ChartTests). The
/// differences from the chart are only these (OPERATOR-DESIGN.md §7.4):
/// <list type="number">
/// <item>the MemberLists have <c>members: []</c> instead of null;</item>
/// <item>no Namespace: the Chroma lives in it already;</item>
/// <item>the images Chroma publishes, by commit, and the command of the frontend of values.dev.yaml;</item>
/// <item>the credentials of Postgres and S3 from Secrets, in the environment;</item>
/// <item>the Chroma as controller of every object;</item>
/// <item>the labels app.kubernetes.io/managed-by and chroma.chromadotnet.org/chroma on every object, not on the Pods.</item>
/// </list>
/// Values the operator does not take from values.dev.yaml: CHROMA_ALLOW_RESET, which lets anyone who reaches the frontend delete all
/// the data, and the replicas of the components it runs with one.
/// </summary>
internal static class DistributedResources
{
	public const string DefaultRegistry = "docker.io/chromadb";
	public const string DefaultBucket = "chroma-storage";
	public const string DefaultRegion = "us-east-1";
	public const int DefaultPostgresPort = 5432;
	public const string DefaultDatabase = "sysdb";
	public const string DefaultSslMode = "disable";

	public const string UsernameKey = "username";
	public const string PasswordKey = "password";
	public const string AccessKeyIdKey = "accessKeyId";
	public const string SecretAccessKeyKey = "secretAccessKey";

	public const string Frontend = "rust-frontend-service";
	public const string Sysdb = "sysdb";
	public const string LogService = "rust-log-service";
	public const string QueryService = "query-service";
	public const string CompactionService = "compaction-service";
	public const string GarbageCollector = "garbage-collector";
	public const string WorkQueue = "work-queue-service";
	public const string FnConsumer = "fn-consumer";
	public const int FrontendPort = 8000;

	/// <summary>The image names Chroma publishes, which are not those of the chart, by component.</summary>
	public static readonly IReadOnlyDictionary<string, string> Images = new Dictionary<string, string>
	{
		[Frontend] = "rust-frontend-service-oss",
		[Sysdb] = "sysdb-service",
		["sysdb-migration"] = "sysdb-migration",
		[LogService] = "rust-log-service",
		[QueryService] = "query-service",
		[CompactionService] = "compactor-service",
		[GarbageCollector] = "garbage-collector-service",
		[WorkQueue] = "work-queue-service",
		[FnConsumer] = "fn-consumer-service",
	};

	/// <summary>The MemberLists, with the label member-type of the Pods the sysdb lists in each.</summary>
	public static readonly IReadOnlyList<(string MemberList, string MemberType, string Component)> MemberLists =
	[
		("query-service-memberlist", "query-service", QueryService),
		("compaction-service-memberlist", "compaction-service", CompactionService),
		("garbage-collection-service-memberlist", "garbage-collection-service", GarbageCollector),
		("rust-log-service-memberlist", "rust-log-service", LogService),
		("fn-consumer-memberlist", "fn-consumer", FnConsumer),
	];

	/// <summary>The order of a rollout: each tier waits for the one before it to be updated and ready; the frontend comes last.</summary>
	public static readonly IReadOnlyList<IReadOnlyList<string>> Tiers =
	[
		[Sysdb],
		[LogService],
		[QueryService, CompactionService, GarbageCollector, WorkQueue, FnConsumer],
		[Frontend],
	];

	private sealed record Defaults(int Replicas, string LimitCpu, string LimitMemory, string RequestCpu, string RequestMemory);

	// replicaCount and resources of values.yaml with values.dev.yaml of the chart.
	private static readonly Dictionary<string, Defaults> ChartDefaults = new()
	{
		[Frontend] = new(1, "100m", "768Mi", "100m", "256Mi"),
		[Sysdb] = new(1, "100m", "384Mi", "100m", "128Mi"),
		[LogService] = new(1, "200m", "768Mi", "200m", "512Mi"),
		[QueryService] = new(2, "100m", "1Gi", "100m", "512Mi"),
		[CompactionService] = new(2, "200m", "1536Mi", "200m", "768Mi"),
		[GarbageCollector] = new(1, "100m", "1Gi", "100m", "512Mi"),
		[WorkQueue] = new(1, "200m", "512Mi", "200m", "256Mi"),
		[FnConsumer] = new(1, "200m", "768Mi", "200m", "384Mi"),
	};

	private const string Jemalloc = "prof:true,prof_active:true,lg_prof_sample:19";

	public static string MigrationJobName(DistributedVersion version) => "sysdb-migration-" + version.Tag;

	public static string Endpoint(V1Alpha1Chroma chroma) =>
		$"http://{Frontend}.{chroma.Namespace()}.svc:{FrontendPort.ToString(CultureInfo.InvariantCulture)}";

	public static string Image(V1Alpha1Chroma chroma, DistributedVersion version, string component) =>
		$"{Registry(chroma)}/{Images[component]}:{version.Tag}";

	public static string Registry(V1Alpha1Chroma chroma) =>
		string.IsNullOrEmpty(Spec(chroma).ImageRegistry) ? DefaultRegistry : Spec(chroma).ImageRegistry!;

	private static V1Alpha1Chroma.DistributedSpec Spec(V1Alpha1Chroma chroma) =>
		chroma.Spec.Distributed ?? throw new InvalidOperationException($"The Chroma {chroma.Name()} has no spec.distributed.");

	public static string Bucket(V1Alpha1Chroma chroma) => Spec(chroma).ObjectStorage.S3.Bucket is { Length: > 0 } b ? b : DefaultBucket;

	public static string Region(V1Alpha1Chroma chroma) => Spec(chroma).ObjectStorage.S3.Region is { Length: > 0 } r ? r : DefaultRegion;

	public static string? StorageEndpoint(V1Alpha1Chroma chroma) => Spec(chroma).ObjectStorage.S3.Endpoint is { Length: > 0 } e ? e : null;

	public static int PostgresPort(V1Alpha1Chroma chroma) => Spec(chroma).Postgres.Port ?? DefaultPostgresPort;

	public static string Database(V1Alpha1Chroma chroma) => Spec(chroma).Postgres.Database is { Length: > 0 } d ? d : DefaultDatabase;

	public static string SslMode(V1Alpha1Chroma chroma) => Spec(chroma).Postgres.SslMode is { Length: > 0 } s ? s : DefaultSslMode;

	/// <summary>The component of the spec, or null.</summary>
	public static V1Alpha1Chroma.ComponentSpec? ComponentOf(V1Alpha1Chroma chroma, string component)
	{
		var spec = Spec(chroma);
		return component switch
		{
			Frontend => spec.Frontend,
			Sysdb => spec.Sysdb,
			LogService => spec.LogService,
			QueryService => spec.QueryService,
			CompactionService => spec.CompactionService,
			GarbageCollector => spec.GarbageCollector,
			WorkQueue => spec.WorkQueue,
			FnConsumer => spec.FnConsumer,
			_ => null,
		};
	}

	public static int Replicas(V1Alpha1Chroma chroma, string component) => ComponentOf(chroma, component)?.Replicas ?? ChartDefaults[component].Replicas;

	/// <summary>
	/// What comes before the workloads: service accounts, roles and bindings, MemberLists, ConfigMaps and Services. A MemberList is only
	/// created, never written again: the sysdb writes its members.
	/// </summary>
	public static List<IKubernetesObject<V1ObjectMeta>> Foundation(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var objects = new List<IKubernetesObject<V1ObjectMeta>>();
		foreach (var account in new[]
		{
			"compaction-service-serviceaccount", "fn-consumer-service-account", "garbage-collector-serviceaccount", "query-service-serviceaccount",
			"rust-frontend-service-serviceaccount", "rust-log-service-serviceaccount", "sysdb-serviceaccount", "work-queue-service-serviceaccount",
		})
			objects.Add(Own(chroma, new V1ServiceAccount { ApiVersion = "v1", Kind = "ServiceAccount", Metadata = new V1ObjectMeta { Name = account } }));

		string[] readerWriter = ["get", "list", "watch", "create", "update", "patch", "delete"];
		objects.Add(Role(chroma, "compaction-service-memberlist-readerwriter", "chroma.cluster", "memberlists", readerWriter));
		objects.Add(Role(chroma, "fn-consumer-memberlist-writer", "chroma.cluster", "memberlists", ["get", "update", "patch"], "fn-consumer-memberlist"));
		objects.Add(Role(chroma, "fn-consumer-memberlist-reader", "chroma.cluster", "memberlists", ["get", "list", "watch"]));
		objects.Add(Role(chroma, "garbage-collection-service-memberlist-readerwriter", "chroma.cluster", "memberlists", readerWriter));
		objects.Add(Role(chroma, "lease-watcher", "coordination.k8s.io", "leases", ["get", "create", "watch", "update", "delete"]));
		objects.Add(Role(chroma, "pod-watcher", "", "pods", ["get", "list", "watch"]));
		objects.Add(Role(chroma, "query-service-memberlist-readerwriter", "chroma.cluster", "memberlists", readerWriter));
		objects.Add(Role(chroma, "rust-log-service-memberlist-readerwriter", "chroma.cluster", "memberlists", readerWriter));

		foreach (var (binding, role, account) in new[]
		{
			("sysdb-compaction-service-memberlist-binding", "compaction-service-memberlist-readerwriter", "sysdb-serviceaccount"),
			("compaction-service-compaction-service-memberlist-binding", "compaction-service-memberlist-readerwriter", "compaction-service-serviceaccount"),
			("compaction-service-memberlist-readerwriter-binding", "compaction-service-memberlist-readerwriter", "default"),
			("compaction-service-serviceaccount-rolebinding", "pod-watcher", "compaction-service-serviceaccount"),
			("sysdb-fn-consumer-memberlist-writer", "fn-consumer-memberlist-writer", "sysdb-serviceaccount"),
			("work-queue-service-fn-consumer-memberlist-reader", "fn-consumer-memberlist-reader", "work-queue-service-serviceaccount"),
			("fn-consumer-service-serviceaccount-rolebinding", "pod-watcher", "fn-consumer-service-account"),
			("sysdb-garbage-collection-service-memberlist-binding", "garbage-collection-service-memberlist-readerwriter", "sysdb-serviceaccount"),
			("garbage-collection-service-garbage-collection-service-memberlist-binding", "garbage-collection-service-memberlist-readerwriter", "garbage-collector-serviceaccount"),
			("garbage-collection-service-memberlist-readerwriter-binding", "garbage-collection-service-memberlist-readerwriter", "default"),
			("garbage-collector-serviceaccount-rolebinding", "pod-watcher", "garbage-collector-serviceaccount"),
			("sysdb-query-service-memberlist-binding", "query-service-memberlist-readerwriter", "sysdb-serviceaccount"),
			("query-service-query-service-memberlist-binding", "query-service-memberlist-readerwriter", "query-service-serviceaccount"),
			("query-service-memberlist-readerwriter-binding", "query-service-memberlist-readerwriter", "default"),
			("rust-frontend-service-query-service-memberlist-binding", "query-service-memberlist-readerwriter", "rust-frontend-service-serviceaccount"),
			("query-service-serviceaccount-rolebinding", "pod-watcher", "query-service-serviceaccount"),
			("rust-frontend-service-rolebinding", "pod-watcher", "rust-frontend-service-serviceaccount"),
			("sysdb-rust-log-service-memberlist-binding", "rust-log-service-memberlist-readerwriter", "sysdb-serviceaccount"),
			("rust-frontend-service-rust-log-service-memberlist-binding", "rust-log-service-memberlist-readerwriter", "rust-frontend-service-serviceaccount"),
			("fn-consumer-rust-log-service-memberlist-binding", "rust-log-service-memberlist-readerwriter", "fn-consumer-service-account"),
			("sysdb-serviceaccount-rolebinding", "pod-watcher", "sysdb-serviceaccount"),
			("sysdb-serviceaccount-lease-watcher-binding", "lease-watcher", "sysdb-serviceaccount"),
			("work-queue-service-serviceaccount-rolebinding", "pod-watcher", "work-queue-service-serviceaccount"),
		})
		{
			objects.Add(Own(chroma, new V1RoleBinding
			{
				ApiVersion = "rbac.authorization.k8s.io/v1",
				Kind = "RoleBinding",
				Metadata = new V1ObjectMeta { Name = binding },
				RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "Role", Name = role },
				Subjects = [new Rbacv1Subject { Kind = "ServiceAccount", Name = account, NamespaceProperty = chroma.Namespace() }],
			}));
		}

		foreach (var (memberList, _, _) in MemberLists)
			objects.Add(Own(chroma, new V1MemberList { Metadata = new V1ObjectMeta { Name = memberList }, Spec = new V1MemberList.MemberListSpec { Members = [] } }));

		var ns = chroma.Namespace();
		var bucket = Bucket(chroma);
		objects.Add(ConfigMap(chroma, "rust-frontend-service-config", DistributedConfig.Frontend(version, ns, bucket)));
		var worker = DistributedConfig.Worker(version, ns, bucket);
		foreach (var config in new[] { "compaction-service-config", "fn-consumer-config", "garbage-collector-config", "query-service-config", "rust-log-service-config", "work-queue-service-config" })
			objects.Add(ConfigMap(chroma, config, worker));

		objects.Add(Service(chroma, FnConsumer, "grpc", 50051, 50051, headless: false, type: null));
		objects.Add(Service(chroma, QueryService, "query-service-server-port", 50051, 50051, headless: true, type: "ClusterIP"));
		objects.Add(Service(chroma, Frontend, "server-port", FrontendPort, 8000, headless: false, type: "ClusterIP"));
		objects.Add(Service(chroma, LogService, "server-port", 50051, 50051, headless: true, type: "ClusterIP"));
		objects.Add(Service(chroma, Sysdb, "grpc", 50051, "grpc", headless: false, type: "ClusterIP"));
		objects.Add(Service(chroma, WorkQueue, "grpc", 50051, 50051, headless: false, type: null));
		return objects;
	}

	/// <summary>The Job of the migrations of the Postgres of the sysdb for the version, with Atlas; it runs where the sysdb runs.</summary>
	public static V1Job MigrationJob(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var spec = Spec(chroma);
		var url = $"postgres://$(PG_USERNAME):$(PG_PASSWORD)@{spec.Postgres.Host}:{PostgresPort(chroma).ToString(CultureInfo.InvariantCulture)}/{Database(chroma)}?sslmode={SslMode(chroma)}";
		return Own(chroma, new V1Job
		{
			ApiVersion = "batch/v1",
			Kind = "Job",
			Metadata = new V1ObjectMeta { Name = MigrationJobName(version) },
			Spec = new V1JobSpec
			{
				Template = new V1PodTemplateSpec
				{
					Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = "sysdb-migration" } },
					Spec = new V1PodSpec
					{
						RestartPolicy = "OnFailure",
						Containers =
						[
							new V1Container
							{
								Args = ["migrate", "apply", "--url", url],
								Image = Image(chroma, version, "sysdb-migration"),
								ImagePullPolicy = "IfNotPresent",
								Name = "migration",
								Env = SysdbEnv(chroma),
							},
						],
						NodeSelector = Copy(spec.Sysdb?.NodeSelector),
						Tolerations = spec.Sysdb?.Tolerations?.ToList(),
					},
				},
			},
		});
	}

	/// <summary>The Deployment or StatefulSet of a component.</summary>
	public static IKubernetesObject<V1ObjectMeta> Workload(V1Alpha1Chroma chroma, DistributedVersion version, string component) => component switch
	{
		Frontend => FrontendDeployment(chroma, version),
		Sysdb => SysdbDeployment(chroma, version),
		LogService => LogStatefulSet(chroma, version),
		QueryService => QueryStatefulSet(chroma, version),
		CompactionService => CompactionStatefulSet(chroma, version),
		GarbageCollector => GarbageCollectorStatefulSet(chroma, version),
		WorkQueue => WorkQueueStatefulSet(chroma, version),
		FnConsumer => FnConsumerDeployment(chroma, version),
		_ => throw new ArgumentOutOfRangeException(nameof(component), component, "Not a component of distributed Chroma."),
	};

	/// <summary>All the objects, in the order of their creation.</summary>
	public static List<IKubernetesObject<V1ObjectMeta>> All(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var objects = Foundation(chroma, version);
		objects.Add(MigrationJob(chroma, version));
		objects.AddRange(Tiers.SelectMany(t => t).Select(c => Workload(chroma, version, c)));
		return objects;
	}

	private static V1Deployment FrontendDeployment(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var placement = ComponentOf(chroma, Frontend);
		return Deployment(chroma, Frontend, memberType: null, "rust-frontend-service-serviceaccount", new V1Container
		{
			Name = Frontend,
			// values.dev.yaml: the image runs the CLI, shared with the single node, which takes the config file as argument.
			Command = ["chroma", "run", "$(CONFIG_PATH)"],
			Image = Image(chroma, version, Frontend),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = new V1Probe { PeriodSeconds = 1, FailureThreshold = 30, HttpGet = new V1HTTPGetAction { Port = FrontendPort, Path = "/api/v2/healthcheck" } },
			Ports = [new V1ContainerPort { ContainerPort = FrontendPort }],
			Resources = Resources(chroma, Frontend),
			Env = [Value("CONFIG_PATH", "/config/config.yaml"), Value("RUST_BACKTRACE", "1")],
			VolumeMounts = [new V1VolumeMount { Name = "rust-frontend-service-config", MountPath = "/config/" }],
		}, [ConfigVolume("rust-frontend-service-config")], placement, affinity: false, spread: false);
	}

	private static V1Deployment SysdbDeployment(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var spec = Spec(chroma);
		var ns = chroma.Namespace();
		var host = spec.Postgres.Host;
		var flags = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			// values.dev.yaml
			["version-file-enabled"] = "true",
			["s3-force-path-style"] = "true",
			["create-bucket-if-not-exists"] = "true",
			// The spec. The keys of S3 are empty, so that the sysdb takes AWS_* from the environment; the credentials of Postgres are
			// expanded by the shell.
			["kubernetes-namespace"] = ns,
			["db-address"] = host,
			["read-db-address"] = host,
			["db-port"] = PostgresPort(chroma).ToString(CultureInfo.InvariantCulture),
			["db-name"] = Database(chroma),
			["ssl-mode"] = SslMode(chroma),
			["username"] = "$PG_USERNAME",
			["password"] = "$PG_PASSWORD",
			["bucket-name"] = Bucket(chroma),
			["s3-region"] = Region(chroma),
			["s3-endpoint"] = StorageEndpoint(chroma) ?? "",
			["s3-access-key-id"] = "",
			["s3-secret-access-key"] = "",
		};
		// As the template of the chart writes it: "coordinator coordinator {{ range }} --key=value {{ end }}", one line.
		var command = "coordinator coordinator " + string.Concat(flags.Select(f => $" --{f.Key}={f.Value} "));
		var env = SysdbEnv(chroma);
		env.Add(Field("POD_NAME", "metadata.name"));
		env.Add(Field("POD_NAMESPACE", "metadata.namespace"));
		return Deployment(chroma, Sysdb, memberType: null, "sysdb-serviceaccount", new V1Container
		{
			Command = ["/bin/sh", "-c", command.TrimEnd()],
			Env = env,
			Image = Image(chroma, version, Sysdb),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051),
			Name = Sysdb,
			Ports = [new V1ContainerPort { ContainerPort = 50051, Name = "grpc" }],
			Resources = Resources(chroma, Sysdb),
		}, volumes: null, ComponentOf(chroma, Sysdb), affinity: false, spread: false);
	}

	private static V1StatefulSet LogStatefulSet(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar> { Value("CONFIG_PATH", "/config/config.yaml"), Field("CHROMA_LOG_SERVICE__MY_MEMBER_ID", "metadata.name") };
		env.AddRange(StorageEnv(chroma, backtrace: false));
		return StatefulSet(chroma, LogService, LogService, serviceName: LogService, "rust-log-service-serviceaccount", new V1Container
		{
			Name = LogService,
			Image = Image(chroma, version, LogService),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051, period: 5),
			VolumeMounts = ConfigAndCacheMounts(LogService),
			Ports = [new V1ContainerPort { ContainerPort = 50051 }],
			Env = env,
			Resources = Resources(chroma, LogService),
		}, ConfigAndCache(LogService, "rust-log-service-config", "/local/cache/chroma-log-service"), updateStrategy: false);
	}

	private static V1StatefulSet QueryStatefulSet(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar> { Value("CONFIG_PATH", "/config/config.yaml") };
		env.AddRange(StorageEnv(chroma, backtrace: true));
		env.Add(Value("_RJEM_MALLOC_CONF", Jemalloc));
		env.Add(Field("CHROMA_QUERY_SERVICE__MY_MEMBER_ID", "metadata.name"));
		return StatefulSet(chroma, QueryService, QueryService, serviceName: QueryService, "query-service-serviceaccount", new V1Container
		{
			Name = QueryService,
			Image = Image(chroma, version, QueryService),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051, service: "chroma.QueryExecutor"),
			VolumeMounts = ConfigAndCacheMounts(QueryService),
			Ports = [new V1ContainerPort { ContainerPort = 50051 }, Pprof()],
			Env = env,
			Resources = Resources(chroma, QueryService),
		}, ConfigAndCache(QueryService, "query-service-config", "/local/cache/chroma-query-service"), updateStrategy: true);
	}

	private static V1StatefulSet CompactionStatefulSet(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar> { Value("CONFIG_PATH", "/config/config.yaml") };
		env.AddRange(StorageEnv(chroma, backtrace: true));
		env.Add(Field("CHROMA_COMPACTION_SERVICE__MY_MEMBER_ID", "metadata.name"));
		return StatefulSet(chroma, CompactionService, CompactionService, serviceName: null, "compaction-service-serviceaccount", new V1Container
		{
			Name = CompactionService,
			Image = Image(chroma, version, CompactionService),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051),
			VolumeMounts = ConfigAndCacheMounts(CompactionService),
			Ports = [new V1ContainerPort { ContainerPort = 50051 }, Pprof()],
			Env = env,
			Resources = Resources(chroma, CompactionService),
		}, ConfigAndCache(CompactionService, "compaction-service-config", "/local/cache/chroma-compaction-service"), updateStrategy: false);
	}

	private static V1StatefulSet GarbageCollectorStatefulSet(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar>
		{
			Value("_RJEM_MALLOC_CONF", Jemalloc),
			Value("CONFIG_PATH", "/config/config.yaml"),
			Field("CHROMA_GC_MY_MEMBER_ID", "metadata.name"),
		};
		env.AddRange(StorageEnv(chroma, backtrace: true));
		var statefulSet = StatefulSet(chroma, GarbageCollector, "garbage-collection-service", serviceName: null, "garbage-collector-serviceaccount", new V1Container
		{
			Name = GarbageCollector,
			Image = Image(chroma, version, GarbageCollector),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50055),
			Ports = [Pprof()],
			Resources = Resources(chroma, GarbageCollector),
			VolumeMounts = ConfigAndCacheMounts(GarbageCollector),
			Env = env,
		}, ConfigAndCache(GarbageCollector, "garbage-collector-config", "/local/cache/chroma-garbage-collector"), updateStrategy: false);
		statefulSet.Metadata.Labels["app"] = GarbageCollector;
		return statefulSet;
	}

	private static V1StatefulSet WorkQueueStatefulSet(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar> { Value("CONFIG_PATH", "/config/config.yaml") };
		env.AddRange(StorageEnv(chroma, backtrace: true));
		var statefulSet = StatefulSet(chroma, WorkQueue, memberType: null, serviceName: null, "work-queue-service-serviceaccount", new V1Container
		{
			Name = WorkQueue,
			Image = Image(chroma, version, WorkQueue),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051, service: "chroma.WorkQueueService"),
			VolumeMounts = [new V1VolumeMount { Name = "work-queue-service-config", MountPath = "/config/" }],
			Ports = [new V1ContainerPort { ContainerPort = 50051, Protocol = "TCP", Name = "grpc" }],
			Env = env,
			Resources = Resources(chroma, WorkQueue),
		}, [ConfigVolume("work-queue-service-config")], updateStrategy: false);
		return statefulSet;
	}

	private static V1Deployment FnConsumerDeployment(V1Alpha1Chroma chroma, DistributedVersion version)
	{
		var env = new List<V1EnvVar> { Value("CONFIG_PATH", "/config/config.yaml") };
		env.AddRange(StorageEnv(chroma, backtrace: true));
		env.Add(Field("CHROMA_FN_CONSUMER_SERVICE__MY_MEMBER_ID", "metadata.name"));
		return Deployment(chroma, FnConsumer, memberType: FnConsumer, "fn-consumer-service-account", new V1Container
		{
			Name = FnConsumer,
			Image = Image(chroma, version, FnConsumer),
			ImagePullPolicy = "IfNotPresent",
			ReadinessProbe = Grpc(50051),
			VolumeMounts = [new V1VolumeMount { Name = "fn-consumer-config", MountPath = "/config/" }],
			Ports = [new V1ContainerPort { ContainerPort = 50051, Protocol = "TCP", Name = "grpc" }],
			Env = env,
			Resources = Resources(chroma, FnConsumer),
		}, [ConfigVolume("fn-consumer-config")], ComponentOf(chroma, FnConsumer), affinity: true, spread: true);
	}

	private static V1Deployment Deployment(V1Alpha1Chroma chroma, string name, string? memberType, string account, V1Container container,
		List<V1Volume>? volumes, V1Alpha1Chroma.ComponentSpec? placement, bool affinity, bool spread)
	{
		var podSpec = new V1PodSpec { ServiceAccountName = account, Containers = [container], Volumes = volumes };
		Place(podSpec, placement, affinity, spread ? memberType : null);
		return Own(chroma, new V1Deployment
		{
			ApiVersion = "apps/v1",
			Kind = "Deployment",
			Metadata = new V1ObjectMeta { Name = name },
			Spec = new V1DeploymentSpec
			{
				Replicas = Replicas(chroma, name),
				Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = name } },
				Template = new V1PodTemplateSpec { Metadata = new V1ObjectMeta { Labels = PodLabels(name, memberType) }, Spec = podSpec },
			},
		});
	}

	private static V1StatefulSet StatefulSet(V1Alpha1Chroma chroma, string name, string? memberType, string? serviceName, string account,
		V1Container container, List<V1Volume> volumes, bool updateStrategy)
	{
		var podSpec = new V1PodSpec { ServiceAccountName = account, Volumes = volumes, Containers = [container] };
		// The work queue has no placement in the chart, and no spread.
		if (name != WorkQueue)
			Place(podSpec, ComponentOf(chroma, name), affinity: true, memberType);
		return Own(chroma, new V1StatefulSet
		{
			ApiVersion = "apps/v1",
			Kind = "StatefulSet",
			Metadata = new V1ObjectMeta { Name = name },
			Spec = new V1StatefulSetSpec
			{
				ServiceName = serviceName,
				Replicas = Replicas(chroma, name),
				Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = name } },
				UpdateStrategy = updateStrategy ? new V1StatefulSetUpdateStrategy { Type = "RollingUpdate" } : null,
				Template = new V1PodTemplateSpec { Metadata = new V1ObjectMeta { Labels = PodLabels(name, memberType) }, Spec = podSpec },
			},
		});
	}

	/// <summary>nodeSelector, tolerations and affinity of the spec, then the topology spread of the chart for the components with a member type.</summary>
	private static void Place(V1PodSpec podSpec, V1Alpha1Chroma.ComponentSpec? placement, bool affinity, string? spreadMemberType)
	{
		podSpec.Tolerations = placement?.Tolerations is { Count: > 0 } tolerations ? tolerations.ToList() : null;
		podSpec.NodeSelector = placement?.NodeSelector is { Count: > 0 } selector ? Copy(selector) : null;
		podSpec.Affinity = affinity ? placement?.Affinity : null;
		if (spreadMemberType is not null)
		{
			podSpec.TopologySpreadConstraints =
			[
				new V1TopologySpreadConstraint
				{
					MaxSkew = 1,
					TopologyKey = "kubernetes.io/hostname",
					WhenUnsatisfiable = "ScheduleAnyway",
					LabelSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["member-type"] = spreadMemberType } },
				},
			];
		}
	}

	private static Dictionary<string, string> PodLabels(string name, string? memberType)
	{
		var labels = new Dictionary<string, string> { ["app"] = name };
		if (memberType is not null)
			labels["member-type"] = memberType;
		return labels;
	}

	/// <summary>The environment of the sysdb and of the migration Job: the credentials of Postgres, and of S3 for the sysdb.</summary>
	private static List<V1EnvVar> SysdbEnv(V1Alpha1Chroma chroma)
	{
		var spec = Spec(chroma);
		var postgres = spec.Postgres.CredentialsSecret.Name;
		var storage = spec.ObjectStorage.S3.CredentialsSecret.Name;
		return
		[
			Value("OPTL_TRACING_ENDPOINT", $"otel-collector.{chroma.Namespace()}.svc.cluster.local:4317"),
			Secret("PG_USERNAME", postgres, UsernameKey),
			Secret("PG_PASSWORD", postgres, PasswordKey),
			Secret("AWS_ACCESS_KEY_ID", storage, AccessKeyIdKey),
			Secret("AWS_SECRET_ACCESS_KEY", storage, SecretAccessKeyKey),
		];
	}

	/// <summary>The environment of the AWS mode of rust/storage: the credentials, the region and the endpoint, if any.</summary>
	private static List<V1EnvVar> StorageEnv(V1Alpha1Chroma chroma, bool backtrace)
	{
		var storage = Spec(chroma).ObjectStorage.S3.CredentialsSecret.Name;
		var env = new List<V1EnvVar>();
		if (backtrace)
			env.Add(Value("RUST_BACKTRACE", "1"));
		env.Add(Secret("AWS_ACCESS_KEY_ID", storage, AccessKeyIdKey));
		env.Add(Secret("AWS_SECRET_ACCESS_KEY", storage, SecretAccessKeyKey));
		env.Add(Value("AWS_REGION", Region(chroma)));
		if (StorageEndpoint(chroma) is { } endpoint)
			env.Add(Value("AWS_ENDPOINT_URL_S3", endpoint));
		return env;
	}

	/// <summary>The resources of the chart, with the quantities of the spec over them, one by one, as Helm merges values.</summary>
	private static V1ResourceRequirements Resources(V1Alpha1Chroma chroma, string component)
	{
		var defaults = ChartDefaults[component];
		var resources = ComponentOf(chroma, component)?.Resources;
		return new V1ResourceRequirements
		{
			Limits = new Dictionary<string, ResourceQuantity>
			{
				["cpu"] = new(resources?.Limits?.Cpu ?? defaults.LimitCpu),
				["memory"] = new(resources?.Limits?.Memory ?? defaults.LimitMemory),
			},
			Requests = new Dictionary<string, ResourceQuantity>
			{
				["cpu"] = new(resources?.Requests?.Cpu ?? defaults.RequestCpu),
				["memory"] = new(resources?.Requests?.Memory ?? defaults.RequestMemory),
			},
		};
	}

	private static V1Probe Grpc(int port, string? service = null, int period = 1) =>
		new() { PeriodSeconds = period, FailureThreshold = 30, Grpc = new V1GRPCAction { Port = port, Service = service } };

	private static V1ContainerPort Pprof() => new() { ContainerPort = 6060, Protocol = "TCP", Name = "pprof" };

	private static V1Volume ConfigVolume(string name) => new() { Name = name, ConfigMap = new V1ConfigMapVolumeSource { Name = name } };

	private static List<V1Volume> ConfigAndCache(string component, string config, string hostPath) =>
	[
		new V1Volume { Name = component + "-config", ConfigMap = new V1ConfigMapVolumeSource { Name = config } },
		new V1Volume { Name = component + "-cache", HostPath = new V1HostPathVolumeSource { Path = hostPath, Type = "DirectoryOrCreate" } },
	];

	private static List<V1VolumeMount> ConfigAndCacheMounts(string component) =>
	[
		new V1VolumeMount { Name = component + "-config", MountPath = "/config/" },
		new V1VolumeMount { Name = component + "-cache", MountPath = "/cache/" },
	];

	private static V1EnvVar Value(string name, string value) => new() { Name = name, Value = value };

	private static V1EnvVar Field(string name, string path) =>
		new() { Name = name, ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = path } } };

	private static V1EnvVar Secret(string name, string secret, string key) =>
		new() { Name = name, ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = secret, Key = key } } };

	private static V1Role Role(V1Alpha1Chroma chroma, string name, string group, string resource, string[] verbs, string? resourceName = null) =>
		Own(chroma, new V1Role
		{
			ApiVersion = "rbac.authorization.k8s.io/v1",
			Kind = "Role",
			Metadata = new V1ObjectMeta { Name = name },
			Rules = [new V1PolicyRule { ApiGroups = [group], Resources = [resource], ResourceNames = resourceName is null ? null : [resourceName], Verbs = verbs }],
		});

	private static V1ConfigMap ConfigMap(V1Alpha1Chroma chroma, string name, string config) =>
		Own(chroma, new V1ConfigMap
		{
			ApiVersion = "v1",
			Kind = "ConfigMap",
			Metadata = new V1ObjectMeta { Name = name },
			Data = new Dictionary<string, string> { ["config.yaml"] = config },
		});

	private static V1Service Service(V1Alpha1Chroma chroma, string name, string port, int number, IntOrString target, bool headless, string? type) =>
		Own(chroma, new V1Service
		{
			ApiVersion = "v1",
			Kind = "Service",
			Metadata = new V1ObjectMeta { Name = name },
			Spec = new V1ServiceSpec
			{
				Ports = [new V1ServicePort { Name = port, Port = number, TargetPort = target }],
				ClusterIP = headless ? "None" : null,
				Selector = new Dictionary<string, string> { ["app"] = name },
				Type = type,
			},
		});

	private static Dictionary<string, string>? Copy(IDictionary<string, string>? values) =>
		values is null || values.Count == 0 ? null : new Dictionary<string, string>(values);

	/// <summary>The namespace of the Chroma, its labels and the Chroma as controller.</summary>
	private static T Own<T>(V1Alpha1Chroma chroma, T value)
		where T : IKubernetesObject<V1ObjectMeta>
	{
		var metadata = value.Metadata;
		metadata.NamespaceProperty = chroma.Namespace();
		metadata.Labels ??= new Dictionary<string, string>();
		metadata.Labels["app.kubernetes.io/managed-by"] = Labels.ManagedBy;
		metadata.Labels[Labels.Chroma] = chroma.Name();
		metadata.OwnerReferences =
		[
			new V1OwnerReference
			{
				ApiVersion = chroma.ApiVersion,
				Kind = chroma.Kind,
				Name = chroma.Name(),
				Uid = chroma.Uid(),
				Controller = true,
				BlockOwnerDeletion = true,
			},
		];
		return value;
	}
}
