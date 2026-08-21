# Deep MAUI Architecture Review

Last reviewed: 2026-07-12. Scope: the current `deep-client-maui` and
`deep-client-shared` worktrees, with Android and Windows as the active release
targets. iOS is intentionally deferred to a later release phase.

## Architecture

The client uses three explicit layers:

1. `Deep.Client.Shared` owns protocol, E2EE, authenticated onion routing,
   persistence, account lifecycle, inbox/outbox, groups, attachments, push
   subscription signatures, and call signaling.
2. `Deep.Client.Maui.Core` owns framework-light ViewModels, commands, and
   navigation contracts.
3. `Deep.Client.Maui` owns composition, pages, and native Android/Windows
   boundaries. Release composition rejects stub/direct messaging fallbacks.

Runtime creation is asynchronous and retryable through
`ClientRuntimeBootstrapper`. SQLCipher state, account-generation barriers,
durable inbox/outbox, idempotent activation queues, and cache-first UI reads keep
network work away from the first rendered frame.

## Review Results

| Area | Result | Evidence |
| --- | --- | --- |
| Release transport | Pass | Three pinned routers, authenticated route contacts, E2EE-only release runtime, embedded Reality bootstrap |
| Persistence | Pass | SQLCipher key in secure storage, async initialization, exact v13 schema validation with reset signaling, account-scoped purge, durable inbox/outbox tests |
| App lifecycle | Pass | Retryable startup, observed activation handlers, bounded background ingress, account-generation cancellation |
| Android | Pass | Signed v15 APK/AAB, physical cold-start smoke, FCM token path, embedded libXray |
| Windows build | Pass | Warning-free `win-x64` Release build with architecture-pinned Xray |
| Windows security | Pass | Fail-closed Windows Hello lock and screen-capture protection |
| Windows push | Code complete | Headless WNS raw activation, authenticated payload, 5,000-byte limit, safe channel rotation, durable unsubscribe retry |
| Windows shell integration | Pass | MSIX manifest includes WNS/toast COM activation and bounded Share Target; Downloads uses the system known folder |

## Security Invariants

- Release endpoints and TLS pins come only from embedded resources.
- Push provider payloads contain only `enc_payload` and `spns`; notification
  plaintext, account identifiers, channel URIs, and keys are not logged.
- WNS channel replacement closes the previous channel only after the new remote
  subscription is durably confirmed.
- Windows Hello never disables an existing lock after a busy, unavailable,
  cancelled, or exceptional verification result.
- Incoming shares are copied into private app storage with count, per-file,
  aggregate-size, queue-size, and filename bounds before acknowledgement.
- Account switching cancels stale work and prevents state written for one
  account generation from becoming visible to another.

## External Windows Release Inputs

The code and packaging path are complete, but Microsoft-controlled identity
inputs are intentionally not fabricated. A distributable Windows package needs:

- the permanent MSIX package identity and publisher subject;
- a trusted Windows code-signing certificate;
- the Entra tenant ID, application/client ID, and enterprise-application object ID;
- a server-side client secret;
- completed PFN-to-AppId mapping for background WNS activation.

Until those values are supplied, the client keeps bounded foreground fallback
sync and the production push server must leave `Push__WnsEnabled=false`.

## Deferred Scope

iOS signing, APNs provisioning, and iOS device acceptance are deferred by
product decision. They are not part of the current Android/Windows release gate.
