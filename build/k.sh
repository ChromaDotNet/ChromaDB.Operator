#!/bin/bash
# kubectl on the test cluster only: the kubeconfig and the context are always given, never the current context of the machine.
# CHROMA_OPERATOR_KUBECONFIG and CHROMA_OPERATOR_CONTEXT choose them; KUBECTL the kubectl to use. The context must be the one of a
# local k3d cluster: named k3d-<cluster>, with its API server on this machine, on the port Docker publishes for that cluster. A wrong
# value must not reach another cluster, also through a local tunnel.
set -euo pipefail
: "${CHROMA_OPERATOR_KUBECONFIG:?the kubeconfig of the test cluster}"
: "${CHROMA_OPERATOR_CONTEXT:?the context of the test cluster}"
case "$CHROMA_OPERATOR_CONTEXT" in
  k3d-*) ;;
  *) echo "k.sh: $CHROMA_OPERATOR_CONTEXT is not the context of a k3d cluster (k3d-*): refused" >&2; exit 1 ;;
esac
# Read from the kubeconfig, without calling the cluster.
server=$("${KUBECTL:-kubectl}" --kubeconfig "$CHROMA_OPERATOR_KUBECONFIG" config view --minify --context "$CHROMA_OPERATOR_CONTEXT" \
  -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
  https://0.0.0.0:*|https://127.0.0.1:*|https://localhost:*|https://host.docker.internal:*|https://\[::1\]:*) ;;
  *) echo "k.sh: the API server of $CHROMA_OPERATOR_CONTEXT is $server, not on this machine: refused" >&2; exit 1 ;;
esac
cluster=${CHROMA_OPERATOR_CONTEXT#k3d-}
port=${server##*:}
port=${port%%/*}
if ! docker port "k3d-$cluster-serverlb" 6443/tcp 2>/dev/null | tr -d '\r' | grep -q ":$port\$"; then
  echo "k.sh: $server is not the API of the k3d cluster $cluster (docker port k3d-$cluster-serverlb 6443/tcp): refused" >&2
  exit 1
fi
exec "${KUBECTL:-kubectl}" --kubeconfig "$CHROMA_OPERATOR_KUBECONFIG" --context "$CHROMA_OPERATOR_CONTEXT" "$@"
