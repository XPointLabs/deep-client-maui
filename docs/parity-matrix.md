# Deep MAUI Parity Matrix

Last updated: 2026-07-12.

This file tracks the MAUI client slice of the repository-level parity matrix in `../docs/parity-matrix.md`.
Statuses are intentionally conservative: a flow is `done` only when local runtime behavior and the relevant platform/UI gate are both proven.

## Launch-Critical Client Flows

| Flow | Status | Current evidence | Remaining gap |
|---|---|---|---|
| Create account | partial | `Deep.Client.Maui.ViewModels.Tests` covers onboarding/account creation; shared account tests pass. | Rendered UI/device evidence across Android/iOS/Windows. |
| Restore account | partial | Shared account recovery and MAUI ViewModel tests cover local restore behavior. | Network-backed profile/account linking parity evidence. |
| Show Session ID/profile state | partial | Shared avatar profile transport exists; MAUI release requires `DEEP_FILE_URL`. | Full rendered profile/settings acceptance and cross-platform visual evidence. |
| Start/open one-to-one chat | partial | Production live acceptance creates accounts, starts a 1:1 chat from Session ID, sends through the deployed storage service, and receives on the peer runtime. Android rendered navigation and self-chat were verified on a physical SM-G970F. | Rendered UI e2e across the full Android/iOS/Windows matrix. |
| Send/receive messages | partial | Production live acceptance passes 1:1 exchange; shared tests cover stable IDs, polling deduplication, and self-chat echo repair. The physical Android client displays delivery glyphs and the real three-hop transport route. | Multi-device rendered offline/restart acceptance across the platform matrix. |
| Stage/send attachments | partial | Shared live e2e uploads encrypted payloads through local `/file`, passes remote metadata through storage, and downloads/decrypts on the receiving side; MAUI ViewModel live acceptance exercises `PickAttachmentsAsync` with real file upload metadata. | Rendered chat-composer/device acceptance with progress/retry UX and cross-platform file picker evidence. |
| Create/open/manage groups | partial | Shared live e2e publishes group state and group messages through local `/storage`; MAUI ViewModel live acceptance creates a group, syncs it to another client, and receives a group message through the same backend. | Complete rendered member-role/destructive-action UX and cross-platform device/UI evidence. |
| Push register/unregister | partial | Android production configuration, native FCM token acquisition/refresh, foreground data notification handling, automatic signed backend subscription, and unregister are implemented. A signed Release APK initialized Firebase on a physical SM-G970F. Synthetic tokens are forbidden. | Credentialed backend FCM send and physical-device delivery evidence; APNs/WNS production credentials. |
| Calls start/respond/end | partial | Production live e2e proves signed encrypted offer/answer/bye and short-lived ICE credentials. MAUI packages a local WebRTC runtime with audio/video capture, DTLS-SRTP, STUN/TURN, mute, camera controls, push wake-up, and call navigation; Android and Windows builds pass. | Complete rendered two-device media acceptance and add dedicated full-screen incoming-call actions. |
| Sign out/wipe local state | partial | MAUI settings/auth navigation tests cover local sign-out behavior. | Rendered cross-platform acceptance and persistence wipe verification. |

## Latest Local Gates

Validated in this workspace on Windows 11 ARM64:

```powershell
$env:DEEP_STORAGE_URL = "http://127.0.0.1:18100"
$env:DEEP_FILE_URL = "http://127.0.0.1:18101"
$env:DEEP_PUSH_URL = "http://127.0.0.1:18102"
$env:DEEP_CALL_SIGNALING_BASE_URL = "http://127.0.0.1:18103"
dotnet test ..\deep-client-shared\Deep.Client.Shared.slnx --configuration Release
dotnet test tests\Deep.Client.Maui.ViewModels.Tests\Deep.Client.Maui.ViewModels.Tests.csproj --configuration Release
dotnet test tests\Deep.Client.Maui.SmokeTests\Deep.Client.Maui.SmokeTests.csproj --configuration Release
dotnet build src\Deep.Client.Maui\Deep.Client.Maui.csproj --configuration Release -f net10.0-windows10.0.19041.0 -p:RuntimeIdentifier=win-arm64 -nr:false
dotnet build src\Deep.Client.Maui\Deep.Client.Maui.csproj --configuration Release -f net10.0-android -nr:false -m:1
```

The signed Android Release APK was installed on a physical SM-G970F as
`network.xpoint.deep`: cold start completed in 1.5 seconds, the crash buffer
remained empty, Firebase initialized, and embedded Xray 26.3.27 started.

The native Windows ARM64 Release executable rendered the onboarding surface,
reached `RuntimeReady` in 1,949 ms, started the pinned ARM64 Xray process, and
closed without leaving a child process or crash entry. Both x64 and ARM64
Release builds pass with zero warnings. The generated MSIX manifest and signing
pipeline validate WNS, toast, and Share Target activation metadata.

## Release-Blocking Evidence Still Needed

- Rendered Windows UI automation for chat send/receive, settings, share ingress,
  Windows Hello, and sign-out after a trusted production MSIX can be installed.
- Android device-lab acceptance for notification background/resume and long-running persistence soak.
- Rendered attachment send acceptance from the chat composer with picker/progress/retry UX.
- Group management rendered acceptance for add/remove/member-role/destructive paths.
- Credentialed WNS canary evidence after Entra/PFN mapping; Android FCM production
  delivery is already operational.
- Two-device Windows call media acceptance.

iOS/APNs signing and device acceptance are deferred to a later product phase and
are not part of the current Android/Windows gate.
