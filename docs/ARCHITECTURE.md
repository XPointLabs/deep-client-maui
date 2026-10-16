# Deep MAUI Architecture

## Current composition baseline — 2026-10-03

This is a source-composition description, not release qualification. The
[workspace audit](../../docs/architecture/ARCHITECTURE-AUDIT-2026-10-03.md) records the
cross-repository findings; the
[implementation plan](../../docs/architecture/IMPLEMENTATION-PLAN-V1.md)
owns the execution order and acceptance gates. Dated notes below preserve
implementation context and do not define another backlog.

The actual application references `Deep.Client.Maui.Core.Did2` and Shared's
`Deep.Client.Shared.Production.csproj`. The
[application project](../src/Deep.Client.Maui/Deep.Client.Maui.csproj) removes
the former pages, startup and service graph, then explicitly includes the DID2
account owner, platform custody/crypto adapters and diagnostic network services.
The [DID2 Core project](../src/Deep.Client.Maui.Core.Did2/Deep.Client.Maui.Core.Did2.csproj)
includes the account and direct-text ViewModels and their narrow interfaces.
The older `Deep.Client.Maui.Core` project and a service file present on disk
are not evidence that the current application composes that feature.

The dedicated Android HTTPS driver now exposes bounded contact/direct-text UI
phases in addition to account diagnostics. It still requires an explicit serial,
exact APK/source/signing provenance, foreground ownership and before/after
protected-package snapshots.
`ScrollContacts` uses the same bounded gesture and exact owned ScrollView guard
as settings, selecting only the fixed `Page.Contacts` ID. It exposes off-screen
retry/status controls without choosing retry, changing the draft or dispatching.
Only one observed conversation may be selected;
typing requires an empty exact editor and independently reobserved focus. Peer
addresses and message contents are not included in result JSON. The only message
input is a fixed synthetic QA marker. Each phase is one action plus observation,
never an automatic send/accept retry; `deviceDeliveryVerified` stays false until
separate cross-device evidence is assembled. See the
[physical checkpoint](DID2-HTTPS-DEVICE-2026-09-28.md).

| Application surface | Current source behavior |
| --- | --- |
| Default and Release builds | DID2 local account/custody graph; no registered network admission or conversation runtime. |
| Loopback canary | Explicit non-Release account/admission diagnostic. |
| HTTPS physical diagnostic | Explicit non-Release admission, publication and Start/Accept/SendText/List/Synchronize commands. |
| Automatic network recovery | Diagnostic proof/publication reconnect with cancellation and bounded retry; it does not drain a message outbox or poll an inbox. |
| Files, images and governed groups | No current DID2 UI/runtime composition. |
| Push, background message sync, calls and native media integration | Earlier implementations are excluded from the current application service whitelist. |

Manual registration/contact-proof rechecks join the reconnect scheduler's actual
single pipeline; they no longer launch a second admission/publication flight.
An idle explicit check starts a fresh flight, not a cached success. Cancelling
one waiter does not cancel the shared owner, while suspend/offline/account-change
still cancels the operation and all pending waiters. RetryAfter and the existing
two-minute active-pipeline budget remain unchanged. A routine periodic refresh
does not turn a previously verified UI offline while it is running; an actual
failure/offline/invalidation still does. This UI state is advisory, never a
freshness capability: each contact/message mutation independently obtains and
verifies its actual proof, placement, time and protected account authority.
This source correction requires new matched client builds and physical retest.

Network-dependent contact/accept/text actions and the network part of mailbox
refresh now share that same process-owned lane with background verification and
account mutation. Local history/selection does not wait for it. A queued refresh
has not begun authority/HTTP work: its existing two-minute budget starts only
after acquiring the lane. Actual per-request/cryptographic lifetimes do not move.
Foreground work retains its caller's UI context; offline/suspend/account revision
changes cancel its actual token, reject stale queued work and drain the old owner
before account mutation. A cancelled foreground waiter cannot cancel another
background owner. Exceptions release the lane and retain their inner cause,
while visible messaging errors remain closed text. This coordination conveys
no authority, success, retry/non-delivery or permission to reset original work.
The foreground delegate now receives its own at-most-two-minute cancellation
budget only after acquiring the lane. Previously that budget applied to the
background pipeline alone. The same actual delegate must drain before release;
an uncooperative operation is not detached with `WaitAsync`, and late success
after cancellation is rejected. This scheduling bound is not request expiry,
proof of noncommit, an automatic resend or permission to replace retained work.
Only the internal test constructor may select a shorter positive budget; zero,
negative and budgets above the production maximum reject before subscription.
The Android Rejected/foreground-cancellation observation motivated this source
audit; it does not by itself establish this race as that device's exact cause.
Source qualification: `artifacts/s05-current-publication-20261015/foreground-pipeline-focused-02`
passed35/0/0/native0; `foreground-pipeline-clean-01` passed150/0/0/native0.
The scheduling cases use controlled delegates, not network/physical evidence.
Matched Windows/Android builds and retained-device retest remain required.

Only the non-Release HTTPS QA composition subscribes a messaging failure
observer. It sends bounded category/depth/code-method/line/IL-offset metadata to
the existing bounded diagnostic log, never source paths or private error text.
For an exact built-in CryptographicException only, fixed owned contact-guard
literals are mapped to closed diagnostic codes; unknown/private messages remain
Unclassified and custom getters are never invoked. Data/ToString are not read.
These labels cannot authenticate a remote failure or change retry. The UI keeps its closed error text. The observer
does not change business state, retry, cancellation or authority and cannot mask
the original failure if diagnostics itself fails. The sealed Protocol
`ApplicationCoreFormatException` at the exact cryptographic-verification/failure
stage additionally maps three fixed replica-prekey literals to binding,
not-before and expiry labels. An ordinary/custom FormatException is not
classified from its text. These observations cannot authorize retry, replacement
keys or a longer lifetime; raw exception text never enters the log.
The shipping composition does
not register it. `business-diagnostics-clean-02` passed160/0/0/native0; these
privacy/observer tests are not device delivery evidence.
`business-diagnostics-windows-compile-01` compiled the matching working tree
with0 warnings/errors; compile-only is neither an installed artifact nor release
qualification. The earlier158-case receipt remains unchanged.
`business-guard-diagnostics-clean-01` passed174/0/0/native0 for the narrowed
guard/IL metadata slice. A diagnostic guard label is not an authenticated result.
The next QA-only label extension covers contact-object authoring, prekey
selection/validity and exact owned publisher/delegation guards, using the same
literal-only classifier. It adds no outcome, private text or recovery operation.
`contact-author-diagnostics-clean-02` passed203/0/0/native0 on the full Clean
project suite; device mapping is still a separate check.
Matching `contact-author-diagnostics-smoke-02` passed119/0/0/native0. Both use
Windows PowerShell5.1 / SDK10.0.301 and do not qualify physical delivery.
The follow-up diagnostic covers the remaining bundle-signing, retained issuance,
successor scope and current-context guards plus the closed route-time artifact /
reason product. Complete literal matching is required; unknown artifacts, private
suffixes and custom exception getters cannot enter a label. This is still QA-only
metadata, not an exception-text trust decision or a change to crypto/expiry policy.
`contact-author-diagnostics-clean-03` passed220/0/0/native0 and matching
`contact-author-diagnostics-smoke-03` passed119/0/0/native0, using the same pinned
Windows PowerShell5.1 / SDK10.0.301 project-suite lane, not release qualification.

Network suspension follows actual `Window.Stopped`/`Destroying`, not loss of
focus through `Deactivated`; resume/activation is idempotent for an already
visible window. This follows the [MAUI lifecycle distinction](https://learn.microsoft.com/en-us/dotnet/maui/fundamentals/app-lifecycle?view=net-maui-10.0).
An auxiliary input/focus transition must not invalidate verified UI readiness.
The diagnostic network card exposes only the closed reconnect state enum,
separately from the last manually observed result; its guarded Android receipt
records the same closed enum, never exception messages or identity material.
This does not add background inbox/message composition or waive fresh authority.

[MauiProgram.Did2](../src/Deep.Client.Maui/MauiProgram.Did2.cs) registers the
conversation runtime only with `DEEP_DID2_HTTPS_ADMISSION`; the application
project explicitly rejects that diagnostic switch for Release. A separate
production composition remains required. A successful Debug build or local
ViewModel test does not qualify it.

The HTTPS diagnostic defaults to system-CA DNS HTTPS on the default port.
For a retained local dev contour, both platform build scripts accept an explicit
`PrivateCertificateAuthorityPath` containing exactly one **public** CA certificate
(no key/PFX). They pin its DER SHA256 into the isolated non-Release build.
Only that opt-in permits a canonical private HTTPS address/nondefault port.
The runtime independently checks the bounded asset, CA signing usage and exact
fingerprint before using Shared's app-scoped private-CA transport factory.
Hostname/SAN, server authentication, chain and revocation checks remain required;
there is no arbitrary certificate callback, global root installation, HTTP or
missing-trust fallback. Release rejects these inputs. Signed bootstrap/network
authority and account-state isolation remain separate requirements. Both Windows
and Android HTTPS diagnostics select a network-scoped local data directory;
switching the signed network does not reset or open another network's account.
Previously stored diagnostic data is left untouched, not migrated. This is
configuration support, not physical E2E acceptance.

Android obtains canonical `DataDir`/`FilesDir` from its OS context and requires
the files directory to be the sandbox's exact `files` child. The physical
directory must remain below that anchor, with no symlink, reparse point or
regular-file component before and after creation. Foreign OS ancestors above
the sandbox are not subjected to Windows reparse-point checks. The initial
device attempt with the all-ancestor guard crashed before account/network
activation; installation alone was not startup or E2E evidence. No data reset
is part of the correction.

The current DID2 application no longer references the unused Windows `Pkcs`
package: its sole CMS-attestation consumer is already outside the compiled
service whitelist. This removes an unnecessary vulnerable package rather than
suppressing NuGet audit warnings. Separate retired attestation tools are not
qualified by that application-package cleanup; the Microsoft
[CMS decode advisory](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) and
[CMS signature advisory](https://github.com/advisories/GHSA-846f-f764-76mx)
still require a patched package wherever those tools remain in use.

Private-profile TLS rejections retain Shared's closed chain/CRL category.
The public-DNS-only, always-rejecting diagnostic connection is not invoked for
a private-root build: an unclassified failure remains `PrivateProfileUnknown`,
not an exception about public-origin configuration. Neither path accepts TLS or
retries with weaker certificate, name or revocation checks.

For Android's local private-CA diagnostic, the supported builder generates a
network-security resource from the validated Registry DNS/IPv4 host. It permits
HTTP only to that exact host (no subdomains), which the local dev PKI also uses
for its CA-signed public CRL. All other hosts retain the base cleartext deny;
all application service origins still require HTTPS. The resource adds no CA
trust anchor: explicit CA trust remains app-scoped in the HTTP factory, with
online revocation and name/EKU/chain validation unchanged. Missing policy or
using it without private-CA/non-Release/physical Android inputs fails the build.
Public-CA diagnostics retain their separate public CRL policy; Release rejects
this opt-in. Private profiles with another CRL host need an explicit reviewed
configuration, not a wildcard allowance. The observed device rejection before
this increment was `AccountProof / RevocationStatusUnknown`, not publication or
message delivery. The regenerated APK still requires a real-device recheck.

After the CRL-policy update the same retained Android account passed the TLS
boundary and reached ADH1 lineage verification. Initial admission must acquire
the identity-neutral authority/time-policy history before verifying its first
current account proof: a current ADH1 may already name an XNA1 successor.
All three diagnostic network entry points now verify the complete fetched
XNA1/DTS1 chain from the packaged genesis pin, require its first exact records
to equal the packaged genesis bytes, and reauthenticate the original directory
genesis. This is root-history acquisition only, not network currentness or
permission to mutate: the owned source still independently verifies the fresh
account proof, signed network closure and protected directory/network floors
before releasing placement or publication authority. No lineage check, floor,
reset or generation fence is relaxed. The previous genesis-only composition
failure and its device receipt remain negative evidence.

The matched Shared pipeline consumes
[DR-0125](../../docs/survival-program/decisions/DR-0125-current-publication-submission.md):
one current publisher-signed submission reconciles missing/pending/completed
issuance of the exact original intent. The separate completed-only reader and
lease-request endpoints are removed, not retained as compatibility paths.
Original account/request/journal and permanent winner stay unchanged; current
authority, fresh XPL and both-node commit checks remain separate. Retained
Windows and Android diagnostic actions reached verified-publication, but the
subsequent Android cold-reopen action returned TransportIo. Neither that failure
nor component success establishes cross-device contact/text delivery or permits
account reset; see the [current checkpoint](DID2-HTTPS-DEVICE-2026-09-28.md).

The diagnostic reads conversation/message history directly from the Shared
protected local owner without bootstrap HTTP or fresh endpoint proofs.
`CanReadLocal` is separate from network `IsReady`; loss of network verification
does not clear authenticated projections or drafts. Network commands remain
closed, and corrupt local reads or account replacement clear projections.
It offers explicit message retry and single-page synchronization. Offline
queueing, automatic delivery scheduling and complete expiry/capacity
recovery remain implementation work. Shared has durable Store, semantic inbox
and mailbox tombstone ACK paths; these are not sender-visible AppAck/Read
receipts. Remote attachment transfer and group delivery are also still missing
from this graph. The linked plan owns their sequencing; no legacy service is
implicitly re-enabled by this document.

## Local persistence diagnostic classification (2026-10-03)

The HTTPS diagnostic distinguishes the existing typed SQLCipher entry-guard and
network-store open rejections from generic IO with fixed display-only labels
`LocalEntryGuardRejected` and `LocalNetworkStoreRejected`. Private exception
messages, paths and scopes never enter UI text. A generic IO wrapper cannot
impersonate either typed rejection. The existing neutral mailbox dependency
exception is displayed as `OnionDependencyRejected`, without claiming delivery
certainty from its public constructor. Other mailbox failure categories retain
generic IO classification. No retry, reset, key repair or transport
fallback is introduced; this is not physical localization until the rebuilt
device actually observes a label. The current physical checkpoint is recorded
in [the workspace evidence](../../docs/DID2-PRODUCTION-DEVICE-CHECKPOINT-2026-10-03.md).

## Publication coordination clean break (2026-10-03)

[DR79](../../docs/survival-program/decisions/DR-0079-did2-publication-issuer-successor.md)
requires matched V3 publication peers/Registry and a current-only protected route
journal rebuild. Existing HTTPS QA binaries cannot attest this increment. Older
disposable QA state uses the explicit isolated recovery control; never silently
migrate or erase a saved account. Keep its recovery material private and do not
claim retained-account/device recovery from local crypto/HTTP fixtures. Physical
contacts, messages, files/images and groups remain unproven.

## Owned DID2 contact/text UI candidate (2026-10-02)

[DR-0066](../../docs/survival-program/decisions/DR-0066-did2-application-sqlcipher-random-key.md)
also requires explicit reset of older isolated QA application generations;
the compiled startup recovery control never migrates or silently recreates them.

[DR-0065](../../docs/survival-program/decisions/DR-0065-did2-contact-application-command-boundary.md)
connects the opt-in HTTPS diagnostic to a testable messaging ViewModel and the
account-owned Start/Accept/QueueText/RetryText/List/Synchronize commands. The existing
dark/cyan workspace now has contact entry, request/accept states, authenticated
message rows and a text composer; narrow windows show either list or selected
conversation. Stable account/navigation IDs remain. Default builds without a
real conversation runtime retain explicit unavailable controls, not a V1 adapter.
Files/images and governed groups remain unavailable until their owners are composed.

UI authorization flags enable controls only; Shared independently verifies every
operation. Errors use fixed sanitized text. Explicit contact retry reuses the
actual protected intent across restart; unknown text outcomes keep the same
operation/text and cannot be replaced by a different send.

Contact retry stopping maps the existing
[stopped-work contract](../../docs/architecture/TRANSPORT-NEUTRAL-MESSAGING.md#841-sendattempt-transitions)
to `CancelOwnInitialContactAsync`, through the same foreground network lane and
independently current account authority. The key-free operation projection adds
read-only `IsStopped`; it is not authorization. Cold Add first discovers the
retained nonstopped intent and offers explicit Retry or `Contacts.StopRetry`,
without dispatch. This local discovery stays outside the foreground network
lane and remains readable offline; it cannot allocate a new intent or dispatch
without current verification. New UI intent allocation occurs only inside the
held foreground lane, immediately before actual Start. A failed/lost stop reply retains the original UI intent.
Only completed owned stop removes that UI choice, and only a subsequent Add
creates a different logical request. Stopped originals, uncertainty and replay
floors remain with their existing retention owners; no reset, recall, expiry
extension, new journal generation or automatic remint. The UI explains that
stopping retries does not withdraw an already accepted remote request.

The same bounded HTTPS QA code-location observer also reports actual background
verification failures through `Did2.NetworkFailure` in the existing diagnostic
log. It preserves and rethrows the original exception, including cancellation;
observer failure cannot alter the reconnect result or grant network authority.
No exception text, payload, identifier, endpoint or new persistence surface is
added. This is debugging metadata, not verification or delivery evidence.

The supported physical Android HTTPS QA builder keeps Debug/isolation/signing/
private-CA and Release rejection rules, but explicitly uses optimized Mono JIT
(`UseInterpreter=false`, `AndroidUseInterpreter=false`, global `Optimize=true`).
A restricted Android-only project profile and pre-build guard reject overrides
that restore interpretation or disable optimization. The build receipt labels
`optimized-jit` and `releaseEvidence=false`; this does not qualify shipping.
The supported builder uses `dotnet build --no-incremental` and records
`incrementalBuild=false`. Fresh APK output alone does not isolate the project's
Android JNI/runtime intermediates. Regenerating that graph avoids packaging
stale registration after source/profile changes without touching device data
or assigning colliding intermediate paths to project references.
This HTTPS QA graph has no push composition and excludes the default
production-package Google Services resource, rather than packaging a mismatched
Firebase configuration. Actual Release keeps its external configuration guard;
this is not push-delivery evidence or removal of the release push requirement.
Ordinary development Debug, Windows and actual Release profiles are unchanged.
[Microsoft's build-property reference](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties#androiduseinterpreter)
documents the Mono Debug interpreter default. Actual SDK evaluation on this
prior QA configuration returned both interpreter flags true/Optimize false;
one physical request used a 96% QA CPU sample and terminated at permanent-contact
`RequireTime` IL118. These observations motivate a controlled JIT comparison,
not a proven universal cause, timing waiver or physical delivery result.
Starting a new UI command clears the previous command's success status before
awaiting work, so an earlier stopped/stored label cannot describe a busy retry.
The optimized Android package need not be debuggable; private `run-as` access
is not assumed. Future HTTPS QA failures additionally emit only the same closed
metadata under `Deep.Did2Qa` in Android logcat. This source-scoped diagnostic
does not export existing private logs, change sandbox permissions or expose
exception text/account data. Collection selects the exact QA process/tag and
accepts only the existing bounded category/check/method/line/IL grammar.
Closed exception categories additionally distinguish arithmetic overflow,
disposed state and invalid operation without reading their messages or data.
Permanent-time QA classification distinguishes the six fixed request/object
boundary failures. Protocol retains the same predicate, exception type and
windows; the labels do not authorize retries or change UI business outcomes.
[DR-0067](../../docs/survival-program/decisions/DR-0067-did2-ordinary-store-completion-and-ui-retry.md)
adds actual protected original-text/operation recovery before composer allocation
and on conversation selection after restart. Nothing is sent automatically.
Input clears after owned Store completion, before an independent history refresh,
so a later UI refresh error cannot resurrect a completed send. Physical restart
evidence remains open. Loss of current verification closes network commands,
not local history; retained rows grant no fresh authority. Isolated HTTPS QA startup recovery now offers an
explicit confirmed STORE-V2 reset; normal shipping startup has no such reset.
The internal composer metadata helper has focused restart/defensive-copy/
unknown-operation replacement tests; it cannot construct or authorize a Shared
conversation. Test visibility is limited to the Clean UI test assembly and does
not expose an account key, proof, route, signer or completion setter.

The current S04 source cutover follows the
[creation/retry owner](../../docs/architecture/TRANSPORT-NEUTRAL-MESSAGING.md#843-compaction-and-boundedness):
the composer reads an existing retry operation or calls QueueTextAsync, then
adopts the returned operation and calls RetryTextAsync. It no longer generates
text operation IDs, and the runtime has no create-or-retry SendText overload.
The visible Send command and automation IDs are unchanged. Interrupted creation
is discovered by the existing protected pending-text recovery before another
creation. On2026-10-13 the Clean consumer Release build and Windows Debug app
build passed with zero warnings/errors; all95 Clean cases passed with native0/
wrapper0 and2505 unchanged inputs. The scoped receipt is
`artifacts/s04-creation-20261013/consumer-clean-02/focused.trx` (SHA256
`32F14E7DF2796D9B2E0BA4E69956F0A0C7D5A89FDC3C470802A4F0B14241CE59`).
This is consumer compilation/UI-contract evidence, not physical messaging,
release packaging or terminal-cleanup qualification. The Android Debug app
build also passed with zero warnings/errors at2026-10-13T00:10:23.9991544Z–
00:16:14.2602249Z; no APK was installed or accepted as a production package.
Full cross-repository qualification remains required.

Composer selection never transfers draft text between contacts: refreshing the
same selected dialog preserves its editable draft; selecting another dialog or
closing the detail clears it. Only an unknown send's protected original text may
be restored when its own dialog is selected again. This is UI isolation, not
mailbox completion or physical delivery evidence.

Each UI command also owns a linked cancellation scope. Account replacement or
loss of network verification cancels an active network scope, not a local read; asynchronous account,
intent and projection reads are rechecked before transport dispatch or UI
mutation. Reverification cannot revive an earlier command. Replacing the
account clears process-local retry metadata; retries for the current account
are restored only from its protected owner. The delayed-lookup reset/refresh
tests exercise UI lifecycle plumbing, not real network or device delivery.

Reconnect invalidation publishes its new state and wake before cancelling the
old attempt. Cancellation can synchronously resume the worker inside the
reentrant lock; the old invalidation must not overwrite newer success or queue
an extra attempt afterward. A deterministic cancellation/reconnect regression
preserves the exact two-attempt assertion. Local availability and delivery
semantics belong to the [normative owner](../../docs/architecture/TRANSPORT-NEUTRAL-MESSAGING.md#31-local-availability-и-delivery-lifecycle);
test scopes and receipts belong to the
[Shared checkpoint](../../deep-client-shared/docs/testing/s07-local-history-2026-10-04.md).

Local command evidence and exact test counts are in
[SPRINT-HISTORY](../../docs/SPRINT-HISTORY.md). Windows ARM64 normal and opt-in
HTTPS compilation passed without warnings; neither is physical delivery evidence.
`Invoke-Did2HttpsWindowsBuild.ps1 -CompileOnly` permits a dirty-tree compiler
check only, never a device-qualified package. Normal `-Execute` still requires
the expected commit and clean MAUI tree; Android does not opt into compile-only.


## Owned permanent-contact entry candidate (2026-10-01)

[DR-0051](../../docs/survival-program/decisions/DR-0051-owned-permanent-contact-client-entry.md)
connects the opt-in HTTPS physical diagnostic to the account-owned contact
publication after verified initial prekey publication. The owner chooses its
protected stable retry intent, signed profile and fixed genesis policy; UI code
never receives a private key or injects a threshold/transport callback. Peer
lookup now runs the actual owned permanent resolver, independently verifies
current peer DID2/floors and checks the diagnostic pasted public credential
against that result. Directory proof alone is no longer HTTPS lookup success.
Loopback admission-only remains explicitly diagnostic. These changes do not
activate a shipping inbox/outbox/group graph or prove physical delivery. Current
node DR50 activation and production package/root/clock/floor promotion are still
required before the diagnostic can succeed against the deployed network.

## DID2 network recovery candidate (2026-09-29)

The process-owned `DeepIdV2NetworkReconnect` subscribes to connectivity and
window activation, cancels obsolete attempts and serializes the entire fresh
DID2 proof/closure/pre-key composition. Typed 429/503 scheduling hints have
bounded monotonic backoff; malformed signatures or corrupt floors remain
rejected. Confirmed local account reset waits for the cancelled pipeline to
drain; returning connectivity never creates or resets an account.

The closed I/O display-stage list includes `BootstrapAuthority`, used while
reading the signed root/time-policy lineage. Its wrapper retains the original
typed exception as its inner cause: a typed429/503 remains eligible only for
the existing bounded reconnect backoff/RetryAfter; generic I/O, malformed
records and signature/floor rejection gain no retry authority. Unknown stage
names still reject. The display wrapper must not replace an expected stage's
transport failure with an unrelated argument error.

This currently restores the isolated DID2 diagnostic composition, **not** a
shipping MSG inbox/outbox/session graph. 46 clean and 119 smoke tests passed;
these are not Windows/Android physical reconnect or message-delivery evidence.
The optional HTTPS diagnostic build also requires the independently compiled
Mr. X trust pin and explicit public bootstrap/runtime inputs. Missing inputs
must not be bypassed to claim an active network build.

The 2026-09-30 physical HTTPS build exposed a missing native composition input:
the clean service allow-list omitted `MauiConnectivityStatusService`, although
the opt-in reconnect graph referenced it. The platform connectivity adapter is
now explicitly included after the wildcard removal. No retired account/contact
runtime is restored, and neither build success nor this composition contract
constitutes physical message-delivery evidence.

Historical wire and trust semantics are owned by
[DR-0014](../../docs/survival-program/decisions/DR-0014-directory-historical-catchup.md);
the local topology is documented in [Deep DEV](../../deep-devops/docs/DEEP_DEV.md).

Deep is a private messenger running over XPoint Network. The MAUI repository is
the native application boundary; portable protocol and state behavior live in
the sibling `deep-client-shared` repository.

## Retained layer and component notes

The following notes mix DID2 implementation history with the former application
graph. Descriptions of the old Core, MAU2 activation, groups, media, calls, push,
background sync and Release composition are historical and **not active in the
current DID2 whitelist**. Use the current composition baseline above when
assessing availability. Remaining DID2 notes describe their dated increment;
their old future-work statements do not supersede the consolidated plan.

### Shared runtime

`Deep.Client.Shared` owns:

- the clean-break 24-word `DeepRecoveryV1` and device-scoped keys; the retired
  13-word Session composition is not a production compatibility surface;
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

The staged DID2/STORE-V2 account owner has a separate platform protection
domain: `deep-store-v2/secure-storage.dss`, V2 DPAPI entropy on Windows and a
V2 Android Keystore alias. Its envelopes are tagged `WDS2`/`ADS2`; V1
protectors reject them. `DeepIdV2AccountRuntimeOwner` now opens and verifies
that isolated state without a network callback, rejects a different network,
and never creates the V1 store. `DeepIdV2AccountRuntimeAccessor` and
`DeepIdV2AccountViewModel` expose one-button offline creation, restart, phrase
reveal/hide and irreversible local deletion without a V1 identity or network
callback. The default MAUI Windows/Android startup now selects this DID2 owner
and shell rather than the V1 account/message graph. The app references a
minimal DID2 UI-core assembly and excludes the retired V1 MAUI services and
platform transport classes from its binary. Until production DID2
authority, contact, messaging and group consumers are composed, the shell
shows these network actions as unavailable; no V1 fallback is offered and this
is not device E2E. This graph still loads the hash-pinned ML-DSA candidate;
it is not a production-approved crypto provider or a release claim. The Windows clean integration gate
exercises real DPAPI plus the V2 SQLCipher generation across restart, retaining
the exact DID2 after the local phrase is deleted; this does not count as a
physical messaging E2E result.

The clean shell restores the responsive conversation-list/detail layout and
navigation to chats, contacts, groups and settings using DID2-only views.
Those sections intentionally show empty or explicitly unavailable states:
there is no send, attachment, contact-accept or group mutation control until
the respective verified V2 runtime is composed. The probe starts in settings
to preserve account diagnostics; the normal package starts in chats. This is
a visual/navigation baseline, not functional messaging or device-E2E evidence.

`DeepDid2AccountProbe=true` with a Debug `DeepLocalDev=true` build selects an
isolated `network.xpoint.deep.did2probe` account-only app, separate from both
production and the existing `.e2e` UAT package. It uses the V2 account model
and candidate ML-DSA verifier explicitly, never composes V1 network/account
services, and marks contacts, messages, attachments and groups unavailable.
Its account and settings surfaces reuse the existing Deep palette and responsive
mobile/Windows visual language without compiling the retired XAML, code-behind,
or SessionId ViewModels. Existing probe automation IDs remain stable. The
recovery phrase is hidden on page disappearance, can be copied only while
revealed, and still requires confirmation before device-local deletion. The
base probe presents no actionable chat, contact, attachment, or group controls.
Release or a non-local build rejects this switch. Probe results may establish
the local Windows/Android account gate, not production provider approval or
messaging E2E evidence. The probe stages the exact-hash Android ML-DSA
candidate from its APK before opening DID2 state; an absent or changed native
asset fails closed without enabling the V1 runtime.
The isolated account-probe and HTTPS-diagnostic settings additionally expose
an explicit test-account reset with a destructive confirmation dialog. It uses
the current account's STORE-V2 purge and clears all UI verification state before
returning to name-only onboarding. It does not rewrite expired signed prekeys,
erase another package, reset a node, or migrate old data; the normal client build
does not contain this diagnostic reset control. A recreated account has a new
identity and is never presented as continuation of the erased account.
The Android HTTPS diagnostic harness separates `BeginReset` (open and inspect
the dialog) from `ConfirmReset` (requires `-ConfirmIsolatedAccountReset` and
the exact owned dialog/button). Every phase keeps the existing other-package
before/after audit and rejects a missing or changed dialog before input.
Android identifies this owned alert by its fixed title rather than an Activity
path. The harness verifies the focused window's OS-reported owner UID against
the exact installed package and requires the package-owned warning and exact
confirmation control. A matching title alone or another app's dialog rejects.
An additional non-Release `DeepDid2CanaryAdmission=true` switch may be used
only with that isolated account probe. It requires an explicit loopback-tunnel
origin, separately pinned XNA1 and DID2 genesis-head hashes, and three exact
signed public bootstrap assets supplied at build time. The probe verifies the
XNA1/DTS1 authority lineage and empty V2 head before opening the account-owned
SQLCipher LKG. Its explicit settings action then performs real DGA1 admission,
independent nonce-bound ADP1/DTT1 verification, and durable LKG commit through
the bounded owned transport. Failed network verification leaves the local
account intact and offers retry. The optional DID2 peer-lookup control repeats
the local admission proof before every peer proof: an earlier successful UI
state is never treated as an unexpired authority. A failed local recheck blocks
the peer request and clears its verified UI state. This diagnostic does not
accept a relationship or compose message, attachment or group services; a
local HTTP hop over an authenticated SSH tunnel is not the release HTTPS
transport or device messaging E2E gate.
For the normal non-Release physical `.e2e` package, an explicit
`DeepDid2HttpsAdmission=true` build input instead connects the same
signed-authority and nonce-bound proof verifier to a canonical DNS HTTPS
origin. The exact XNA1/DTS1/genesis-head assets and two public hash pins are
still mandatory build inputs. This switch is rejected for the isolated probe,
loopback canary and Release. After admission it also fetches the bounded public
network closure through the owned HTTPS adapter and invokes Shared's
`VerifyCurrentNetworkAsync` with the same monotonic clock as the proof client.
The account-owned SQLCipher network floor must commit and reauthenticate
before the action succeeds. A missing, stale, altered or incomplete closure
fails closed while retaining the local account; the HTTP loopback probe remains
admission-only. HTTPS QA opens account-owned ONION custody and invokes the one
`PublishOwnPreKeyInventoryAsync` command under
[DR-0131](../../docs/survival-program/decisions/DR-0131-owned-prekey-maintenance-author.md).
It prepares/maintains and seals the complete inventory with fresh authority before
dispatch through the two independently selected replicas, using
the selected-entry transport frozen in
[`DR-0009`](../../docs/survival-program/decisions/DR-0009-did2-selected-entry-transport.md).
The action completes only after independently verified XIC1 signatures and
durable account-owned receipt-pair recording. Registry proof, staging or HTTP
success alone cannot satisfy it. The loopback probe remains admission-only.
This candidate has not yet proved live publication or physical ML-KEM.
It does not authorize contact acceptance, messaging, files or groups.
The default build still shows
these capabilities as unavailable when the physical UAT inputs are absent.
The DID2 HTTPS diagnostic rejects retired mailbox/PMA trust-floor and artifact
identity inputs rather than requiring fictitious values. Its explicit nonzero
network ID is still bound by runtime verification to the signed bootstrap.
`eng/Invoke-Did2HttpsWindowsBuild.ps1` builds a clean, explicitly pinned committed
source as an unpackaged portable Debug candidate, with preflight by default and
`-Execute` required to build. It neither installs nor launches the result and is
not a release-signing shortcut. Windows custody resides in a compile-time
per-network `XPointLabs/DeepDid2Physical` lane, separate from normal application
data and both isolated account/canary probes; no existing account is migrated.
`eng/Invoke-Did2HttpsAndroidBuild.ps1` reuses the same no-write source and
bootstrap preflight, builds an ARM64 APK with file-based signing inputs and
verifies its explicitly pinned signer. Its dedicated Android package is
`network.xpoint.deep.did2https`; the normal Android system-root configuration
is retained, without the older physical lab CA. The existing guarded Android
installer accepts this lane only with `-Lane Did2Https`, an exact build-artifact
path and APK/signing/source pins; it audits production, older E2E and account
probe packages before and after, and performs no reset or uninstall.
The SDK build uses its disposable debug signer; a separate explicit PKCS12
SDK step signs the final APK with file-based passwords and verifies its pinned
signer before any installation. Private signing arguments never enter MSBuild.
The final signer consumes the already zip-aligned disposable APK and uses the
PKCS12 store password for its same-password key, matching the existing SDK
helper. It does not request two passwords from the original single-line file.
Guarded `Inspect`, `SetName`, `CreateAccount`, `Settings`, `VerifyNetwork` and
`Restart` phases apply only to that installed HTTPS package. They require its
foreground ownership and exact enabled AutomationIds, retain bounded XML in
memory only, and return fixed controls/classifications instead of identity or
recovery text. Restart preserves account and device state.
UI-only harness corrections need not rebuild an identical application: an
explicit `ExpectedApkSourceCommit` may refer to a committed ancestor only when
the exact changed-file allowlist contains that UI script, its composition test
or its two architecture/evidence documents. Install cannot use this exception;
application/build-source changes reject. Results record the APK source and
harness commits separately, retaining the same pinned APK and signer.
Mobile settings uses its actual mobile AutomationId. When the probe reports
a shown keyboard, other control taps reject until the explicit
`DismissKeyboard` phase closes that observed keyboard; this avoids tapping an
IME key through a stale application layout.
`ScrollSettings` swipes only inside one exact owned vertical `Page.Settings`
pane with validated bounds, then requires a fresh observation before a tap.
HTTP diagnostic failures identify one of the fixed account-proof,
network-verification or publication stages using exception
classifications only. They preserve the cause without exposing private request
bytes or exception messages and do not authorize retries or fallback.
TLS failures additionally classify only a closed list of standard
chain-status names from bounded authentication-exception chains; unknown or
oversized messages remain `Unknown`. This display never changes validation.
Actual device observations and remaining gates are recorded in
[`DID2-HTTPS-DEVICE-2026-09-28.md`](DID2-HTTPS-DEVICE-2026-09-28.md).
The MAUI account and transport runtimes are process-scoped DI singletons.
Android may destroy a Window and create another without ending that process,
so Window destruction must not dispose those runtimes or turn a valid account
into an apparent incompatible-account startup failure.
If this isolated probe finds an incompatible pre-clean-break DID2 account,
startup stays closed and offers a dedicated reset button with an in-app
confirmation. The reset invokes STORE-V2's explicit account reset only for the
probe root; it never silently migrates the old 2036-byte credential or touches
the normal `.e2e` and production packages.
On Windows, the probe uses a dedicated private
`%LOCALAPPDATA%/XPointLabs/DeepDid2AccountProbe` root for both account state
and diagnostics, never the normal MAUI application-data root.

The Android physical probe uses `eng/Invoke-PhysicalDid2AccountProbeAndroid.ps1`
with explicit serial, committed SHA, APK SHA-256 and signer SHA-256. A call
without `-Execute` is a read-only preflight; execution installs only the
dedicated probe package and compares production/`.e2e` package metadata
snapshots before and after. An existing probe requires explicit
`-AllowProbeUpdate`; this updates only that package without clearing its data.
The snapshots cover public package identity, install metadata and data inode,
not the contents of another app's private data. The script never clears or
uninstalls either existing app.

The SQLCipher key uses a compile-time lane namespace. Ordinary packages retain
the existing `client-state.sqlcipher-key.v1` SecureStorage slot, while
`DeepPhysicalE2E=true` packages use the distinct stable
`client-state.sqlcipher-key.physical-e2e.v1` slot. A confirmed local-state reset
removes and recreates only the active lane slot; it never deletes the shared
SecureStorage backing file, probes the other lane, or falls back to another key.

Android Release always applies `FLAG_SECURE` to the activity window. The
retired privacy-screen preference is not a Release input; a stale preference
cannot disable screenshot protection. Debug keeps the flag off for local UI
diagnostics and physical test capture.

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
does not register a Session message transport. The clean-break composition
exposes verified-contact DPH2/DAO1 first-send and a manually invoked
self-mailbox receive path. The clean Contacts surface now exposes explicit
secure-channel start, durable direct-text staging/dispatch, and manual inbox
polling; attachment and image composition are not yet exposed. ContactResolve
uses the verified privacy route; there is no direct
HTTPS fallback for MSG-01.
The clean startup opens the DMB1 generation-2 mailbox and direct-event inbox
through the account owner with its own protected SQLCipher key. A successful
responder initial-session saga also replays its staged SessionInit and first
DMC2 into that inbox atomically. The Contacts screen can initiate a verified
first-contact DPH2 and manually poll the self mailbox. Its bounded poll grants
ACK only after all retrieved DPH2/DPE2 entries commit and materialize; partial
batches remain unacknowledged. For a selected verified conversation the screen
can display canonical authenticated MessageCreate events from the local
SQLCipher inbox. Text composition is available for that selected conversation,
but background receive and incoming-conversation discovery remain open.
This is still a DID1 diagnostic composition, not a DID2 release identity or
physical Android↔Windows E2E proof.
The account-owned production API can now preview an incoming DPH2 with its
protected local DPK2 secret and verify the encrypted XPK1/XPC1 transcript
against a fresh initiator DMD1 checkpoint and current placement. This returns
only a verified initial-claim capability; no prekey reservation, session,
inbox event or ACK is created by this step. A production mailbox receiver must
still supply those verified inputs and complete the later durable transitions.

The current-device protected signer now serves both GroupV1 and ContactV1
custody requests, with separate protocol request validation. XRA1 local
authoring composes a verified route proposal, protected PMT2-scoped
metadata-sealing key and that signer. Authenticated startup invokes the
generation-zero publication bootstrap; the Contacts screen can invoke inbound
receive. Neither path is physical device E2E evidence until exercised on two
devices. The clean composition registers one
bounded Registry route-authority client. The account-owned author can submit
its exact XRA1, verify the nonce-bound PMS2/XRC1/XSS1 threshold response and
finish XRR1/XIR1 with the same current-device signer. The bootstrap continues
through durable DCB1/DCR1 publication.
The clean composition also registers a bounded publication-authority client.
Protocol authors its request only through current device custody and
independently re-verifies the returned exact XPA1/XPU1 against current
placement. The account-owned author can authorize an already verified genesis
DCR1/route closure and stages the verified XPU1 for publication. The bootstrap
is fail-closed on publication failure; successful device delivery remains to
be verified physically.

## Historical startup composition — excluded graph

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

## Historical messaging composition — excluded graph

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

The retired direct `/file` attachment transport, its `DEEPATT2` reader/author
and its HTTP factory have been deleted from Shared. The excluded Session HTTP
composition cannot enable attachments from a configured file-service URL.
Its remaining UI file-I/O boundary is explicitly unavailable until the DID2
DAM1 durable blob custody, authenticated routed upload/download and current
MAUI consumers are composed. Protocol chunk-crypto and preparation tests are
not upload, delivery or physical-device evidence. Images and document-mode
behavior likewise require the new platform composition.

The old isolated `GroupText` harness does not cover the current DID2 graph.
Arbitrary contacts, direct text, attachments and groups remain release-unproven
until a retained current Android ↔ Windows physical run exercises them.

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

## Historical push composition — excluded graph

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

## Historical platform integration — excluded graph

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
