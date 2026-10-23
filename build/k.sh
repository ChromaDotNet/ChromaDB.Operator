#!/bin/bash
# kubectl on the test cluster only: the kubeconfig and the context are always given, never the current context of the machine.
# CHROMA_OPERATOR_KUBECONFIG and CHROMA_OPERATOR_CONTEXT choose them; KUBECTL the kubectl to use.
set -euo pipefail
: "${CHROMA_OPERATOR_KUBECONFIG:?the kubeconfig of the test cluster}"
: "${CHROMA_OPERATOR_CONTEXT:?the context of the test cluster}"
exec "${KUBECTL:-kubectl}" --kubeconfig "$CHROMA_OPERATOR_KUBECONFIG" --context "$CHROMA_OPERATOR_CONTEXT" "$@"
