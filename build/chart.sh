#!/bin/bash
# Usage: build/chart.sh. The reference of the distributed mode: for each case of the tests it renders
# the chart distributed-chroma of Chroma with helm template, with the values the operator derives from the spec of the case,
# into tests/ChromaDotNet.Operator.Tests/Chart. The operator must create the same objects; ChartTests compares them.
# It also fetches the files the operator embeds from the same commit of Chroma: the config files of the services, and the
# MemberList CRD of the chart for deploy/. The versions are those of DistributedVersion.Known, with their charts: a new version gets
# its line in versions and a case, then this runs, the diff is read and the code adapted.
# Needs helm 3.8 or later and the network (ghcr.io, raw.githubusercontent.com); it reads no cluster.
set -euo pipefail
cd "$(dirname "$0")/.."
# The commit of Chroma and the chart of the same commit, newest first.
versions=(
  "52ce8448cb38a498f1660d55ea80df25b78f000a 0.1.94"
  "26e51e79746ac8b3c2ff9d5fa9bada26f5acdbc8 0.1.93"
)
out=tests/ChromaDotNet.Operator.Tests/Chart
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

# The chart and the config files of a version, which becomes the one of the next renders.
use() { # commit chart_version
  commit=$1 chart_version=$2 tag=${1:0:7}
  configs=src/ChromaDotNet.Operator/Distributed/$tag
  chart=$work/$tag/distributed-chroma
  if [ ! -d "$chart" ]; then
    helm pull oci://ghcr.io/chroma-core/helm-charts/distributed-chroma --version "$chart_version" --untar --untardir "$work/$tag" >/dev/null
    mkdir -p "$configs"
    local raw=https://raw.githubusercontent.com/chroma-core/chroma/$commit
    curl -fsSL "$raw/rust/frontend/sample_configs/distributed.yaml" -o "$configs/frontend.yaml"
    curl -fsSL "$raw/rust/worker/chroma_config.yaml" -o "$configs/worker.yaml"
  fi
}

# The corrections to a config file of Chroma, as the operator makes them in DistributedConfig:
# 1. every storage block from the Minio mode, with its fixed development keys and endpoint, to the AWS mode, which reads AWS_*
#    from the environment; the block of the garbage collector has no credentials at all (Minio by default), so it gets them;
# 2. the namespace of the Chroma in the names of the services and of the MemberLists;
# 3. the bucket of the spec, after the namespace, so that a bucket ending in .chroma stays as it is.
configure() { # file namespace bucket blocks: the number of storage blocks without credentials, 1 in the workers' file
  sed 's/credentials: "Minio"/credentials: "AWS"/' "$1" |
    awk -v expected="$4" '{ print }
      $0 == "  storage_config:" { block = 1; next }
      block == 1 && $0 == "    s3:" { block = 2; next }
      block == 2 && $0 ~ /^      bucket: / { print "      credentials: \"AWS\""; added++ }
      { block = 0 }
      END { if (added != expected) exit 1 }' |
    sed -e "s/\.chroma\"/.$2\"/g" \
      -e "s/kube_namespace: \"chroma\"/kube_namespace: \"$2\"/" \
      -e "s/\.chroma\.svc\.cluster\.local/.$2.svc.cluster.local/g" \
      -e "s/bucket: \"chroma-storage\"/bucket: \"$3\"/"
}
# Every version: its config files have the storage blocks expected, and its chart the MemberList CRD of the newest.
for version in "${versions[@]}"; do
  use $version
  configure "$configs/worker.yaml" chroma chroma-storage 1 >"$work/check.yaml"
  if grep -q 'credentials: "Minio"' "$work/check.yaml" || [ "$(grep -c 'credentials: "AWS"' "$work/check.yaml")" != 8 ]; then
    echo "The config file of the workers at $tag does not have the 7 storage blocks and the block of the garbage collector expected." >&2
    exit 1
  fi
done
use ${versions[0]}
cp "$chart/crds/memberlist_crd.yaml" deploy/memberlist-crd.yaml
for version in "${versions[@]:1}"; do
  use $version
  cmp -s "$chart/crds/memberlist_crd.yaml" deploy/memberlist-crd.yaml || { echo "The MemberList CRD of the chart $chart_version differs from the newest." >&2; exit 1; }
done

# The values the operator derives from a spec, after values.yaml and values.dev.yaml of the chart, for the published images and
# for the external Postgres and object storage, whose credentials come from Secrets.
values() { # namespace registry bucket endpoint region pg_host pg_port pg_db pg_sslmode pg_secret s3_secret
  local ns=$1 registry=$2 bucket=$3 endpoint=$4 region=$5 host=$6 port=$7 db=$8 sslmode=$9 pg=${10} s3=${11}
  local endpoint_env='' endpoint_raw=''
  if [ -n "$endpoint" ]; then
    endpoint_env="
    - name: AWS_ENDPOINT_URL_S3
      value: 'value: \"$endpoint\"'"
    endpoint_raw="
    - name: AWS_ENDPOINT_URL_S3
      value: \"$endpoint\""
  fi
  cat <<EOF
namespace: $ns
rustFrontendService:
  image: {repository: '$registry/rust-frontend-service-oss', tag: '$tag'}
  otherEnvConfig: |
    - name: RUST_BACKTRACE
      value: "1"
sysdb:
  image: {repository: '$registry/sysdb-service', tag: '$tag'}
  flags:
    kubernetes-namespace: $ns
    db-address: $host
    read-db-address: $host
    db-port: $port
    db-name: $db
    ssl-mode: $sslmode
    username: \$PG_USERNAME
    password: \$PG_PASSWORD
    bucket-name: $bucket
    s3-region: $region
    s3-endpoint: "$endpoint"
    s3-access-key-id: ""
    s3-secret-access-key: ""
  env:
    - name: OPTL_TRACING_ENDPOINT
      value: 'value: "otel-collector.$ns.svc.cluster.local:4317"'
    - name: PG_USERNAME
      value: |
        valueFrom:
          secretKeyRef: {name: $pg, key: username}
    - name: PG_PASSWORD
      value: |
        valueFrom:
          secretKeyRef: {name: $pg, key: password}
    - name: AWS_ACCESS_KEY_ID
      value: |
        valueFrom:
          secretKeyRef: {name: $s3, key: accessKeyId}
    - name: AWS_SECRET_ACCESS_KEY
      value: |
        valueFrom:
          secretKeyRef: {name: $s3, key: secretAccessKey}
sysdbMigration:
  image: {repository: '$registry/sysdb-migration', tag: '$tag'}
  username: \$(PG_USERNAME)
  password: \$(PG_PASSWORD)
  netloc: $host
  port: $port
  dbName: $db
  sslmode: $sslmode
rustLogService:
  image: {repository: '$registry/rust-log-service', tag: '$tag'}
  otherEnvConfig: |
    - name: AWS_ACCESS_KEY_ID
      valueFrom:
        secretKeyRef: {name: $s3, key: accessKeyId}
    - name: AWS_SECRET_ACCESS_KEY
      valueFrom:
        secretKeyRef: {name: $s3, key: secretAccessKey}
    - name: AWS_REGION
      value: "$region"$endpoint_raw
queryService:
  image: {repository: '$registry/query-service', tag: '$tag'}
  env: &storage
    - name: RUST_BACKTRACE
      value: 'value: "1"'
    - name: AWS_ACCESS_KEY_ID
      value: |
        valueFrom:
          secretKeyRef: {name: $s3, key: accessKeyId}
    - name: AWS_SECRET_ACCESS_KEY
      value: |
        valueFrom:
          secretKeyRef: {name: $s3, key: secretAccessKey}
    - name: AWS_REGION
      value: 'value: "$region"'$endpoint_env
compactionService:
  image: {repository: '$registry/compactor-service', tag: '$tag'}
  env: *storage
garbageCollector:
  image: {repository: '$registry/garbage-collector-service', tag: '$tag'}
  env: *storage
workQueueService:
  image: {repository: '$registry/work-queue-service', tag: '$tag'}
  env: *storage
fnConsumer:
  image: {repository: '$registry/fn-consumer-service', tag: '$tag'}
  env: *storage
rustSysdbService:
  replicaCount: 0
mdacService:
  enabled: false
EOF
}

# A case: its parameters, as in tests/ChromaDotNet.Operator.Tests/Chart/<case>.chroma.yaml, and the optional values of the
# components in <case>.values.yaml (replicas, resources, placement).
render() { # case, then the parameters of values
  local case=$1 ns=$2 bucket=$4
  shift
  values "$@" >"$work/$case.values.yaml"
  configure "$configs/frontend.yaml" "$ns" "$bucket" 0 >"$work/$case.frontend.yaml"
  configure "$configs/worker.yaml" "$ns" "$bucket" 1 >"$work/$case.worker.yaml"
  local extra=()
  [ -f "$out/$case.values.yaml" ] && extra=(-f "$out/$case.values.yaml")
  local set_files=(--set-file "rustFrontendService.configuration=$work/$case.frontend.yaml")
  for component in rustLogService compactionService queryService garbageCollector workQueueService fnConsumer; do
    set_files+=(--set-file "$component.configuration=$work/$case.worker.yaml")
  done
  {
    echo "# helm template of distributed-chroma $chart_version for the case $case. Generated by build/chart.sh: do not edit."
    helm template chroma "$chart" -f "$chart/values.dev.yaml" -f "$work/$case.values.yaml" "${extra[@]}" "${set_files[@]}"
  } >"$out/$case.yaml"
  echo "$out/$case.yaml: $(grep -c '^kind:' "$out/$case.yaml") objects"
}

use ${versions[0]}
render default chroma docker.io/chromadb chroma-storage http://minio.chroma.svc.cluster.local:9000 us-east-1 \
  postgres 5432 sysdb disable chroma-postgres chroma-storage
render custom team-a registry.example.com/mirror vectors '' eu-west-1 \
  pg.databases.svc.cluster.local 6432 chroma_sysdb require team-a-pg team-a-s3
# The version before, from which the trials update: the parameters of default.
use ${versions[1]}
render previous chroma docker.io/chromadb chroma-storage http://minio.chroma.svc.cluster.local:9000 us-east-1 \
  postgres 5432 sysdb disable chroma-postgres chroma-storage
