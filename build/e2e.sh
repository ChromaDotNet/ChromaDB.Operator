#!/bin/bash
# Usage: build/e2e.sh [dotnet test arguments]. The end-to-end tests on a local k3d cluster:
#   1. builds the image of the operator and imports it, with the images of Chroma the tests use, into the cluster;
#   2. applies deploy/chroma-operator.yaml and waits for the operator;
#   3. runs tests/ChromaDotNet.Operator.E2E.
# The cluster is CHROMA_OPERATOR_CLUSTER (default chroma-op), its kubeconfig CHROMA_OPERATOR_KUBECONFIG, its context
# CHROMA_OPERATOR_CONTEXT (default k3d-<cluster>, and nothing else). Every kubectl gets them explicitly: the current context of the
# machine is never used.
# A new cluster: k3d cluster create <name> --kubeconfig-switch-context=false, then k3d kubeconfig get <name> > <kubeconfig>.
set -euo pipefail
cd "$(dirname "$0")/.."
cluster=${CHROMA_OPERATOR_CLUSTER:-chroma-op}
export CHROMA_OPERATOR_KUBECONFIG=${CHROMA_OPERATOR_KUBECONFIG:?the kubeconfig of the test cluster, not the one of the machine}
export CHROMA_OPERATOR_CONTEXT=${CHROMA_OPERATOR_CONTEXT:-k3d-$cluster}
# The images go into the k3d cluster, the tests into the context: both must be the same local cluster.
if [ "$CHROMA_OPERATOR_CONTEXT" != "k3d-$cluster" ]; then
  echo "e2e.sh: the context $CHROMA_OPERATOR_CONTEXT is not the one of the k3d cluster $cluster (k3d-$cluster): refused" >&2
  exit 1
fi
export CHROMA_OPERATOR_KUBECTL=${KUBECTL:-kubectl}
# k.sh checks the context once, here; the calls below, and the one at the exit, use kubectl with the same kubeconfig and context.
bash build/k.sh get namespace default -o name >/dev/null
k() { "$CHROMA_OPERATOR_KUBECTL" --kubeconfig "$CHROMA_OPERATOR_KUBECONFIG" --context "$CHROMA_OPERATOR_CONTEXT" "$@"; }

docker build -q -t ghcr.io/chromadotnet/chroma-operator:dev . >/dev/null
images="chromadb/chroma:1.5.3 chromadb/chroma:1.5.9 ghcr.io/vecturadb/vecturadb:1.0.0-preview.1"
for image in $images; do docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null; done
MSYS_NO_PATHCONV=1 k3d image import -c "$cluster" ghcr.io/chromadotnet/chroma-operator:dev $images >/dev/null

# Kyverno, for the test of a mutating webhook that rewrites the image of the Pods: installed once, with its admission controller only.
if ! k get crd policies.kyverno.io >/dev/null 2>&1; then
  "${HELM:-helm}" --kubeconfig "$CHROMA_OPERATOR_KUBECONFIG" --kube-context "$CHROMA_OPERATOR_CONTEXT" upgrade --install kyverno kyverno \
    --repo https://kyverno.github.io/kyverno/ --version 3.5.2 -n kyverno --create-namespace --set admissionController.replicas=1 \
    --set backgroundController.enabled=false --set cleanupController.enabled=false --set reportsController.enabled=false \
    --wait --timeout 6m >/dev/null
fi

k apply -f deploy/chroma-operator.yaml >/dev/null
# A test stops the operator for a while: one replica again, also when an earlier run stopped before it put the operator back, and at
# the end of this run.
k -n chroma-operator-system scale deploy/chroma-operator-operator --replicas=1 >/dev/null
trap 'k -n chroma-operator-system scale deploy/chroma-operator-operator --replicas=1 >/dev/null' EXIT
# The image just built, whatever tag the manifest has.
k -n chroma-operator-system set image deploy/chroma-operator-operator operator=ghcr.io/chromadotnet/chroma-operator:dev >/dev/null
k -n chroma-operator-system rollout restart deploy/chroma-operator-operator >/dev/null
k -n chroma-operator-system rollout status deploy/chroma-operator-operator --timeout=180s >/dev/null
echo "operator: $(k -n chroma-operator-system get deploy chroma-operator-operator -o jsonpath='{.spec.template.spec.containers[0].image}')"

dotnet test tests/ChromaDotNet.Operator.E2E -c Release --logger "console;verbosity=normal" "$@"
