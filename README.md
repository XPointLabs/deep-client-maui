# Deep Client MAUI

`deep-client-maui` is the production .NET MAUI client for Deep over XPoint
Network. Shared protocol, encrypted persistence, E2EE, onion routing, groups,
attachments, push subscriptions, and call signaling live in
`deep-client-shared`; this repository owns the MAUI UX and native platform
integration.

## Agent Specs

- Start with [`AGENTS.md`](AGENTS.md) before changing this repository.
- Use [`docs/SESSION_PORTING.md`](docs/SESSION_PORTING.md) when comparing, migrating, or porting behavior from Session Android/iOS/Desktop.
- Keep this repo focused on MAUI platform integration and UX; shared domain/runtime behavior belongs in `deep-client-shared`.

## Projects

- `src/Deep.Client.Maui.Core`: testable MVVM layer, route catalog, commands, attachment picker abstraction.
- `src/Deep.Client.Maui`: MAUI app targeting Android, iOS, Windows, and Mac Catalyst.
- `tests/Deep.Client.Maui.ViewModels.Tests`: onboarding, chat, groups, attachment/notification viewmodel tests.
- `tests/Deep.Client.Maui.SmokeTests`: shell route smoke tests.
- `tests/Deep.Client.Maui.DeviceTests`: Android/iOS target smoke tests for device-bound app services.
- `tests/Deep.Client.Maui.UiTests`: Windows UI automation smoke tests (Appium/WinAppDriver) that verify real rendered controls and onboarding->chats flow.

## Local Verification

Validated in this workspace:

```powershell
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifierOverride=win-x64
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-android
dotnet test tests/Deep.Client.Maui.ViewModels.Tests/Deep.Client.Maui.ViewModels.Tests.csproj
dotnet test tests/Deep.Client.Maui.SmokeTests/Deep.Client.Maui.SmokeTests.csproj

# Optional Windows UI automation (requires WinAppDriver and published MAUI exe)
$env:DEEP_UI_TESTS = "1"
$env:DEEP_MAUI_EXE = "C:\\Work\\Deep\\deep-client-maui\\src\\Deep.Client.Maui\\bin\\ARM64\\Release\\net10.0-windows10.0.19041.0\\win-arm64\\publish\\Deep.Client.Maui.exe"
# Optional: defaults to http://127.0.0.1:4723
# $env:WINAPPDRIVER_URL = "http://127.0.0.1:4723"
dotnet test tests/Deep.Client.Maui.UiTests/Deep.Client.Maui.UiTests.csproj
```

Implemented E2 platform contour coverage:

- APNS/WNS native token ingestion and provider-token persistence/unregister paths.
- Android uses the official Firebase Messaging binding, obtains a real FCM token, refreshes it, and registers it with the push subscription API. Synthetic provider tokens are never used.
- Background sync scheduling with retry backoff.
- Media transcode + attachment staging pipeline.
- Durable, bounded Share/notification activation ingestion for Android and Windows.

Platform caveats/workarounds are documented in `docs/ARCHITECTURE.md`.

Runtime transport behavior:

- Debug builds can use local stub mode for deterministic UI behavior.
- Non-Debug builds require exactly three unique authenticated XPoint onion routers and fail
  closed when production trust/configuration is missing.
- Routed composition requires exactly three lowercase pinned
  `<64-hex-routerId>|<absolute-url>` entries. Router bases reject
  userinfo/query/fragment and non-root paths; HTTP is accepted only for a
  literal loopback IP. Any simultaneous `DEEP_STORAGE_URL` is rejected.
- `MauiProgram` and Release tests use the same Core production-composition
  factory for the real route provider and routed session transport.
- `DEEP_STORAGE_URL` is Debug-only direct-storage diagnostics. It cannot satisfy
  routed release evidence and is never a fallback after router failure.
- `DEEP_TRANSPORT_BASE_URL` remains Debug-only for a custom diagnostic HTTP message API.

For the separate Debug direct-storage diagnostic lane only, set:

```powershell
$env:DEEP_STORAGE_URL = "http://127.0.0.1:18100"
$env:DEEP_STRICT_DIRECT_STORAGE = "1"
```

Set `DEEP_TRANSPORT_BASE_URL` only when using custom real HTTP transport endpoints, for example:

```powershell
$env:DEEP_TRANSPORT_BASE_URL = "http://127.0.0.1:18081"
```

Optional call signaling endpoint (used when calls are enabled):

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

Production Android and Windows builds do not use public HTTP(S) node URLs. They embed three
signed bootstrap anchors from `deep.bootstrap.json`, start `XTLS/libXray`, and
connect to each seed over VLESS Reality using the node origin IP. Session RPC is
available to the managed client only through three loopback listeners. The seed
then returns a dynamic three-hop route whose relay contacts are verified with
the nodes' Ed25519 identities.

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

## MAUI Parity Stages (Session Android/iOS/Desktop)

Reference upstream clients for behavior parity:

- `../source/session-android`
- `../source/session-ios`
- `../source/session-desktop`

Parity objective: launch-critical user flows and client runtime behavior are equivalent across Android/iOS/Desktop Session and Deep MAUI release profile.

### Stage 0 - Baseline Lock (done)

Scope:

- Freeze parity target list and acceptance matrix (`docs/parity-matrix.md`, launch-critical LC-01/LC-02/LC-05 + client-adjacent LC-10/PG-01/OP-01).
- Keep protocol/runtime baselines from completed tracks B1/B2/B3 and E2/E3/E4 as non-regression constraints.

Exit gate:

- All Stage 0 references are documented and linked from architecture docs.

### Stage 1 - Release Transport Parity (in progress)

Scope:

- Make real HTTP transport the default for release profiles in MAUI and shared runtime.
- Keep stub transport test-only/dev-only, with explicit guardrails to prevent release usage.
- Add CI assertion that release builds fail if stub transport is wired.

Exit gate:

- No launch-critical flow in release path depends on `StubSessionBackend`.
- 10 consecutive green runs for shared + MAUI critical suites under real transport profile.

### Stage 2 - Feature Surface Parity (client UX + behavior)

Scope:

- Close remaining group-management UX coverage (member-role screens, destructive action confirmations, localization paths).
- Close onboarding/recovery parity follow-ups (network-backed profile/account linking restore semantics where required by upstream behavior).
- Ensure attachment and notification behavior is equivalent for Android/iOS/Windows platform edges.

Exit gate:

- Acceptance scenarios for onboarding, restore, 1:1 chat, groups lifecycle, attachments, and push actions pass on MAUI.
- No open launch-critical client parity blockers in tracking board.

Current completion delta:

- Release/debug feature-flag invariants are covered by shared tests (`TransportRequired => !StubTransportAllowed`).
- MAUI chat flow now includes explicit incoming-call UX surface with accept/decline actions.

### Stage 3 - Realtime Calls Parity (signaling + media)

Scope:

- Replace in-memory signaling transport with secure network signaling integration.
- Integrate platform-native WebRTC media capture/render pipeline for Android/iOS/Desktop targets.
- Add reconnect and quality diagnostics parity checks for call setup, mid-call degradation, and teardown.

Exit gate:

- Call state and media lifecycle behavior match upstream Session expectations for supported scenarios.
- Device-level call smoke tests are green across target platforms.

Current completion delta:

- Signaling transport supports HTTP endpoint integration with dedicated tests.
- Chat call lifecycle now covers start, poll incoming ringing, accept, decline, and end paths in ViewModel tests.
- Chat page includes automatic 2-second call polling during active page lifetime.

### Stage 4 - Cross-Platform Acceptance and Soak

Scope:

- Expand MAUI UI/device/e2e suites to enforce parity-critical user journeys across Android, iOS, and Windows.
- Add long-running soak and restart-recovery checks for message sync, notification ingestion, and call session resilience.
- Persist artifacts for failures (logs, runtime snapshots, UI traces) for every nightly run.

Exit gate:

- Nightly parity acceptance suite reaches stable pass rate target (>= 95 percent rolling 14-day window).
- All launch-critical parity scenarios have deterministic test evidence.

### Stage 5 - GA Readiness and Cutover

Scope:

- Final parity review against Session Android/iOS/Desktop behavior matrix.
- Security/SRE/release gates are satisfied for MAUI production rollout.
- Canary rollout and rollback rehearsals complete with documented MTTR and runbooks.

Exit gate:

- MAUI client is approved as parity-equivalent for launch-critical scope.
- Cross-functional sign-off (client, protocol, security, SRE, release).

## Suggested Execution Order

1. Stage 1 (release transport parity)
2. Stage 2 (feature surface parity)
3. Stage 3 (realtime calls parity)
4. Stage 4 (cross-platform acceptance and soak)
5. Stage 5 (GA readiness and cutover)

This order minimizes risk on the critical path: transport and core client behavior first, realtime/media second, then reliability and release discipline.
