A Kubernetes operator for [Chroma](https://www.trychroma.com/), written in .NET. Independent project. Not affiliated with, endorsed by or sponsored by Chroma Inc. CHROMA is a registered trademark of Chroma Inc., used here only to describe compatibility.

```
kubectl apply -f https://github.com/ChromaDotNet/ChromaDB.Operator/releases/latest/download/chroma-operator.yaml
```

**Single-node Chroma.** One resource `Chroma` gives a server with its PersistentVolumeClaim, its configuration and its Service.
- The operator keeps the highest version of Chroma that has opened the data, and refuses an older image, since Chroma does not open
  data migrated by a newer version.
- A claim is kept by default when the Chroma is deleted, with its data version.

**Distributed Chroma, preview.** With `mode: distributed` the operator creates the objects of the Helm chart `distributed-chroma` of
Chroma, on an external Postgres and an external S3 bucket.
- A test compares them with `helm template` of the chart.
- The operator checks Postgres and the bucket before anything starts.
- The migration Job runs first, then the components tier by tier.
- Going back to an earlier version is refused without an annotation.
- The images of Chroma for the distributed mode need CPUs with AVX-512.

**VecturaDB, preview.** A single-node Chroma can run [VecturaDB](https://github.com/VecturaDB/VecturaDB), which adds sparse vectors,
BM25, the Search API and collection fork to the single node of Chroma, with the same API and on the same data.
- The data passes from Chroma to VecturaDB only along the passages the operator knows, each one tried on a volume first.
- Back to Chroma needs an annotation, and the status says what Chroma does not see of the data.

**Known limit.** On AKS a change of a Chroma once reached the operator and was applied only after a restart of the operator. The cause
is not known. Two safety nets cover it:
- every Chroma goes back into the queue of the operator once a minute;
- when a Chroma has had no pass for 5 minutes, the liveness probe of the operator fails and Kubernetes restarts the operator.

See the [README](https://github.com/ChromaDotNet/ChromaDB.Operator#readme) for the fields, the status and the limits.
