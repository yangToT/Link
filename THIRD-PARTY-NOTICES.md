# Third-party components

Link's own source is licensed under Apache-2.0. Each independent bundled component retains its own license; the complete distribution is not exclusively Apache-2.0.

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| NetBird client | 0.79.0 | BSD-3-Clause, with directory-specific exceptions documented upstream | https://github.com/netbirdio/netbird/tree/v0.79.0 |
| NetBird combined server | 0.79.0 | AGPL-3.0 | https://github.com/netbirdio/netbird/tree/v0.79.0/combined |
| Wintun Windows driver | 0.14.1 | Wintun prebuilt binaries license | https://www.wintun.net/ |

Link calls unmodified NetBird executables as separate processes and communicates through their documented protocols. No upstream code is copied into Link's executables. Source and license files for the pinned NetBird version are available in the upstream repository. Linux release distributions containing the AGPL server must include its corresponding source archive alongside the binaries. `deploy/fetch_netbird.py` records the official image manifest and binary digests.

Copies of the upstream license texts are in `third_party/`. Windows packages preserve the Wintun binary license. Link does not imply endorsement by NetBird or WireGuard.

Go's runtime and standard library are covered by the Go BSD license: https://go.dev/LICENSE. The Windows interface uses Windows' installed .NET Framework and does not redistribute the .NET runtime.

## Optional development integration

The experimental layer-2 feature invokes separately installed SoftEther VPN components through their documented CLI and JSON-RPC APIs. Preparation scripts target the official Stable Edition 4.44 Build 9807, tag `v4.44-9807-rtm` (Apache-2.0 for SoftEther's own code; bundled dependencies retain their licenses). This source change does not bundle SoftEther executables, drivers, or upstream source. Before redistributing those components, retain their license, third-party notices and any required source. Windows payloads are validated against SoftEther Corporation's Authenticode signature. Upstream: https://github.com/SoftEtherVPN/SoftEtherVPN_Stable/releases/tag/v4.44-9807-rtm .
