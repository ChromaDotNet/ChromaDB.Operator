# ChromaDB.Operator

A Kubernetes operator for single-node [Chroma](https://www.trychroma.com/) 1.x, written in .NET with
[KubeOps](https://github.com/dotnet/dotnet-operator-sdk). It follows the patterns of [CloudNativePG](https://cloudnative-pg.io/):
the operator manages the Pod and its PersistentVolumeClaim directly, without a StatefulSet, and keeps the state in the status of the
resource.

Community project, not affiliated with Chroma.

> v0.1, work in progress: nothing is released yet, and the API can change.

## What v0.1 does

- A `ChromaServer` gives one Chroma server: a PersistentVolumeClaim for `/data`, a Pod, a ConfigMap with the configuration, and a Service.
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
  `ghcr.io/chroma-core/chroma`. Whoever can create a ChromaServer chooses its image, and the operator runs it.

Not yet: backups (with CSI VolumeSnapshots), resize of the claim, hibernation, metrics, a Helm chart, authentication, distributed Chroma.

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

`deploy/chroma-operator.yaml` has the CustomResourceDefinition, the RBAC, and the Deployment of the operator in the namespace
`chroma-operator-system`. Two environment variables of the Deployment configure the operator:

| Variable | Default | |
|---|---|---|
| `CHROMA_DEFAULT_IMAGE` | `docker.io/chromadb/chroma:1.5.9` | The image of a ChromaServer without `spec.image`. |
| `CHROMA_ALLOWED_IMAGES` | `docker.io/chromadb/chroma,ghcr.io/chroma-core/chroma` | The repositories a ChromaServer can use, separated by commas, or `*` for any. |

Who can create a ChromaServer is up to the RBAC of the cluster, as for any other resource.

The operator runs as the ServiceAccount `chroma-operator`. Its ClusterRole has only what the operator uses:
- ChromaServers: read, watch and update; update of their status and of their finalizers;
- Pods: read, watch, create, update and delete;
- ConfigMaps, Services and PersistentVolumeClaims: read, create and update.

The operator never deletes a claim.

## Uninstall

`kubectl delete -f deploy/chroma-operator.yaml` also deletes the CustomResourceDefinition, and with it every ChromaServer of the
cluster. Their Pods, Services and ConfigMaps go too, and so do the claims of the servers with `pvcDeletionPolicy: Delete`.

The manifest deletes the namespace of the operator first, so the operator stops before its ChromaServers are deleted. A ChromaServer
that owns its claim, with `Delete`, has a finalizer that only the operator removes (see below). With the operator gone, such a server
stays in deletion, and so do the CustomResourceDefinition, the namespace that holds the server, and `kubectl delete` itself.

Uninstall in this order:
1. Set `Retain` on the ChromaServers whose data you want to keep.
2. Delete the ChromaServers while the operator runs, and wait until they are gone:
   ```
   kubectl delete chromaserver --all -A
   kubectl wait --for=delete chromaserver --all -A --timeout=5m
   ```
3. Delete the manifest.

If the manifest was deleted first and the deletion waits, remove the finalizer from the ChromaServers that are left. Their claims, owned
with `Delete`, are deleted with them:
```
kubectl get chromaserver -A -o custom-columns=NS:.metadata.namespace,NAME:.metadata.name --no-headers |
  while read ns name; do kubectl -n "$ns" patch chromaserver "$name" --type=merge -p '{"metadata":{"finalizers":null}}'; done
```

## A ChromaServer

```yaml
apiVersion: chroma.chromadotnet.org/v1alpha1
kind: ChromaServer
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
$ kubectl get chromaserver
NAME   PHASE   IMAGE                             DATA    AGE
demo   Ready   docker.io/chromadb/chroma:1.5.9   1.5.9   40s
```

Clients connect to `http://demo-rw.<namespace>.svc:8000`, which is also in `status.endpoint`.

| Field | |
|---|---|
| `image` | The image of Chroma with an exact version tag, optionally followed by `@sha256:<digest>`. Empty: the default image of the operator, written into the spec. |
| `imagePullPolicy` | `IfNotPresent` by default. |
| `storage.size`, `storage.storageClass` | The PersistentVolumeClaim of `/data`. An empty class means the default class of the cluster. |
| `resources` | Requests and limits of the container. Write quantities as strings, like `"1"` or `500m`. |
| `config.maxPayloadSizeBytes`, `config.corsAllowOrigins` | `max_payload_size_bytes` and `cors_allow_origins` of Chroma, in the ConfigMap mounted at `/config.yaml`. |
| `service.type`, `service.annotations` | The Service `<name>-rw`, `ClusterIP` by default. |
| `pvcDeletionPolicy` | `Retain` (default) keeps the claim when the ChromaServer is deleted. `Delete` deletes the claim with it. Whether the volume and its data go too depends on the `reclaimPolicy` of the StorageClass: most dynamic classes use `Delete`. |
| `nodeSelector`, `tolerations`, `priorityClassName` | Scheduling of the Pod. |

The status has `phase`, which is `Creating`, `Ready`, `Updating` or `Failed`, and the conditions `Ready` and `UpgradeBlocked`, whose
`reason` says why: for example `PodReady`, `NameConflict`, `ServiceRefused`, `PodRefused`, `CrashLoopBackOff`, `Downgrade`,
`ImageNotAllowed` or `DataVersionUnknown`. It also has:

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
highest version that has run on the data, annotate the ChromaServer with
`chroma.chromadotnet.org/declared-data-version: "<x.y.z>/<code>"`. A declaration names one incident: a later one needs a new
declaration.

**A retained claim.** The data version is also kept on the claim, with whether it is unknown. A ChromaServer created again on a
retained claim therefore knows them.

**Deleting a ChromaServer.**
- A ChromaServer that owns its claim, with `Delete`, has the finalizer `chroma.chromadotnet.org/claimfinalizer`. The operator adds it
  before it makes the server an owner of the claim, and removes it after the server is no longer one.
- A change to `Retain` counts even when the ChromaServer is deleted right after it, or while the operator is stopped. The finalizer
  keeps the ChromaServer until the operator has removed it from the owners of the claim.
- A change to `Delete` counts once a pass has applied it, within seconds. A ChromaServer deleted before that keeps its claim, and the
  operator says so in its log.
- While the operator is not running, a deleted ChromaServer that owns its claim waits, and so does a namespace being deleted with it.
  One with `Retain` is deleted at once.

`kubectl delete --cascade=foreground` is the exception: Kubernetes deletes the owned objects at once, alongside the finalizer. After a
change to `Retain`, wait until the claim has no owner references before such a deletion.

**Writes the API refuses**, like a claim of size 0, a Pod whose requests exceed its limits, an annotation key that is not valid, or a
ResourceQuota that is used up, fail the server with the explanation of the API, for example `PodRefused`.

**Objects of others.** The operator never changes a Pod, ConfigMap or Service with the name of one of its objects that it did not
create for this ChromaServer: the server fails with the reason `NameConflict`. A claim `<name>-1` is used only if the server controls
it, or if it has the labels of a claim retained by a ChromaServer of this name. To give the data of another claim to a ChromaServer,
label it with `chroma.chromadotnet.org/server=<name>` and `app.kubernetes.io/managed-by=chroma-operator`.

**Names.** The name of a ChromaServer is a DNS label of at most 60 characters, so that its Service `<name>-rw` is a valid DNS label.

## Development

The project targets .NET 10 and uses KubeOps 13.3.4.

```
dotnet test tests/ChromaDotNet.Operator.Tests      # unit tests
bash build/manifest.sh                             # regenerates deploy/chroma-operator.yaml from the code
```

The end-to-end tests run against a k3d cluster. Every command gets the kubeconfig and the context of the test cluster explicitly,
and the current context of the machine is never used. The scripts and the tests refuse any context that is not one of k3d (`k3d-*`):

```
k3d cluster create chroma-op --kubeconfig-switch-context=false
k3d kubeconfig get chroma-op > <dir>/kubeconfig
CHROMA_OPERATOR_KUBECONFIG=<dir>/kubeconfig CHROMA_OPERATOR_CONTEXT=k3d-chroma-op bash build/e2e.sh
```

## License

[Apache License 2.0](LICENSE).
