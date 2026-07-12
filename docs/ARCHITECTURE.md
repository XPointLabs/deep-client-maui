# Deep MAUI Architecture

Deep is a private messenger running over XPoint Network. The MAUI repository is
the native application boundary; portable protocol and state behavior live in
the sibling `deep-client-shared` repository.

## Layers

### Shared runtime

`Deep.Client.Shared` owns:

- Session-compatible identity and recovery phrase derivation;
- end-to-end encrypted envelopes and replay protection;
- three-hop authenticated XPoint onion routing;
- SQLCipher repositories, migrations, durable inbox/outbox, and account purge;
- one-to-one and group conversation services;
- encrypted attachment and avatar transports;
- signed push subscribe/unsubscribe contracts;
- encrypted realtime call signaling and short-lived ICE configuration.

### MAUI Core

`Deep.Client.Maui.Core` contains ViewModels, commands, navigation contracts, and
UI caches. It has no native Android or Windows API dependency and is covered by
fast unit tests.

### Native MAUI boundary

`Deep.Client.Maui` contains pages, dependency injection, lifecycle coordination,
secure storage adapters, media/picker integration, push callbacks, app lock,
Reality sidecars, and OS activation ingress.

Release builds require real transports, three pinned Reality bootstrap nodes,
TLS public-key pins, encrypted local persistence, and E2EE. Debug-only stubs and
loose environment configuration cannot be selected by a Release process.

## Startup

1. `MauiProgram` validates immutable embedded settings and composes narrow
   platform services.
2. `ClientRuntimeBootstrapper` initializes encrypted persistence and migrations
   off the UI thread. A transient failure can be retried from the startup view.
3. `AuthNavigationState` selects onboarding or conversations from local state.
4. Conversation and message pages render cached snapshots first; sync runs in
   a cancellable background path.

## Messaging

Outgoing messages are persisted to a durable outbox before dispatch. The client
encrypts content for the recipient, selects a signed three-node route, and sends
through local Reality listeners. Inbox synchronization verifies authenticated
storage responses, decrypts envelopes, rejects replay, persists domain state,
and acknowledges only after durable processing.

Groups use the same transport and persistence guarantees for state and messages.
Attachments are encrypted before upload; ordinary images are compressed for
inline media while document mode preserves the source file.

## Push

Android uses FCM and Windows uses WNS. Both register a provider token through the
same signed v2 subscription protocol. Provider payloads are encrypted with
AES-256-GCM and contain no message plaintext.

Windows requests a channel on foreground activation, keeps the previous channel
valid during server registration, and closes it only after durable confirmation.
Raw background activation creates a headless service graph and does not create a
MAUI window or invoke Windows Hello. Invalid, expired, replayed, or undecryptable
payloads are dropped.

## Platform Work

Android uses JobService-backed retry/catch-up, Firebase callbacks, biometric or
device-credential lock, MediaStore downloads, and a bounded share-ingress service.

Windows uses architecture-specific Xray, Windows Hello, WNS raw background
activation, AppNotificationManager, MSIX Share Target activation, system
Downloads, and foreground/network-restoration maintenance. Production packaging
is documented in `WINDOWS_RELEASE.md`.

iOS remains in the source tree but is outside the current release phase.
