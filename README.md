# ChromaDB.Operator

A Kubernetes operator for [Chroma](https://www.trychroma.com/) 1.x, single-node and distributed, written in .NET with
[KubeOps](https://github.com/dotnet/dotnet-operator-sdk). It follows the patterns of [CloudNativePG](https://cloudnative-pg.io/):
for single-node Chroma the operator manages the Pod and its PersistentVolumeClaim directly, without a StatefulSet; for distributed
Chroma it creates the objects of the Helm chart of Chroma. Either way it keeps the state in the status of the resource.

Community project, not affiliated with Chroma.

> v0.1, work in progress: nothing is released yet, and the API can change.

## What v0.1 does

- A `Chroma` in the mode `single-node`, the default, gives one Chroma server: a PersistentVolumeClaim for `/data`, a Pod, a ConfigMap
  with the configuration, and a Service. In the mode `distributed` it gives the components of the Helm chart `distributed-chroma` of
  Chroma, on an external Postgres and an external S3 bucket: see [A distributed Chroma](#a-distributed-chroma). The mode cannot change
  after the creation.
- A change of the configuration restarts the Pod. The data stays on the claim.
- The Pod runs as a non-root user, with a read-only root filesystem and `/tmp` as an `emptyDir`. Chroma needs a writable `/tmp`: without
  it SQLite fails at the start. The probes are HTTP, since the image of Chroma has no `curl`.
- A new image replaces the Pod. `status.dataVersion` is the highest version of Chroma that has run on the data. An older image is
  refused, because Chroma migrates the data at its start and an older version does not open it again. Version 1.0.0, for example,
  panics on data opened by 1.5.9.
- The image needs an exact version tag, like `docker.io/chromadb/chroma:1.5.9`. The API refuses `latest` and the development tags.
- Without an image, the operator writes its default image into the spec. A new version of the operator therefore does not change
  the version of the servers already created.
- Only the repositories the operator allows can run. By default these are `docker.io/chromadb/chroma` and
  `ghcr.io/chroma-core/chroma`. Whoever can create a Chroma chooses its image, and the operator runs it.

Not yet: backups (with CSI VolumeSnapshots), resize of the claim, hibernation, metrics, a Helm chart, authentication.

## Install

There is no published image yet. Build it, load it into the nodes of the cluster, and apply the manifest:

```
docker build -t ghcr.io/chromadotnet/chroma-operator:dev .
k3d image import ghcr.io/chromadotnet/chroma-operator:dev -c <cluster>            # k3d
kind load docker-image ghcr.io/chromadotnet/chroma-operator:dev --name <cluster>   # kind
kubectl apply -f deploy/chroma-operator.yaml
```

On other clusters, push the image to a registry the nodes can pull from, then set it with
`kubectl -n chroma-operator-system set image deploy/chroma-operator-operator operator=<registry>/chroma-operator:<tag>`.

`deploy/chroma-operator.yaml` has the CustomResourceDefinitions, the RBAC, and the Deployment of the operator in the namespace
`chroma-operator-system`. Two environment variables of the Deployment configure the operator:

| Variable | Default | |
|---|---|---|
| `CHROMA_DEFAULT_IMAGE` | `docker.io/chromadb/chroma:1.5.9` | The image of a Chroma without `spec.image`. |
| `CHROMA_ALLOWED_IMAGES` | `docker.io/chromadb/chroma`, `ghcr.io/chroma-core/chroma` and the nine images of distributed Chroma in `docker.io/chromadb` | The repositories a Chroma can use, separated by commas; `<path>/*` for every repository under a path, like a mirror; or `*` for any. |

Who can create a Chroma is up to the RBAC of the cluster, as for any other resource.

The operator runs as the ServiceAccount `chroma-operator`. Its ClusterRole has only what the operator uses:
- Chroma resources: read, watch and update; update of their status and of their finalizers;
- Pods: read, watch, create, update and delete;
- PersistentVolumeClaims: read, create and update;
- for distributed Chroma, the objects of the chart: ConfigMaps, Services, ServiceAccounts, Roles, RoleBindings, Deployments,
  StatefulSets and Jobs, read, listed, created and patched; the Jobs of earlier versions deleted;
- Secrets: read only, for the checks of Postgres and of the bucket of a distributed Chroma;
- MemberLists and Leases: what the Roles of the chart grant to its components, since Kubernetes lets a Role grant only what its
  writer holds.

The operator never deletes a claim, a Postgres or a bucket.

One operator works at a time. The Deployment replaces its Pod with the strategy `Recreate`. The operator holds the Lease
`chroma-operator-leader` in its namespace, through a Role limited to it: with more replicas, or with a Pod left on a lost node,
another one waits. When the leader goes, another replica takes over once the Lease expires (15 seconds). The liveness and readiness
probes call `/healthz` on port 8080.

## Uninstall

`kubectl delete -f deploy/chroma-operator.yaml` also deletes the CustomResourceDefinition, and with it every Chroma of the
cluster. Their Pods, Services and ConfigMaps go too, and so do the claims of the servers with `pvcDeletionPolicy: Delete`.

The manifest deletes the namespace of the operator first, so the operator stops before its Chroma resources are deleted. A Chroma
that owns its claim, with `Delete`, has a finalizer that only the operator removes (see below). With the operator gone, such a server
stays in deletion, and so do the CustomResourceDefinition, the namespace that holds the server, and `kubectl delete` itself.

Uninstall in this order:
1. Set `Retain` on the Chroma resources whose data you want to keep.
2. Delete the Chroma resources while the operator runs, and wait until they are gone:
   ```
   kubectl delete chroma --all -A
   kubectl wait --for=delete chroma --all -A --timeout=5m
   ```
3. Delete the manifest.

If the manifest was deleted first and the deletion waits, remove the finalizer from the Chroma resources that are left. Their claims, owned
with `Delete`, are deleted with them:
```
kubectl get chroma -A -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name --no-headers |
  while read ns name; do kubectl -n "$ns" patch chroma "$name" --type=merge -p '{"metadata":{"finalizers":null}}'; done
```

## A Chroma

```yaml
apiVersion: chroma.chromadotnet.org/v1alpha1
kind: Chroma
metadata:
  name: demo
spec:
  storage:
    size: 10Gi
  resources:
    requests: {cpu: 500m, memory: 2Gi}
    limits: {memory: 4Gi}
```

```
$ kubectl get chroma
NAME   PHASE   IMAGE                             DATA    AGE
demo   Ready   docker.io/chromadb/chroma:1.5.9   1.5.9   40s
```

Clients connect to `http://demo-rw.<namespace>.svc:8000`, which is also in `status.endpoint`.

| Field | |
|---|---|
| `mode` | `single-node` (default) or `distributed` (see below). It cannot change after the creation: the data of the two modes are not the same. |
| `image` | The image of Chroma with an exact version tag, optionally followed by `@sha256:<digest>`. Empty: the default image of the operator, written into the spec. |
| `imagePullPolicy` | `IfNotPresent` by default. |
| `storage.size`, `storage.storageClass` | The PersistentVolumeClaim of `/data`. An empty class means the default class of the cluster. |
| `resources` | Requests and limits of the container. Write quantities as strings, like `"1"` or `500m`. |
| `config.maxPayloadSizeBytes`, `config.corsAllowOrigins` | `max_payload_size_bytes` and `cors_allow_origins` of Chroma, in the ConfigMap mounted at `/config.yaml`. |
| `service.type`, `service.annotations` | The Service `<name>-rw`, `ClusterIP` by default. |
| `pvcDeletionPolicy` | `Retain` (default) keeps the claim when the Chroma is deleted. `Delete` deletes the claim with it. Whether the volume and its data go too depends on the `reclaimPolicy` of the StorageClass: most dynamic classes use `Delete`. |
| `nodeSelector`, `tolerations`, `priorityClassName` | Scheduling of the Pod. |

The status has `phase`, which is `Creating`, `Ready`, `Updating` or `Failed`, and the conditions `Ready` and `UpgradeBlocked`, whose
`reason` says why: for example `PodReady`, `NameConflict`, `ServiceRefused`, `PodRefused`, `CrashLoopBackOff`, `Downgrade`,
`ImageNotAllowed` or `DataVersionUnknown`.

The condition `Reconciled` appears after a pass that failed for another reason than the server, with the reason in its message:
- `ApiError`: an answer of the API like 500;
- `Error`: no answer at all, like a network failure.

The operator tries again after 10 seconds, then 20 and 40, up to 5 minutes, and sets `Reconciled` back to `True` once a pass goes
through. The status also has:

| Field | |
|---|---|
| `image` | The image of the Pod, once it is ready. |
| `dataVersion` | The highest version of Chroma that has run on the data. It goes up before a newer Pod is created, since Chroma can migrate the data before the Pod reports that it started. |
| `recoveryImage` | The newest image seen that is allowed and not older than the data: the image a server whose spec is refused starts again with. |
| `dataVersionUnknown`, `dataVersionUnknownIncident` | Set when an image without a version ran on the data (see below). |
| `endpoint`, `pod`, `pvc`, `observedGeneration` | |

**An image that is not allowed** starts nothing. If a Pod is already running, it stays, and `UpgradeBlocked` explains why.

**An older image.** If `spec.image` is older than `status.dataVersion`, the running Pod stays, and `UpgradeBlocked` explains why. The
annotation `chroma.chromadotnet.org/allow-downgrade: "true"` forces the older image, for versions that share the same data format.
Only the exact value `true` counts. When a new Pod is needed, a refused server starts one with `status.recoveryImage`.

**Rolling back a failed upgrade.** The data version goes up before the new Pod is created. Going back to the previous image after an
upgrade that failed, even one whose image was never pulled, is therefore a downgrade: it needs the annotation.

**Annotations take up to 30 seconds.** A change to the annotations alone does not start a pass, since only a change to the spec
does. The operator reads `allow-downgrade` and `declared-data-version` at its next periodic pass, within 30 seconds.

**An image without a version.** A Pod edited by hand to an image like `latest` makes the version of the data unknown. While it is
unknown, the operator replaces or starts no Pod; a running Pod stays. The condition `UpgradeBlocked` gives a code. Once you know the
highest version that has run on the data, annotate the Chroma with
`chroma.chromadotnet.org/declared-data-version: "<x.y.z>/<code>"`. A declaration names one incident: a later one needs a new
declaration.

**A retained claim.** The data version is also kept on the claim, with whether it is unknown. A Chroma created again on a
retained claim therefore knows them.

**Deleting a Chroma.**
- A Chroma that owns its claim, with `Delete`, has the finalizer `chroma.chromadotnet.org/claimfinalizer`. The operator adds it
  before it makes the server an owner of the claim, and removes it after the server is no longer one.
- A change to `Retain` counts even when the Chroma is deleted right after it, or while the operator is stopped. The finalizer
  keeps the Chroma until the operator has removed it from the owners of the claim.
- A change to `Delete` counts once a pass has applied it, within seconds. A Chroma deleted before that keeps its claim, and the
  operator says so in its log.
- While the operator is not running, a deleted Chroma that owns its claim waits, and so does a namespace being deleted with it.
  One with `Retain` is deleted at once.

`kubectl delete --cascade=foreground` is the exception: Kubernetes deletes the owned objects at once, alongside the finalizer. After a
change to `Retain`, wait until the claim has no owner references before such a deletion.

**Writes the API refuses**, like a claim of size 0, a Pod whose requests exceed its limits, an annotation key that is not valid, or a
ResourceQuota that is used up, fail the server with the explanation of the API, for example `PodRefused`.

**Objects of others.** The operator never changes a Pod, ConfigMap or Service with the name of one of its objects that it did not
create for this Chroma: the server fails with the reason `NameConflict`. A claim `<name>-1` is used only if the server controls
it, or if it has the labels of a claim retained by a Chroma of this name. To give the data of another claim to a Chroma,
label it with `chroma.chromadotnet.org/chroma=<name>` and `app.kubernetes.io/managed-by=chroma-operator`.

**Webhooks that rewrite the image.** A mutating webhook can rewrite the image of the Pod at its creation, like a registry mirror or a
policy that adds a digest. The operator keeps the image the API admitted on the Pod, in the annotation
`chroma.chromadotnet.org/admitted-image`. A Pod that runs that image is the one the operator created, and it stays. An image changed
after the creation, by hand, is replaced. `status.image` is then the admitted image. A rewrite must keep the version tag:
`chromadb/chroma:1.5.9@sha256:...` keeps the version of the data known. `chromadb/chroma@sha256:...` alone has no version, so it makes
the version of the data unknown, as an image like `latest` does (see above).

**Names.** The name of a Chroma is a DNS label of at most 60 characters, so that its Service `<name>-rw` is a valid DNS label.

## A distributed Chroma

The operator creates the objects of `helm template` of the chart `distributed-chroma` of Chroma, for the values it derives from the
spec, over `values.yaml` and `values.dev.yaml` of the chart. A test compares them with the output of Helm (`build/chart.sh`).

```yaml
apiVersion: chroma.chromadotnet.org/v1alpha1
kind: Chroma
metadata:
  name: chroma
spec:
  mode: distributed
  distributed:
    version: 52ce844
    postgres:
      host: postgres
      credentialsSecret: {name: chroma-postgres}      # keys username and password
    objectStorage:
      s3:
        endpoint: http://10.43.119.132:8333           # empty for AWS S3
        bucket: chroma-storage
        credentialsSecret: {name: chroma-storage}     # keys accessKeyId and secretAccessKey
    queryService: {replicas: 2}
```

Clients connect to `http://rust-frontend-service.<namespace>.svc:8000`, which is also in `status.endpoint`.

**What it needs.**
- **A known version.** `version` is the commit of Chroma whose images run, as Chroma tags them. The operator runs only the versions it
  knows, each with its chart and the config files of the same commit: today `52ce844`, chart 0.1.94.
- **CPUs with AVX-512.** The published images of query, compaction, garbage collector, work queue and fn-consumer need it: elsewhere
  they end with SIGILL (exit code 132).
- **One distributed Chroma per namespace.** The objects of the chart have fixed names. A second one fails with
  `DistributedNamespaceTaken`.
- **A Postgres and an S3 bucket that exist.** The operator checks them before it creates anything, and never creates or deletes them:
  - the conditions `PostgresReady` and `ObjectStorageReady` say what is wrong, for example `SecretNotFound`, `KeyMissing`,
    `Unreachable`, `AuthenticationFailed`, `DatabaseNotFound`, `BucketNotFound` or `AccessDenied`;
  - the username and the password of Postgres may contain only letters, digits and `. _ ~ -`, because the chart passes them unquoted
    to a shell and into a URL.
- **For an S3 inside the cluster, the IP of its Service in the endpoint.** The S3 client of the services of Chroma addresses the bucket
  in the path only when the endpoint is an IP. With a name it asks for `<bucket>.<host>`, which the DNS of a cluster does not resolve.
  The operator checks it: `VirtualHostNotResolvable`.

**Fields of `spec.distributed`.**

| Field | |
|---|---|
| `version` | The commit of Chroma: its first 7 characters, or the whole hash. |
| `imageRegistry` | The registry and the path of the images, `docker.io/chromadb` by default. It must be in `CHROMA_ALLOWED_IMAGES`. |
| `postgres.host`, `port`, `database`, `sslMode`, `credentialsSecret` | The Postgres of the sysdb: port 5432, database `sysdb` and `sslMode` `disable` by default. |
| `objectStorage.s3.endpoint`, `bucket`, `region`, `credentialsSecret` | The bucket: `chroma-storage` in `us-east-1` by default. |
| `frontend`, `sysdb`, `logService`, `queryService`, `compactionService`, `garbageCollector`, `workQueue`, `fnConsumer` | Per component, what the values of the chart let change: `replicas` (frontend, log, query, compaction), `resources` (cpu and memory, merged over those of the chart), `nodeSelector`, `tolerations`, `affinity`. The API refuses a field the chart does not have for that component. |

**How it starts and updates.**
1. The Job `sysdb-migration-<version>` migrates Postgres, and nothing else starts until it completes. The Jobs of earlier versions go
   once it does, as Helm deletes them.
2. Then the components, tier by tier, each after the one before is updated and ready: the sysdb, the log service, the workers, and
   the frontend last.
3. `status.versionHistory` keeps the versions migrated on Postgres. Going back to one before the last is a downgrade, refused without
   the annotation `chroma.chromadotnet.org/allow-downgrade: "true"`.

**Repairs.** The operator repairs two known failures of distributed Chroma, and the condition `Repaired` says when and why:
- the work queue stays not ready when it starts before the storage: its Pod, not ready for 180 seconds, is deleted
  (`WorkQueueRestarted`);
- the sysdb runs for leader only once, and after it loses the Lease nobody lists the members of the components. A MemberList missing
  ready Pods for 180 seconds, with the sysdb ready as long, restarts the sysdb (`SysdbLeaderLost`).

**Status.** `phase` is `Creating`, `Migrating`, `Ready`, `Updating` or `Failed`. `components` has, for each component, its replicas,
the ready ones and the members of its MemberList. `version` is the version that runs on all the components.

**Differences from the chart**, and only these:
- the MemberLists have `members: []` instead of null;
- there is no Namespace, since the Chroma lives in it already;
- the images Chroma publishes, by commit, and the command of the frontend of `values.dev.yaml`;
- the credentials of Postgres and S3 come from the Secrets, through the environment, never in the ConfigMaps;
- the Chroma is the controller of every object, and the operator labels them with `chroma.chromadotnet.org/chroma`. The sysdb
  rewrites the MemberLists without their labels and owner, so the operator deletes them when the Chroma is deleted. If the operator
  is not running at that moment, the MemberLists stay: `kubectl -n <namespace> delete memberlists --all`;
- `values.dev.yaml` sets `CHROMA_ALLOW_RESET`, which lets anyone who reaches the frontend delete all the data: the operator does not.

## Development

The project targets .NET 10 and uses KubeOps 13.3.4.

```
dotnet test tests/ChromaDotNet.Operator.Tests      # unit tests
bash build/manifest.sh                             # regenerates deploy/chroma-operator.yaml from the code
bash build/chart.sh                                # renders the chart of distributed Chroma for the tests; needs helm and the network
```

The end-to-end tests run against a k3d cluster. Every command gets the kubeconfig and the context of the test cluster explicitly,
and the current context of the machine is never used. The scripts and the tests refuse any context that is not one of k3d (`k3d-*`).
`build/e2e.sh` installs [Kyverno](https://kyverno.io/) 1.15 on the cluster once, for the test of a mutating webhook:

```
k3d cluster create chroma-op --kubeconfig-switch-context=false
k3d kubeconfig get chroma-op > <dir>/kubeconfig
CHROMA_OPERATOR_KUBECONFIG=<dir>/kubeconfig CHROMA_OPERATOR_CONTEXT=k3d-chroma-op bash build/e2e.sh
```

## License

[Apache License 2.0](LICENSE).
