# Deep Client MAUI

`deep-client-maui` is the production .NET MAUI client for Deep over XPoint
Network. Shared protocol, encrypted persistence, E2EE, Deep-native privacy routing, groups,
attachments, push subscriptions, and call signaling live in
`deep-client-shared`; this repository owns the MAUI UX and native platform
integration.

## Agent Specs

- Start with [`AGENTS.md`](AGENTS.md) before changing this repository.
- Keep this repo focused on MAUI platform integration and UX; shared domain/runtime behavior belongs in `deep-client-shared`.

## Projects

- `src/Deep.Client.Maui.Core`: testable MVVM layer, route catalog, commands, attachment picker abstraction.
- `src/Deep.Client.Maui`: MAUI app targeting Android, iOS, Windows, and Mac Catalyst.
- `tests/Deep.Client.Maui.Clean.Tests`: current DID2 account and clean-break UI contracts.
- `tests/Deep.Client.Maui.SmokeTests`: shell route smoke tests.
- `tests/Deep.Client.Maui.DeviceTests`: Android/iOS target smoke tests for DID2 account creation and persistence plus device-bound app services.
- `tests/Deep.Client.Maui.UiTests`: retired SessionId-era physical harness, outside the clean solution; it cannot approve a DID2 release.

## Local Verification

Validated in this workspace:

```powershell
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-android
dotnet test tests/Deep.Client.Maui.Clean.Tests/Deep.Client.Maui.Clean.Tests.csproj
dotnet test tests/Deep.Client.Maui.SmokeTests/Deep.Client.Maui.SmokeTests.csproj
```

The retired SessionId UI automation must not be run as release evidence.
A DID2 Android↔Windows physical messaging lane remains a release gate.

Implemented E2 platform contour coverage:

- APNS/WNS native token ingestion and provider-token persistence/unregister paths.
- Android uses the official Firebase Messaging binding, obtains a real FCM token, refreshes it, and registers it with the push subscription API. Synthetic provider tokens are never used.
- Background sync scheduling with retry backoff.
- Media transcode + attachment staging pipeline.
- Durable, bounded Share/notification activation ingestion for Android and Windows.

Platform caveats/workarounds are documented in `docs/ARCHITECTURE.md`.

Runtime transport behavior:

- Physical Debug MAU2 loads an exact app-private `privacy-routes.v2.json` bound
  by both `activation.v1.json` and the Mr. X-signed mailbox policy.
- Primary and fallback routes each contain exactly three independent X25519
  hops. All six router identities and keys, and both HTTPS ingress origins, must
  be distinct. The Survival primary route is `xnode3 -> xnode4 -> xnode1`, where
  `xnode1` is the sole authoritative mailbox coordinator. The fallback route is
  `xnode5 -> xnode6 -> xnode2`, where `xnode2` is a forwarding-only privacy exit:
  after unwrapping the privacy frame it forwards the unchanged canonical MAU2 to
  `xnode1`. Consequently, authenticated MQR3 evidence identifies `xnode1`; its
  coordinator is not the terminal fallback hop (`xnode2`).
- Canonical MAU2 is sealed through `PrivacyRoutedMailboxBinaryIngress`.
  Fallback is permitted only after a definite pre-forward rejection. There is
  no direct MAU2, Session RPC, routed-storage, or raw HTTP message fallback.
- Non-Debug builds remain fail-closed until production mailbox credentials and
  an independently approved production privacy-route artifact are available.
- `XNODE_URLS` and the Reality runtime support node diagnostics and adjacent
  transport work only; they are not the mailbox message path.

The retired external outbox-worker prototype is not part of the DID2 release.
Durable direct-message outbox and authenticated ACK remain release gates in the
account-owned protocol store, with no legacy worker fallback.

Legacy pre-cutover call signaling endpoint (UAT evidence only; deleted by the
clean-break release and never a target fallback):

```powershell
$env:DEEP_CALL_SIGNALING_BASE_URL = "http://127.0.0.1:18103"
```

Optional backend push subscription endpoint (used to mirror supported device registrations into the Session-style push service):

```powershell
$env:DEEP_PUSH_URL = "http://127.0.0.1:18102"
```

`DEEP_FILE_URL` is also used by the attachment picker to upload encrypted attachment payloads before message send.

Release configuration is immutable at runtime. Release builds read only
embedded `deep.release.env`, `deep.bootstrap.json`, and the generated embedded
Windows environment resource. OS environment variables and loose config files
are accepted only in Debug builds.

Production Android and Windows keep the signed Reality bootstrap for node
diagnostics and adjacent transport work. It does not send mailbox messages.
Production MAU2 remains unavailable until its production credential and
privacy-route acquisition seam is provisioned; startup fails closed instead of
selecting Session RPC or a direct HTTP route.

Windows releases are signed MSIX packages with architecture-specific Xray,
Windows Hello app lock, WNS background activation, native notifications,
Windows Share Target, and known-folder downloads. See
[`docs/WINDOWS_RELEASE.md`](docs/WINDOWS_RELEASE.md).

The checked-in Android AAR is reproducible with:

```powershell
.\eng\build-libxray-android.ps1 `
  -GoRoot C:\path\to\go1.26.2 `
  -AndroidSdkRoot C:\path\to\android-sdk `
  -JavaHome C:\path\to\jdk-21
```

The build pins the upstream tag, commit, and `gomobile` version. It rejects an
AAR unless both native ABIs contain valid ELF section metadata, a `.dynamic`
section, and 16 KB-aligned load segments.
