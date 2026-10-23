#!/bin/bash
# Usage: build/e2e.sh [dotnet test arguments]. The end-to-end tests on a local k3d cluster:
#   1. builds the image of the operator and imports it, with the images of Chroma the tests use, into the cluster;
#   2. applies deploy/chroma-operator.yaml and waits for the operator;
#   3. runs tests/ChromaDotNet.Operator.E2E.
# The cluster is CHROMA_OPERATOR_CLUSTER (default chroma-op), its kubeconfig CHROMA_OPERATOR_KUBECONFIG, its context
# CHROMA_OPERATOR_CONTEXT (default k3d-<cluster>). Every kubectl gets them explicitly: the current context of the machine is never used.
# A new cluster: k3d cluster create <name> --kubeconfig-switch-context=false, then k3d kubeconfig get <name> > <kubeconfig>.
set -euo pipefail
cd "$(dirname "$0")/.."
cluster=${CHROMA_OPERATOR_CLUSTER:-chroma-op}
export CHROMA_OPERATOR_KUBECONFIG=${CHROMA_OPERATOR_KUBECONFIG:?the kubeconfig of the test cluster, not the one of the machine}
export CHROMA_OPERATOR_CONTEXT=${CHROMA_OPERATOR_CONTEXT:-k3d-$cluster}
export CHROMA_OPERATOR_KUBECTL=${KUBECTL:-kubectl}
k() { bash build/k.sh "$@"; }
k get namespace default -o name >/dev/null

docker build -q -t ghcr.io/chromadotnet/chroma-operator:dev . >/dev/null
for image in chromadb/chroma:1.5.3 chromadb/chroma:1.5.9; do docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null; done
MSYS_NO_PATHCONV=1 k3d image import -c "$cluster" ghcr.io/chromadotnet/chroma-operator:dev chromadb/chroma:1.5.3 chromadb/chroma:1.5.9 >/dev/null

k apply -f deploy/chroma-operator.yaml >/dev/null
# The image just built, whatever tag the manifest has.
k -n chroma-operator-system set image deploy/chroma-operator-operator operator=ghcr.io/chromadotnet/chroma-operator:dev >/dev/null
k -n chroma-operator-system rollout restart deploy/chroma-operator-operator >/dev/null
k -n chroma-operator-system rollout status deploy/chroma-operator-operator --timeout=180s >/dev/null
echo "operator: $(k -n chroma-operator-system get deploy chroma-operator-operator -o jsonpath='{.spec.template.spec.containers[0].image}')"

dotnet test tests/ChromaDotNet.Operator.E2E -c Release --logger "console;verbosity=normal" "$@"
