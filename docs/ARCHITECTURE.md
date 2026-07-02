# Deep MAUI Architecture

The MAUI app is a shell over `deep-client-shared`.

## Source Mapping

- Session Desktop: conversation registry, Redux conversation state, staged attachments, notification formatting, message queue, config sync jobs.
- Session Android: config-to-database sync, message sender/receiver, attachment pointer conversion, notification processor, database migration surface.
- Session iOS: onboarding sync state, push token sync job, notification action handling, GRDB migrations, group creation/editing flows.

## Layers

`Deep.Client.Maui.Core` is UI-framework-light. ViewModels depend on the shared runtime and expose observable state:

- `OnboardingViewModel`: registration/login/session id flow.
- `ConversationsViewModel`: conversation list state.
- `ChatViewModel`: 1:1 chat send/receive, staged attachment metadata.
- `GroupsViewModel`: Groups v2 creation/list refresh backed by shared group-state sync.
- `NotificationRegistrationViewModel`: push registration boundary.
- `AttachmentPickerViewModel`: attachment picker boundary.

`Deep.Client.Maui` contains MAUI pages, DI, and platform implementations. Platform boundaries are explicit for:

- push notifications,
- media encode/decode,
- permissions,
- background tasks,
- share extension equivalents,
- realtime call lifecycle bridge to shared signaling service.

Only behavior backed by a concrete runtime service is exposed in release builds. Feature flags remain for controlled rollout and tests, not as UI placeholders.

## Runtime Flow

1. App starts through `MauiProgram.CreateMauiApp`.
2. DI creates a shared `ClientRuntime` with SQLite-backed persistent store and HTTP session transport.
3. Onboarding registers or restores a Session ID through shared account services.
4. Chat viewmodels send messages through shared message services and platform transport integration.
5. Group creation creates a Groups v2 domain scaffold, persists the corresponding conversation, and publishes group state through shared storage sync.

## UX Completeness (E4)

- `GroupsViewModel` now exposes admin/member lifecycle operations backed by shared runtime: rename group, promote/demote role, mark pending removal, remove member, leave group, destroy group, remote group-state refresh, and list state.
- `GroupChatViewModel.Refresh` now pulls remote group state and group messages before rendering local message history, so MAUI Core acceptance covers group creation and group message delivery through the local storage backend.
- `OnboardingViewModel` recovery edge-cases are validated through viewmodel tests (malformed Session ID, login command guards, restored-account persistence expectations).
- Recovery/login flow now requires recovery phrase input (not raw Session ID). Session ID is deterministically derived from phrase key material and onboarding exposes the generated phrase after account creation.
- Restore flow can complete without manual display-name entry when network profile lookup succeeds; otherwise onboarding prompts fallback manual name.
- Cross-runtime parity tests validate equivalent group scaffolding behavior for MAUI target simulations.

## Platform Integrations (E2)

- Push lifecycle service implements APNS/FCM/WNS provider mapping, real-token persistence, unregister flow, and native token bridge ingestion. It never manufactures provider tokens.
- Background sync service schedules delayed runs with retry backoff and bridge events for sync workers.
- Media/attachment pipeline transcodes selected files through `IMediaCodecService` before staging metadata.
- Share and notification actions parity is handled via Android/iOS/Windows activation ingestion into in-app bridges.

## Realtime Calls (E3)

- `CallSessionCoordinator` is the single signaling inbox consumer for active WebRTC calls and incoming offers. The shared HTTP transport encrypts payloads to the recipient, signs them with the active account, and fetches signed ephemeral ICE credentials.
- `CallPage` hosts only packaged HTML/JavaScript through `HybridWebView`; no remote page or script can access call media. Browser WebRTC provides DTLS-SRTP audio/video, while MAUI owns permissions, navigation, status, and controls.
- Current implementation provides deterministic signaling/state/reconnect behavior for runtime and tests while native media engines are integrated.

## Caveats

- Android build currently emits CA1422 warning for legacy `GetParcelableExtra` usage compatibility path; migration to typed overload can be done when min SDK policy is raised.
- Android FCM uses the external `google-services.json` for package `network.xpoint.deep`, `FirebaseMessagingService` for token refresh/data messages, and automatic signed subscription after account load. Production builds fail when the Firebase configuration is missing.
- iOS/MacCatalyst token registration requires APNS entitlements and provisioning profiles on physical device/testflight builds.
- Windows WNS provider path uses channel URI and requires packaged app identity for production push registration.
- E3 currently uses in-memory signaling transport and compatibility SDP/ICE payload path; platform-native WebRTC media capture/render remains a parity follow-up.
- Group management still needs dedicated rendered member-role management screens and localized destructive-action UX flows, but shared/MAUI Core now have live local storage evidence for group state and group-message delivery.
- Recovery phrase flow is crypto-backed (PBKDF2 seed + Ed25519 public key derivation) but not yet full upstream Session account-linking parity (network-backed restore steps remain follow-up).
