# Deep MAUI Architecture

Deep is a private messenger running over XPoint Network. The MAUI repository is
the native application boundary; portable protocol and state behavior live in
the sibling `deep-client-shared` repository.

## Layers

### Shared runtime

`Deep.Client.Shared` owns:

- the currently active legacy 13-word recovery implementation, which is
  disposable pre-production evidence and must be replaced by 24-word
  `DeepRecoveryV1` plus device-scoped keys before a public release;
- end-to-end encrypted envelopes and replay protection;
- three-hop binary Deep-native privacy routing for canonical MAU2;
- SQLCipher repositories with exact v16 baseline attestation, durable
  inbox/outbox, and account purge;
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

Fresh account creation accepts only a profile name and commits in one action.
The generated 24-word Deep Recovery Phrase is atomically retained with the
account in platform secure storage; onboarding never requires display, copy or
confirmation. Settings is the only post-activation reveal surface and provides
explicit copy, hide and irreversible device-local deletion actions. An absent
secure phrase is a normal state: the account and device keys remain usable, and
the phrase is never populated from SQLite or another wrapped store.

The SQLCipher key uses a compile-time lane namespace. Ordinary packages retain
the existing `client-state.sqlcipher-key.v1` SecureStorage slot, while
`DeepPhysicalE2E=true` packages use the distinct stable
`client-state.sqlcipher-key.physical-e2e.v1` slot. A confirmed local-state reset
removes and recreates only the active lane slot; it never deletes the shared
SecureStorage backing file, probes the other lane, or falls back to another key.

Reality sidecars are exposed as one application-scoped
`IRealityTransportRuntime`; `App` is the single idempotent shutdown owner.
Endpoint catalog construction is synchronous and does not start native
networking. Startup and foreground recovery have bounded waits. Every routed
request verifies the requested local listener. Android tracks foreground state
explicitly: a background request may use an already-attested listener once, but
cannot start, restart, or poll the sidecar. Pausing the Activity cancels the
current lifecycle generation, including an in-flight start/restart or listener
poll; a non-cooperative native call is rechecked and any late success is stopped.
Network callbacks only invalidate readiness. An explicit `XNODE_URLS` catalog
selects configured mode and is authoritative: no embedded sidecar is constructed
or started. Without that catalog, embedded mode publishes only its runtime-
attested loopback catalog. Windows defers routed composition until bounded port selection,
sidecar startup, and exact listener-owner PID attestation succeed. Rejected
candidates are cleaned up and reselected before the endpoint catalog is
published. After publication, restart retries only those exact ports and fails
closed if they cannot be reclaimed; silently republishing different ports would
leave existing router clients bound to stale endpoints. Shutdown initiates
sidecar stop concurrently with a bounded wait for
any native startup attempt and deletes generated configuration. Native startup
errors are reduced to a static diagnostic and never persist endpoint or
credential material. No failure path enables a direct or unpinned fallback.

Release builds require real transports, at least three unique signed access
seeds, encrypted local persistence, and the new ratcheted Deep E2EE generation.
More access bridges may be learned from signed rotating catalogs. The Reality bootstrap
does not register a Session message transport. Today mailbox delivery uses the
separately provisioned Deep-native privacy routes, but its entry connection is
still direct HTTPS. The Reality runtime is not the
`IPrivacyManagedIngressTransport` used by MAU2, so anti-blocking message
delivery remains a pre-release integration blocker.

## Startup

1. `MauiProgram` validates immutable embedded settings and composes narrow
   platform services.
2. `ClientRuntimeBootstrapper` initializes encrypted persistence off the UI
   thread without requiring network readiness. Fresh state receives the current
   clean-break baseline; existing state is
   exactly attested and incompatible state raises an actionable reset-required
   error. Account/recovery creation is local and remains available offline.
   `DeepAccountRuntimeOwner` opens the account database first and lazily owns the
   account/network-scoped SQLCipher ContactV1 operation journal, AccountDirectory
   LKG, XPoint LKG, and protected entry-guard stores only when a consumer asks
   for them. Those stores use distinct generation-scoped SecureStorage key
   slots; reset destroys every database family and its key together with the
   local account generation.
   A new permanent Deep ID is always written to the local pending-contact queue
   before ContactResolve prerequisites are inspected. The lazy ContactResolve
   accessor can only compose `HttpContactResolveDirectoryArtifactSource` with
   `ProductionContactResolvePathAuthoritySource`, a privacy-routed resolver
   transport, and a trusted response verifier. It accepts no raw ADP1/DTT1 or
   caller-authored placement. Missing genesis pin, protected monotonic clock,
   privacy route, directory source, or response authority leaves the contact in
   retryable pending state and returns an exact nonfatal UI status. The current
   release composition intentionally reports the missing genesis pin: the
   legacy mailbox trust floor is not silently reused as XPoint genesis authority.
   Registry origin validation and Registry HTTP client construction are deferred
   until the first operation that actually provisions network reachability; a
   missing or unreachable Registry cannot prevent the local shell or onboarding
   from opening.
   Operational failures remain retryable and are not classified as reset
   authorization.
3. `AuthNavigationState` selects onboarding or conversations from local state.
4. Conversation and message pages render cached snapshots first; sync runs in
   a cancellable background path.

## Messaging

Outgoing messages are persisted to the durable MAU2 outbox before dispatch.
The existing six-node physical Debug composition loads two hash-bound three-hop
privacy routes from app-private `mailbox-runtime-v1/privacy-routes.v2.json`.
Canonical MAU2 is wrapped in a padded binary privacy frame and sent to the first
route's public HTTPS ingress. The exit returns a reply encrypted to the
per-attempt client key; only then does the existing mailbox adapter verify MQR3,
MRP1, or MAR1 evidence and advance durable state. The fallback route is eligible
only when the primary proves forwarding did not start. Direct MAU2 HTTPS,
Session RPC, and routed-storage fallback are absent.

The phrase “public HTTPS ingress” is literal for the current implementation:
`PrivacyManagedIngressHttpTransport` does not dial through
`IRealityTransportRuntime`. The target release path must send that same opaque
frame through an attested local/embedded Xray connection to the XNode
VLESS/Reality ingress and must fail closed instead of bypassing it with direct
HTTPS.

The Survival primary topology is `xnode3 -> xnode4 -> xnode1`; `xnode1` is the
sole authoritative mailbox coordinator. The current lab fallback topology is
`xnode5 -> xnode6 -> xnode2`; `xnode2` is a forwarding-only privacy exit and,
after unwrapping the privacy frame, forwards the unchanged canonical MAU2 to
`xnode1`. MQR3 therefore authenticates `xnode1` as coordinator and does not
identify the terminal fallback hop (`xnode2`) as the coordinator.

The raw route artifact SHA-256 must equal both the activation
`privacyRoutesSha256` and the same field in the verified Mr. X-signed policy.
Its current lab schema binds the platform, two clean HTTPS root origins and
three independently keyed hops per route. The six-node fixture happens to use
six distinct router identities/keys, but that is test-topology evidence rather
than a production capability claim. Production v1 does not promise a
failure-domain-disjoint fallback.

The routed runtime accepts between three and sixteen distinct lowercase pinned identities
and canonical router URLs. Router bases have a root path and no
userinfo, query, or fragment; HTTP requires a literal loopback IP, while HTTPS
uses the existing identity/certificate pinning. `MauiProgram` resolves the route
provider and message transport through the same Core production factory covered
by Release tests. `UseMauiApp` and native handler setup own the framework portion
of startup. Every Deep-owned service, adapter, ViewModel, and page registration
is then added by one headless `ConfigureApplicationServices` entrypoint called
exactly once immediately before `MauiAppBuilder.Build`; no Deep registration is
allowed after it. Runtime settings, platform metadata, endpoint validation, HTTP
client factories, and service factories are precomputed before the entrypoint
and passed in one immutable input bag. The Release entrypoint itself has no
branches, switches, ambient environment/config reads, reflection, or dynamic
invocation.

CI builds the Windows Release assembly and applies a fail-closed call-target
allowlist to `CreateMauiApp`. New same-app helpers are rejected, especially any
helper able to receive the builder, service collection, or an opaque object
derived from them. The guard verifies entrypoint dominance and adjacency in
compiled control flow, scans the reachable Release call graph for direct/stub
tokens, executes the exact entrypoint against a real final DI container, and
binds the complete current Windows Release service manifest. It also checks
resolved factory instances, the configured pinned set, and the absence of direct/stub
descriptors or concretes. Conditional/dead entrypoint, pre-entrypoint
`RegisterExtra(builder.Services)`, environment-conditional direct/stub,
indirect post-entrypoint registration, and post-entrypoint descriptor mutation
fixtures must fail without depending on process environment values.

The verifier intentionally does not start MAUI/COM and does not claim Windows
runtime rendering or Android runtime/device coverage. Android Release remains a
separate compile/package gate plus physical-device lane; this Windows compiled
composition guard is not presented as Android runtime evidence. It also does
not inspect framework-owned registrations created by `UseMauiApp`.
Source-text matching is not release evidence. The generic Reality/XNode route
provider retained in the app graph serves diagnostics and adjacent transport
work only. It is not an MAU2 message transport and no Session/onion storage
message transport is registered by the MAUI composition.

### External persistent-outbox execution boundary

The portable persistent outbox can be requested with
`DEEP_PERSISTENT_TRANSPORT_OUTBOX=1`, but MAUI enables it only after a
platform supervisor has passed both binary attestation and a live protocol
probe. A failed or missing supervisor clears the effective outbox feature flag
and continues with the existing message runtime; it does not pass a dormant
executor to `ClientRuntime`.

The current Windows boundary is a bounded, per-dispatch child-process
supervisor in `Deep.Client.Maui.Outbox`. The worker executable must live at the
fixed app-relative path
`outbox-worker/Deep.Client.Maui.OutboxWorker.exe`, remain below a non-reparse
trusted root that the client principal cannot add to, modify, or delete from.
The parent directory and trusted root are held open with write/delete sharing
denied from verification through Job termination, preventing root
rename/recreate races even when the parent grants `FILE_DELETE_CHILD`.
Production activation also rejects a principal that can add, delete, change
ownership/DACLs, or write attributes in either directory. The executable must
match `DEEP_OUTBOX_WORKER_SHA256`; every regular file under
the bounded deployment root must also match the deterministic digest supplied
in `DEEP_OUTBOX_WORKER_BUNDLE_SHA256`. All attested files remain open with
write/delete sharing denied from verification through worker termination.
The bundle digest is SHA-256 over the ASCII domain
`Deep.ExternalTransportOutbox.Bundle.v1\0`, followed for each ordinally sorted
root-relative `/` path by its big-endian UTF-8 path length, big-endian file
length, UTF-8 path bytes, and file bytes.
Each invocation inherits only its three private standard handles, uses a fresh
256-bit session key and nonce, HMAC-SHA-256 request/response binding, and
fixed-width/length-prefixed binary request and response payloads. A maximum
1 MiB ciphertext therefore produces a mathematically bounded 1,048,759-byte
authenticated request frame without base64 expansion. The process is created
suspended, assigned to a preconfigured
kill-on-close Windows Job Object, and only then resumed; worker or descendant
code cannot execute before Job membership. Timeout, cancellation, crash,
malformed receipt, failed receipt, executor disposal, or hash drift returns no
trusted receipt. Termination uses a bounded native Job accounting check; if an
empty Job cannot be proven, the executor permanently poisons its admission
capacity instead of releasing it. Worker
stderr is drained without retention. No worker is resident while the app is
idle.

This is a platform execution boundary, not production outbox activation.
There is currently no production worker binary, packaged worker hash, or
versioned adapter definition for interpreting the opaque ciphertext bundle and
dispatching it through the routed transport. Consequently both checked-in
Debug and Release configurations leave the feature disabled.

Android is explicitly fail-closed. A `Task`, thread, `JobService`, or foreground
service in the MAUI process is not an independently killable boundary and is
not registered as an executor. Android activation requires a separately
declared process, authenticated length-bounded Binder IPC, package/signature
binding, bounded admission, Binder-death confirmation after forced termination,
and physical-device hostile-worker/battery evidence. Until that exists, an
Android request resolves to `UnsupportedPlatform` and the normal runtime
continues with persistent transport outbox disabled.

## Development transport diagnostics

The optional Reality/VLESS runtime is currently adjacent transport diagnostics only. It
does not yet implement, select, or forward the privacy mailbox message path, and no
Session RPC client or membership-route provider is registered. Privacy mailbox
hops come only from the separately signed and activation-bound
`privacy-routes.v2.json`; Settings projects a read-only diagnostic view from
that active route. Connecting these two paths without introducing an unmasked
fallback is the highest-priority transport change before release.

Android cleartext is broadened only in non-Release
`DeepPhysicalE2E=true` packages. The normal and Release network-security
resource is unchanged and keeps its deny-by-default cleartext policy.
The same explicitly selected physical-E2E Development composition passes a
dev-local endpoint policy through both initial parsing and composition-factory
revalidation. It permits canonical literal loopback, RFC1918, and
`169.254.0.0/16` IPv4 HTTP router and service URLs. Hostnames, public,
unspecified, multicast, noncanonical IPv4, credentials, query, and fragment
forms remain rejected. Ordinary Debug and Release continue using the original
HTTPS-or-explicit-loopback policy.

Groups use the same transport and persistence guarantees for state and messages;
there is no legacy-group read-only conversation kind. Attachments are encrypted
before upload using the current authenticated chunked `DEEPATT2` format, and
downloads fail closed for every other encrypted format. Ordinary images are
compressed for inline media while document mode preserves the source file.

Local tests and the isolated `GroupText` physical harness cover this
composition, but a successful current Android ↔ Windows device run has not
been retained. Arbitrary contacts, direct text and groups therefore remain
release-unproven.

## Direct P2P and user-managed status

`RuntimeTransportMode` and the portable delivery policy reserve an isolated
`direct-p2p` profile. Release composition deliberately rejects it unless an
explicit `IDirectP2pSessionMessageTransport` is registered; no such production
adapter exists. Android Wi-Fi/BLE code is disabled review scaffolding, Windows
Nearby is unsupported, and there is no message rendezvous, authenticated
handshake, mesh routing or NAT traversal. Product scope keeps Direct P2P as a
future architecture requirement, not a blocker for the initial XPoint-only
production release. It remains safely unavailable rather than acting as a
hidden fallback.

The future mode is a peer mesh: `Direct` means no mandatory central mailbox,
not necessarily one hop. Composition must permit authenticated direct and
multi-hop store-and-forward links while preserving the existing E2EE/outbox
boundary. UI and telemetry must distinguish direct, mesh-relayed and
partitioned states; an intermediate device never becomes message origin,
destination or plaintext holder.

The protocol also reserves `authenticated-mau2/user-managed` and SHR1
self-hosted activation, but MAUI currently composes the official Registry
coordinator only. Future on-prem support requires a distinct profile-scoped
authority/acquisition provider and profile-scoped file, signaling and TURN
services. It must not be implemented by weakening official authority or TLS
validation.

When a feature is disabled by the selected transport, its control must be
hidden or report an exact unavailable state. In particular, calls must not
appear available in `direct-p2p` while `CallsEnabled` is false or fall back
to process-local signaling.

## Push

Android uses FCM and Windows uses WNS. Both register a provider token through the
same signed v2 subscription protocol. Provider payloads are encrypted with
AES-256-GCM and contain no message plaintext.

Windows requests a channel on foreground activation, keeps the previous channel
valid during server registration, and closes it only after durable confirmation.
Raw background activation creates a headless service graph and does not create a
MAUI window or invoke Windows Hello. Invalid, expired, replayed, or undecryptable
payloads are dropped.

Local message notifications are decided only after inbox synchronization. The
durable queue carries both message and conversation IDs; a foreground notification
is acknowledged without presentation only when that conversation is actively open,
while background delivery and other conversations are always presented.

## Platform Work

Android uses JobService-backed retry/catch-up, Firebase callbacks, biometric or
device-credential lock, MediaStore downloads, and a bounded share-ingress service.

Windows uses architecture-specific Xray, Windows Hello, WNS raw background
activation, AppNotificationManager, MSIX Share Target activation, system
Downloads, and foreground/network-restoration maintenance. Production packaging
is documented in `WINDOWS_RELEASE.md`.

iOS remains in the source tree but is outside the current release phase.

### Nearby lifecycle durability

Nearby stop and cancellation first bound physical radio cleanup, then enqueue
the durable Off intent before completing. A completed `StopAsync` can therefore
publish a stopped radio with `IntentPersistenceState.Pending` while an earlier
intent-store call is still blocked. `DrainAsync` is the public durability
boundary: after it returns, the snapshot reports `Consistent` with the Off
intent saved, or `Failed`; disposal converts the failed state to the typed
`IntentCommitFailed` transition error. Disposal still stops the radio before
waiting on that same durability drain.

Platform subscription disposal never invokes unsubscribe under its monitor.
While one unsubscribe attempt is running, reentrant and concurrent `Dispose`
calls return immediately. A successful attempt is exactly once; a failed
attempt reports only the sanitized state-read error and atomically permits a
later retry.

Coordinator disposal has the same non-joining concurrency boundary. The first
`DisposeAsync` caller owns the operation and observes its success or typed
failure; calls made while that operation is in progress return a completed
`ValueTask`. Disposal first closes the platform-event queue, bounds physical
radio cleanup, and unsubscribes outside internal locks before the final drain.
Captured or racing platform events are ignored once the lifecycle leaves
`Running`, so an event flood cannot extend the durability drain. Event
admission and queue publication share the `sync` then `eventSync` lock order:
an event admitted before disposal is visible to the final drain, while later
events are rejected. If disposal fails after the subscription has already
closed, the coordinator enters a retryable failed-disposal state. Normal
start, stop, refresh and public drain operations remain closed there; only a
later `DisposeAsync` owner may retry durability and terminal cleanup.

## Offline update verification

`Deep.Client.Maui.Core` contains the portable P02B verifier for exact canonical
TUF/POUF metadata, threshold signatures, sequential root rotation, expiry,
rollback, and parent hash/length bindings. It copies a metadata-bound APK into
an app-private bounded snapshot before calling the narrow
`IAndroidPackageSignerVerifier` adapter. Android's archive API remains the
package-signing authority; Core does not implement APK signature cryptography
or install packages.

No production trusted root or update key is embedded and no update verifier is
registered in the Release service graph. The Settings row therefore shows an
explicit fail-closed unavailable state until Mr. X provisions a separately
reviewed non-production trust configuration. This preserves the exact
privacy-routed Release composition. Verification failure has no override,
and the ViewModel requires an exact visible version confirmation after success.

iOS, iPadOS, and Mac Catalyst remain subject to Apple signing, provisioning,
notarization, and supported distribution channels. P02 metadata does not provide
an iOS sideload bypass.
