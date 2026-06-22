# Deep MAUI Parity Matrix

Last updated: 2026-06-10.

This file tracks the MAUI client slice of the repository-level parity matrix in `../docs/parity-matrix.md`.
Statuses are intentionally conservative: a flow is `done` only when local runtime behavior and the relevant platform/UI gate are both proven.

## Launch-Critical Client Flows

| Flow | Status | Current evidence | Remaining gap |
|---|---|---|---|
| Create account | partial | `Deep.Client.Maui.ViewModels.Tests` covers onboarding/account creation; shared account tests pass. | Rendered UI/device evidence across Android/iOS/Windows. |
| Restore account | partial | Shared account recovery and MAUI ViewModel tests cover local restore behavior. | Network-backed profile/account linking parity evidence. |
| Show Session ID/profile state | partial | Shared avatar profile transport exists; MAUI release requires `DEEP_FILE_URL`. | Full rendered profile/settings acceptance and cross-platform visual evidence. |
| Start/open one-to-one chat | partial | MAUI ViewModel live acceptance creates accounts, starts a 1:1 chat from Session ID, sends through local storage, and receives on the peer runtime. | Rendered UI e2e from onboarding to chat across platform matrix. |
| Send/receive messages | partial | `DEEP_STORAGE_URL=http://127.0.0.1:18100 dotnet test Deep.Client.Shared.slnx --configuration Release` passes live local message round-trip; MAUI ViewModel live acceptance covers the client path. | Multi-device/client polling and offline/restart acceptance from the rendered client. |
| Stage/send attachments | partial | Shared live e2e uploads encrypted payloads through local `/file`, passes remote metadata through storage, and downloads/decrypts on the receiving side; MAUI ViewModel live acceptance exercises `PickAttachmentsAsync` with real file upload metadata. | Rendered chat-composer/device acceptance with progress/retry UX and cross-platform file picker evidence. |
| Create/open/manage groups | partial | Shared live e2e publishes group state and group messages through local `/storage`; MAUI ViewModel live acceptance creates a group, syncs it to another client, and receives a group message through the same backend. | Complete rendered member-role/destructive-action UX and cross-platform device/UI evidence. |
| Push register/unregister | partial | `DEEP_PUSH_URL=http://127.0.0.1:18102 dotnet test Deep.Client.Shared.slnx --configuration Release` passes live local subscribe/unsubscribe; MAUI ViewModel live acceptance registers a client token through the real local push service. | Real APNs/FCM/Huawei credentialed canary and device notification delivery evidence. |
| Calls start/respond/end | partial | Shared live e2e runs offer/answer/bye through local `/api/calls`; MAUI ViewModel live acceptance starts, receives, accepts, connects, and ends a call through the same endpoint. | No native WebRTC media stack yet; release call media acceptance missing. |
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
dotnet build src\Deep.Client.Maui\Deep.Client.Maui.csproj --configuration Release -f net10.0-windows10.0.19041.0 -nr:false
dotnet build src\Deep.Client.Maui\Deep.Client.Maui.csproj --configuration Release -f net10.0-android -nr:false -m:1
```

## Release-Blocking Evidence Still Needed

- Rendered Windows UI automation for onboarding -> chat -> send/receive -> settings/sign-out.
- Android and iOS device-lab acceptance with real app lifecycle, notification permission, background/resume, and local persistence.
- Rendered attachment send acceptance from the chat composer with picker/progress/retry UX.
- Group management rendered acceptance for add/remove/member-role/destructive paths.
- Credentialed push provider canary evidence for APNs/FCM/Huawei.
- Native call media/WebRTC acceptance once the platform media stack is implemented.
