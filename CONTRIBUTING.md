# Contributing

Issues and pull requests are welcome. To report a vulnerability, follow [SECURITY.md](SECURITY.md) instead.

## Build and test

[Development](README.md#development), in the README, has the commands: the unit tests, the manifest and the chart of distributed
Chroma that the tests use, and the end-to-end tests on a k3d cluster.

## Pull requests

- A fix comes with a test that fails without it.
- New functionality comes with tests in the automated test suite, which the CI runs on every pull request: the unit tests, and the
  end-to-end tests on a k3d cluster.
- `deploy/chroma-operator.yaml` is generated: change the code or `deploy/kustomization.yaml`, then run `bash build/manifest.sh`.
  The CI fails when the manifest differs from the one the code generates.
- The CI must pass on every job, and a maintainer approves the change, before it is merged.
- The maintainers sign their commits.

Only the version tags publish a release: the image and the manifest.
