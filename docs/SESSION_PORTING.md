# Session Porting Spec - Client MAUI

Last updated: 2026-06-10.

## Scope

This document defines how agents port or compare Session Android/iOS/Desktop client behavior into the Deep MAUI client. The goal is launch-critical behavioral parity, not line-by-line UI reproduction.

Primary local references:

- `../source/session-android`
- `../source/session-ios`
- `../source/session-desktop`
- shared runtime: `../deep-client-shared`

The strict I01B release-evidence workflow is bound to shared runtime commit
`2e55f78c68699520e49ec0c8d5ddebd4b9817584`. Local evidence must use that exact
shared revision so routed DTO/version-confusion checks cannot drift from CI.

## Porting Principle

Extract the product contract first, then implement it in MAUI-native MVVM. Do not copy Android activity/Compose structure into MAUI if it creates a weaker cross-platform client. A port is valid when the Deep user journey and failure semantics match Session for the accepted scope.

## Behavior Map

Session reference areas and Deep targets:

- Home/inbox: `session-android/.../home/HomeActivity.kt`, `HomeViewModel.kt`, start conversation sheets -> `ConversationsPage`, `ConversationsViewModel`.
- New message: `home/startconversation/newmessage/*` -> Session ID composer and immediate chat route.
- Conversation input bar: `conversation/v2/input_bar/InputBar.kt` and v3 compose screens -> `ChatPage`, `ChatViewModel`, attachment staging, send/sync controls.
- Conversation settings/profile/search: `conversation/v3/settings/*` -> current info/profile actions and future conversation settings pages.
- Groups: `home/startconversation/group/*`, `groups/*`, `conversation/v3/settings/*` -> `GroupsPage`, `GroupChatPage`, `GroupsViewModel`, `GroupChatViewModel`.
- Notifications/push: Session push registration and notification actions -> `MauiPushNotificationService`, `NotificationRegistrationViewModel`, shared push transports.
- Calls: Session call signaling behavior -> authenticated `CallSessionCoordinator`; WebRTC audio/video media -> packaged `HybridWebView` runtime on `CallPage`; NAT traversal -> signed, short-lived ICE credentials.

## Required Porting Steps

For every Session-derived behavior:

1. Identify upstream source files and the user-visible contract.
2. Write a short behavior note in the issue/commit or relevant doc.
3. Add or update ViewModel tests before relying on manual UI checks.
4. Add MAUI UI/device coverage when the behavior depends on rendered controls, navigation, file pickers, notifications, or platform lifecycle.
5. Document intentional deviations in this file.

## Current Accepted Deviations

- MAUI uses a unified cross-platform layout instead of Android-specific activities/fragments/bottom sheets.
- Debug builds may use deterministic stub transport for local UI work; release builds must require real transport configuration.
- Incoming call presentation currently uses a MAUI system prompt instead of Session's dedicated full-screen ringing activity. Audio/video media, mute, camera enablement, camera switching, hangup, encrypted signaling, push wake-up, STUN, and TURN relay are implemented.
- Desktop-style split inbox/detail layout is allowed on wide screens, while mobile should route directly into the chat.
- Attachment upload metadata can be staged through shared attachment models before final backend storage cutover is complete.

## Non-Negotiable Parity

These must not regress:

- Start chat by Session ID opens an actual chat route, not only a list item.
- Sending messages and attachments updates local chat state and shared runtime state.
- Restore/create flows must end in authenticated navigation and expose the active Session ID.
- Group create/open/send/member management flows must preserve shared group rules.
- Sign-out must clear authenticated navigation state and honor configured local wipe behavior.
- Non-Debug startup must not silently replace real transport with stub transport.
- Non-Debug routed startup requires exactly three unique lowercase pinned router
  identities and URLs, rejects `DEEP_STORAGE_URL`, and has no direct-storage
  fallback after router failure.

## UX Porting Rules

- Prefer MAUI-native controls and stable `AutomationId`s.
- Keep buttons close to where Session users expect them: profile/settings in the top area, new chat in inbox, attachment/send in the composer, call controls in chat header/incoming-call banner.
- Every visible action must be command-backed or lifecycle-backed.
- Use hidden/disabled states for unavailable features; do not leave inert buttons.
- When Android uses a bottom sheet, MAUI may use a page, modal, action sheet, or split pane if the workflow remains efficient.

## Evidence Checklist

Each parity slice should leave:

- upstream files consulted,
- Deep files changed,
- tests run with pass/fail results,
- screenshots or UI automation evidence for rendered changes when possible,
- explicit note for unresolved parity debt.

Strict Windows, Android, and live-infrastructure lane semantics are documented in
`docs/STRICT_E2E_LANES.md`. The current Windows UIA3 slice proves only that the real
Welcome UI renders and is interactive; it intentionally does not claim account,
message, group, or cross-device E2E parity.

## Future Porting Backlog

- Full conversation settings page: search, media/attachments view, mute/disappearing messages, destructive actions.
- Message requests and approval/rejection flow.
- Rich message rendering: quotes, link previews, media/document/audio variants, reactions, deleted/control messages.
- Contacts and global search beyond local conversation filtering.
- Join community/open group URL handling.
- Dedicated full-screen incoming-call notification actions on each operating system.

## P02B offline update behavior

The P02B slice adds a fail-closed Settings entry and a portable verification
ViewModel. It intentionally does not reproduce an app-store updater or add a
silent installer. No trusted production root is bundled, so the visible release
state is “not configured”; tests supply public insecure keys explicitly.
Android package identity and signer extraction stays behind the native platform
adapter, while Apple platforms show their exact distribution limitation.

## P02D safe installer handoff boundary

P02D replaces the deleted verification snapshot with a bounded, app-private,
one-time handoff lifecycle. A successful verification returns only an opaque
cryptographically random handle, never the source or private file path. The
service creates a fixed, ownership-marked child below the supplied app-private
root and only removes marker-validated `pkg-*` children; unrelated siblings and
unmarked directories are never deleted. The active snapshot is limited to one
APK, at most 1 GiB, and at most 15 minutes.

Immediately before a platform request, the core reopens the preserved snapshot
and revalidates its exact length and SHA-256, then repeats Android package ID,
version, and signer-certificate inspection through the platform verifier. The
handle is consumed in a short non-cancelable critical section, so cancellation
cannot leave replayable state, and cancellation is then honored during signer
and adapter work. The ViewModel converts cancellation into a controlled blocked
state and requires a fresh verification before retry.

The platform boundary receives an already-open read-only stream while the core
requests read-only sharing and rehashes that same open handle after the adapter
returns. It never receives a filesystem path. Before returning success, an adapter must synchronously copy or take
durable OS ownership of the bytes and return an exact length/SHA-256 receipt;
missing or mismatched receipts fail closed. The stream is disposed immediately
after the adapter returns, so delayed reads are not part of the contract.

Deletion is attempted after success, rejection, tamper, expiry, replacement,
and cancellation. Filesystem semantics do not permit an absolute secure-erasure
claim: a failed deletion is reported as `CleanupPending`, retained in the
service retry set, retried before later preservation, and retried from
ownership-marked entries at startup. Startup fails closed if owned cleanup still
cannot complete. This is cleanup with retry, not a guarantee that storage blocks
have been physically erased. For cancellation the service deliberately
propagates `OperationCanceledException`, so there is no result object on which a
`CleanupPending` flag could be returned; any failed cancellation cleanup is
still queued internally and retried by the same later-operation/startup rules.

No Android installer adapter, FileProvider authority, unknown-sources
permission, or production DI registration is added in this slice. The future
Android adapter must use a content URI with a narrowly scoped, temporary,
read-only grant and must be reviewed with the exact signed release input.
Windows is explicitly unsupported until it has its own independently verified
installer contract; an Android APK is never forwarded to Windows packaging.

Session Android references consulted for the platform boundary were its
`AndroidManifest.xml` FileProvider declaration and `FileProviderUtil.java`.
They demonstrate general private-file sharing, but not the signed, one-time
update contract required here, so their broad provider setup was not copied.

## P12A nearby policy shell

P12A adds only a deterministic, fail-closed policy coordinator in
`Deep.Client.Maui.Core` and explicit disabled Android/unsupported Windows radio
shells. It does not register either adapter, request or declare BLE/Wi-Fi
permissions, call platform radio APIs, run a foreground service, exchange a
payload, or claim that nearby messaging works. Every process start remains
`Off`; saved settings intent cannot restore an active mode or deadline.

The first safe slice deliberately leaves the existing settings ViewModel/page,
`MauiProgram.cs`, and platform manifests unchanged. A later reviewed UX slice
must add honest limitation text, approved emergency-duration controls,
permission denial/retry/open-settings guidance, and battery/thermal stop
reasons before a nearby control becomes visible. Production radio registration
remains blocked on accepted P03D activation, two named physical Android
devices, measured energy evidence, and Android permission/privacy review.
