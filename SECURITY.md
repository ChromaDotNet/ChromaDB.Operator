# Security

## Supported versions

Security fixes go into the latest release of the operator: the image `ghcr.io/chromadotnet/chroma-operator` and the manifest `chroma-operator.yaml` of the GitHub release. Earlier versions do not get fixes: update to the latest one.

## Reporting a vulnerability

Please do not report a vulnerability in a public issue, discussion or pull request.

Report it privately on GitHub instead: [Report a vulnerability](https://github.com/ChromaDotNet/ChromaDB.Operator/security/advisories/new), in the **Security** tab of this repository. Only the maintainers see the report.

Include the version of the operator, the version of Kubernetes, the `Chroma` resource that shows the problem (without credentials), and the steps to reproduce it.

## What happens next

- We acknowledge the report within 7 days.
- We confirm or rule out the vulnerability within 14 days, and keep you informed of the progress.
- A confirmed vulnerability is fixed in a new release. A GitHub security advisory follows the release, with credit to you unless you prefer otherwise.
- Please keep the details private until the advisory is published, and for at most 90 days from the report.

## How the releases are published

- Only the CI publishes, from the version tags of this repository: the image for amd64 and arm64, with its build provenance and its Sigstore signature, and the manifest, attached to the GitHub release.
- The workflows pin their actions to commit hashes and the Dockerfile its base images to digests; Dependabot proposes their updates.
- The organization requires two-factor authentication.

The security considerations of running the operator, such as the permissions it needs, are in the [README](README.md#security-considerations).
