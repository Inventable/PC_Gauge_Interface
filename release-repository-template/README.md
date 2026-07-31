# Northstar Gauge Releases

Public, binary-only downloads for Northstar Gauge Interface and approved gauge
firmware. Source code lives in separate repositories.

## Download the Windows application

Open the latest GitHub Release and download
`Northstar-Gauge-Interface-Setup-<version>.exe`. The installer includes the
.NET runtime and does not require a separate framework installation.

## Firmware catalog

The desktop application reads `channels/stable.json`, selects the newest
stable entry matching the connected gauge device type, downloads the referenced
Offset-production HEX, verifies its SHA-256, and validates the image layout
before programming is enabled.

Published release files are immutable: correcting an artifact requires a new
version and a new directory. `SHA256SUMS.txt` records hashes for human/offline
verification.

## Repository layout

```text
README.md
channels/
  stable.json
schemas/
  release-manifest.schema.json
.github/
  workflows/
    finalize-release.yml
```

The first public repository should be named `Inventable/IT_Releases`
so the application default catalog URL works without configuration. Enable
GitHub release immutability and protect the `main` branch before routine use.

Installer and HEX binaries belong in GitHub Release assets, not Git history.
