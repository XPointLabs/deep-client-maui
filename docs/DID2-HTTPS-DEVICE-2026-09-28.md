# DID2 HTTPS physical checkpoint — 2026-09-28

This records an incomplete real Windows diagnostic, not messaging or release
approval. Registry and seed1–seed3 are production infrastructure; Mr. X permits
testing there until he explicitly reports users exist. There is no remote UAT.

## VPN retry after contact rollout — 2026-10-02

The focused increment adds Shared's typed genesis admission availability
exception and displays only `AdmissionRateLimited` / `AdmissionAuthorityUnavailable`.
The bounded native client tests exercise genuine DID2 request generation and
one actual transport call against controlled 429/503 responses. These categories
cannot prove admission, authorize replay or change reconnect scheduling. A new
physical Windows build `f2566c8` restored the same account/recovery without reset
and performed one registration action. Its terminal result remains
`AccountProof (TransportIo)`, not either admission or proof availability label.
Apphost SHA-256 `3eabdc1e576e773f5040ef71f2a7dec114893eb223c894e61120c660dafcd17d`;
assembly SHA-256 `affbee240cb26def89a7f6bf70a128529189b83bd04e7c795191c5a976f0e6fb`.
Five focused Shared admission tests and 25 MAUI display tests passed; this is
not delivery evidence or a full release gate.

The display also recognizes the framework's `HttpIOException`, which is not an
`HttpRequestException`: `ResponseEnded` and `InvalidResponse` have fixed labels,
all other values map to `HttpStreamIo`. Private exception text is never parsed;
generic IO wrappers cannot impersonate a framework stream error. This adds no
retry, transport downgrade or verification relaxation. A physical result from
the new build is still required before attributing Windows failure to stream IO.

The `8560312` physical Windows build also restored the same account and ended
one action with `AccountProof (TransportIo)`. A bounded QA-process-only exception
trace, retained privately, localizes the failure to socket/TLS frame reads,
H2 response processing and `HttpServiceRequestTransport.ReadBoundedAsync` during
`DeepIdV2DirectoryProofClient.FetchCurrentWithFloorAsync`. It is not a local SQL
or protected-account write failure. The trace does not identify censorship,
Cloudflare or a particular proxy as the source of the disconnect.

Source inspection found `CreateHttpHandler` unconditionally disabled proxies.
The human's Windows VPN is a system proxy, so this public Registry HTTP path
did not honor that configuration. HTTPS diagnostic Registry options now
explicitly opt into the platform proxy through Shared's `UseSystemProxy` option;
the default and the separately selected/pinned ONION connector remain direct.
An owned custom connector and proxy opt-in are rejected together. A focused
real loopback CONNECT rejection test proves no binary body, direct retry or H1
fallback after proxy failure; TLS and signature validation are unchanged.
The `e745b63` Windows build restored the same account and encrypted recovery.
Its first action passed AccountProof but failed TLS at NetworkVerification.
After six isolated public NCQ2 reads succeeded through the same proxy-aware
Shared factory (three fresh-connection and three pooled requests), one further
UI action reached terminal `verified-publication` without reset. That operation
requires the independently verified two-replica prekey completion and owned
permanent-contact publication. This is not peer consent or message delivery,
and does not establish stable automatic reconnect after every TLS failure.
The separate `PlatformChainAccepted` diagnostic is a reject-only direct TLS
handshake; it is not evidence that the failed proxied handshake was accepted.
Apphost SHA-256 `8e11adccfff215edcfff696f67495315d1dc3a13650034b0f55be0ef126d7264`;
assembly SHA-256 `5cb77e89d69c2330a2a3d446a461020527f601c227fd5f770107959b5e6b251f`.
15 focused Shared transport/proxy tests and 43 MAUI composition/display tests
passed, with unchanged assertions on TLS, H2 and byte/deadline boundaries.

The signed `503505f` Android APK (Protocol `7c513f1`, Shared `9bac6e7`;
APK SHA-256 `5c5ddd839e711bd2f89e0f77b7e647ff88e7bd4a2111d2a418eb5168341d8708`)
identified the retained-account rejection as **XRA1 / Expiry**. This is a
signed route lifetime rejection, not evidence of censorship. Under the human's
standing QA reset authorization, only the dedicated HTTPS QA account was reset
through its owned confirmation UI. A new offline one-click account retained
encrypted recovery; one VPN-enabled network action then reached the terminal
`verified-publication` state. That complete diagnostic operation requires the
verified two-replica prekey completion and owned permanent-contact commit before
success. Protected production/E2E/probe package snapshots remained unchanged.
It does not prove Windows peer lookup, Hello/Accept, text, attachments or groups.
The expired-intent renewal gate in
[DR-0051](../../docs/survival-program/decisions/DR-0051-owned-permanent-contact-client-entry.md)
remains open; the QA reset is not a product recovery implementation.

Post-reset and post-create immediate UI readback encountered a transient window
replacement. Independent subsequent inspection established the welcome screen,
then the created account. The harness now retries only those two known readback
failures once, with a new foreground-owner check, never repeating the mutation.
Ownership loss and repeated failure remain terminal. Focused functional tests
exercise this observation-only retry; no account/network retry is authorized.

Windows rebuilt `503505f` and restored the same account without reset.
Its AccountProof action still ended in `TransportIo`, as did the earlier
`e1090b5` running build. Android's successful result cannot establish a Windows
transport fix. The next display-only increment separates existing typed HTTP
boundary errors from generic IO; it never reads
private exception text, changes TLS/H2/byte limits, retries a request or reports
delivery success. Its freshly rebuilt Windows diagnostic must identify the
actual failure before any product correction is claimed.

The locally built `8937d0a` Windows diagnostic was launched through the supported
window API, restored the same QA account/recovery and performed one network
action. Terminal inspection still reported `AccountProof (TransportIo)`; none
of the four typed HTTP boundary labels appeared. Apphost SHA-256:
`edc4731fca9f881e5ad0aae4a64f875f2f84bd1e3864e80d754914c6faa9aa74`;
assembly SHA-256:
`46c679c919db9c349026eea20e1eb490fb8fa911c29081f966bd319d0032dd4c`.
The source also contains an untyped admission-authority 429/503 IOException;
the observation does not distinguish it from generic stream IO. An origin-only
bounded log scan produced no matching directory entries, so it supplies no
attribution. Do not declare this a VPN, WAF, HTTP-body or quota defect yet.
The 25 focused diagnostic/readback tests pass; this is not the full release gate.

The human enabled VPN on the physical Android device. One guarded network action
still failed, with no stage wrapper in the sanitized UI result. A read-only,
foreground-owned status inspection matched that exact fixed error to
`DeepIdV2RouteContext.Covers` in Protocol: a route artifact does not cover the
complete authenticated time interval. This does not identify which artifact,
not-before versus expiry, or censorship as a cause. After the coordinated contact
rollout, another guarded action and terminal inspection reproduced the same
fixed coverage error. Local recovery remained retained; protected packages were
unchanged and no account reset was performed. Neither action proves delivery.

Deployment evidence and preserved custody belong to the
[Registry operator checkpoint](../../deep-registry-api/docs/DID2_DIRECTORY_CANDIDATE.md#private-did2-contact-coordination-deployment).
All three nodes now have the closed DID2 coordination/resolver/claim profile and
readiness 200; mailbox authority remains unready. The next diagnostic must isolate
the failing artifact/time relation without relaxing TLS, signed-time coverage or
protected retry custody. Contacts, text, attachments/images and groups remain
unproven physically; no release approval is implied.

The subsequent diagnostic build uses Protocol's closed route-time artifact and
boundary labels. Android inspection emits only those two allowed fields, not
interval values or raw error text. Unknown labels remain unclassified failures;
neither a label nor an enabled button can establish publication success. The
cryptographic predicate, wire and protected retry state remain unchanged. No
account reset is required by this diagnostic change.

## Current committed contact candidate — 2026-10-02

Latest physical follow-up used the `e1090b5` HTTPS APK. After the operator enabled
VPN on Android, the network action passed account proof, signed network verification
and local pre-key staging, then reported `PreKeyPublication (TransportIo)`.
The guarded phases preserved all three protected packages. No verified publication
pair or contact/message/attachment/group delivery is established by this result.

The diagnostic distinguishes code-owned unknown ONION completion, rejected
reply, timeout and transport failures from generic I/O. These are display-only
categories, not retry or delivery authority. It retains the original exception
privately without displaying messages, identifiers, URLs or payloads. Focused
classification tests pass 18/18, including the typed directory 429/503 categories
`ProofRateLimited` / `ProofAuthorityUnavailable`. These do not introduce retry or
change admission quotas.

The signed `aa320f7` diagnostic APK built with zero warnings/errors; SHA-256
`d37080cb210d31e403cc112a74c23416d04295795fae6808cb85ec1a63bf50d6`.
The guarded USB update preserved all three protected packages; Settings restored
the account and retained encrypted recovery. One network action then completed
with `PreKeyPublication (OnionCompletionUnknown)`. A closed 180-second log window
was empty before this action; after it, seed1 reported one terminal
`proof-rate-limit`. This establishes a server-side proof admission failure in
the publication window, not a verified receipt pair or message delivery.
All three nodes subsequently returned readiness 200 / ONION ready; retained
health samples included temporary refusals. A currently green health is not
evidence that the publication succeeded.

Next isolated slice: reconcile the fresh-proof acquisition count of the complete
publication with bounded Registry admission and replay-ledger capacity. Do not
blindly increase quotas, reset nonce/floor custody, reuse expired authority or
interpret unknown completion as rejection before forwarding.

The signed `fdab89c` APK was built with zero warnings/errors and installed through
the guarded update, retaining the account and all protected-package snapshots.
APK SHA-256: `2336e670e46c6cb9328ff03bf54c2f91e0e7363c96be8e80a46a97c1d739c0b3`.
One attempt reported `AccountProof` / `RevocationStatusUnknown`; a subsequent
bounded attempt with VPN enabled passed that stage and again reported
`PreKeyPublication (TransportIo)`. No TLS/revocation checks were disabled.

Production XNode `6dbf0fb` was installed on the same three nodes through the
supported installer. Registered Ed/BLS keys remained unchanged; all three
readiness checks returned 200 with ONION ready. Its closed terminal diagnostic
has a per-process rate bound and never records exceptions or request identifiers.
37 focused native tests pass; no physical publication/delivery is inferred from
the installation or from an empty diagnostic log window.

Owned settings scrolling uses a pane-local third, with an explicit upward phase,
so a full-screen fling cannot skip the network action and force a reset/relaunch.
Both directions require the observed exact package, scroll pane and bounded
geometry; subsequent clicks still require fresh enabled selectors.

MAUI source `1261920b8e75a6262b9a38e05f6964609add6b37` built through both
supported strict, clean-source scripts. Windows apphost SHA-256 is
`e8327f36c4c5956444b7f49180467d84490643ddeb4d7a484d7fe783f9b0d0aa`;
assembly SHA-256 is
`8bdd17545ec01f0e08b2cdf3fe816bc37b9fcc2c877d260d28e8854e593d5175`.
Its actual window reached the explicit incompatible-isolated-account reset
confirmation; deletion had not been performed at this checkpoint.

Android build completed with zero warnings/errors. APK SHA-256 is
`67168e439384fae9737e398bdbbc6ea1f17220d50bc198b49cdf4807d812cd9a`,
with the production-custody signer already recorded below. The guarded USB
update installed and launched only the dedicated HTTPS package; all three
protected packages retained equal before/after path and stable metadata hashes.
This is build/install evidence, not contact, message or account verification.

The Android harness now recognizes the isolated startup recovery control and
its exact, UID-owned confirmation separately from the settings reset dialog.
Both final reset phases require the explicit isolated-reset switch; no package
clear, uninstall, account migration or protected-package mutation is available.
UI-only harness revisions can inspect an already-installed, pinned ancestor
APK only under the existing four-file whitelist, never install an older APK.

On the actual USB device, BeginIncompatibleReset displayed the exact owned
startup dialog. The next confirmation phase rejected because that dialog was
no longer present; no blind retry was made and automated deletion is not
claimed. Fresh Inspect observed empty onboarding. Name/Create was performed
once; its immediate hierarchy capture failed, so the action was not repeated.
Fresh Inspect then observed the workspace, and Settings observed both the
current address and retained encrypted recovery without exporting either.
Force-stop/relaunch followed by Settings retained both. The network action
failed at `DID2 AccountProof failed (TransportIo)`; no independent proof,
publication, contact or message delivery is established by these observations.

- MAUI code `2e8eae6bdd68bb7b297a5543be7d202706a49042` passed 21 clean-account
  tests, 119 smoke tests and the selected seven-file secret scan. Actual MSBuild
  admission guards accepted the complete DID2 build inputs and rejected retired
  mailbox/PMA inputs and a zero network ID. Script preflight passed before build.
- The supported DID2 Windows script built an unpackaged ARM64 Debug candidate.
  Apphost SHA-256: `808c9dffcfc8f3f2326c4a00d7eb80341aeb6792b49c8d79a515590106124a51`;
  assembly SHA-256: `26746613e04679e09d11d8ffafff042c08b4da5a57f79f2662be66d85194292b`.
  No installer, package deployment or Release signing was performed.
- The real native window displayed onboarding. A separate test account was
  created by filling the name and pressing Create; settings showed retained,
  encrypted recovery without revealing or copying it. Account and device state
  files exist in the dedicated per-network physical lane; normal application
  data and both older probes were untouched. The app was then closed through
  its UI and the next committed candidate reopened the same account with its
  recovery phrase still hidden and retained.
- The explicit HTTPS action failed with a request-send error and retained the
  account. The independent Registry floor advanced from generation 17/tree 6
  to 18/tree 7, consistent with accepted admission, but not independent client
  proof completion or pre-key publication. An identity-neutral external HTTPS
  closure query returned 200 and 11,300 bytes; it is untrusted distribution,
  not device authority or a verified two-replica receipt pair.
- Diagnostic HTTP failures now carry only fixed operation stage and exception
  classification, preserving the inner exception without exposing its private
  message, URL or request bytes. This does not add a retry, fallback or bypass.
- Candidate `03bb8404d049236bea61fa8a72ace587bb8063e9` rebuilt successfully
  and reopened the same physical account. Apphost SHA-256:
  `2c5152b0d6d487abb981ccbf33eb792c8bf88e532ba30bf737f7cec6a4b20ec2`;
  assembly SHA-256:
  `b9ef87ab8a460d576b65fd63cc5cd6f694f73057991b9a84b0b909a6fdd1d511`.
  The actual window reported a bounded HTTP timeout on the next network
  action. The account remained visible; neither complete proof nor publication
  is claimed. Timeout failures now receive the same fixed-stage classification
  as request failures; HTTP deadlines and TLS checks remain unchanged.

Next: locate the exact failing stage, finish independently verified two-replica
publication, then compose DID2 contacts/DPH2/receive and verify Windows↔Android
text, file/image integrity and groups. None of those gates is closed here.

## Android diagnostic preparation

Code `915d05ca0fd3ee9aa7adef10edb7aa2defe790d3` compiled the dedicated ARM64
HTTPS package with zero warnings/errors. Its new signing step initially failed:
two explicit file-based password requests cannot consume the original
single-line password source. The existing `Invoke-ExplicitApkSigning` SDK helper
successfully signed the zip-aligned disposable APK using the PKCS12 store
password alone. Independent signature and 16-KiB-page alignment checks passed.
The actual production-custody signer is
`bf8abed56e852d0902796f9e0131789f188688784a07ad516760f127684204c2`,
not the older debug probe signer. No signer-mismatch check was bypassed.

The guarded installer passed preflight and installed/launched only
`network.xpoint.deep.did2https`; APK SHA-256:
`9829d57d070023bc4506b4d0edb00b50410fd078408510e6646aca57185c4c6d`.
Before/after path and stable metadata hashes matched for production, older
E2E and account-probe packages. No account was reset. This is installation
evidence only, not Android account/proof/message evidence.

The first guarded UI inspection rejected foreground ownership because the
Samsung `dumpsys window windows` subsection omits `mCurrentFocus`. Independent
read-only full-window and resumed-activity checks both identified the exact
HTTPS package in front, with no keyguard or captured runtime exception.
The harness now queries full `dumpsys window`; the signer now mirrors the
existing working helper and uses an aligned input. Clean tests passed 22/22,
smoke tests 119/119, and the revised seven composition guards passed. Actual
UI phases on the corrected harness remain to be completed.

## Follow-up: physical accounts and repeated HTTPS responses

Windows candidate `8cb27125509461e10f3ae2bb45579b1933166dd0` completed
AccountProof, then failed at NetworkVerification. A private, bounded runtime
trace showed HTTP 200 for admission, first proof and public closure. The second
proof declared 11,224 bytes but left its final 1,172 bytes unread until the
existing HTTP deadline. No account/SQL lease timeout was observed; changing
account-lock behavior would not address this evidence.

Shared candidate `2c5c387c3d5547bff91144fec0f147e6ed0d587c` selects exact HTTP/2
for public binary requests, with no HTTP/1 downgrade or application retry.
Focused transport/proof/closure tests passed 14/14; the full Shared Release
gate passed 171/171. These are local tests, not device delivery evidence.

MAUI candidate `167280c82dff342b0368c5ae142a07f47d2f2062` includes the closed
TLS classifier in the actual DID2 compile graph (the first attempted build
detected a missing include). Clean tests passed 25/25 and smoke tests 119/119.
The supported Windows build reopened the existing physical account. Apphost
SHA-256: `5e1bea9feb73fa0b00b77409ddd2fd538fcff57d83a1f70b56ba1436aced2aef`;
assembly SHA-256: `6401ed0986fa9be3d81d600741e2e4c1ad20ea8002c73591c0dcbb37a6be66a1`.
HTTP/2 did not resolve the failure: second-proof headers returned 200, followed
by an aborted body read with a socket-close exception chain. A separate
identity-neutral three-request closure diagnostic reproduced the second-request
failure on the reused connection, including with plain HttpClient. Three fresh
clients received complete untrusted closure responses in 376/234/231 ms.
This narrows diagnosis to connection reuse along the real HTTPS path; it does
not establish which runtime, network or proxy component caused the close.
IPv4-first acquisition also reproduced the reused-connection failure. The
next diagnostic candidate requests fresh public-service connections through
the existing positive lifetime option. It does not retry a failed operation,
downgrade HTTPS/H2, change selected-entry transport, or relax TLS/signatures.
Its physical result remains to be observed; this is not a release-wide fix claim.
The `b5fcaea52282c5a7e58a1ff206c54fdab94ff2af` Windows build reopened the
same account and completed both HTTP proof reads using fresh connections.
It then rejected NETCODEC policy lineage (stale-or-fork), before a verified
publication pair could be produced. Review found full genesis-to-successor
history was passed as an incremental successor suffix on repeated live mint.
The Shared correction uses the existing DR-0012 full-history boundary; it must
pass local and device checks before this gate can close. No floor was reset.

The next Android diagnostic adds an independent five-second TLS observation
only after an unclassified secure-connection failure. Its certificate callback
always returns false, including for a platform-valid chain; it cannot send an
application request, admit an account or provide transport authority. Only
closed chain flags are retained, never certificates, names or exception text.
The actual request path still uses unchanged platform trust and online
revocation. This is diagnosis, not an alternate trust path or TLS success.

Android candidate `31bda1a599167cb5a53ad7d9698424f290b98583` was built and
signed through the corrected supported script. APK SHA-256:
`3a4f72523a99be50ae57c530c45928d6d16679ca29b06ea0230640bed84f91e7`.
The real device created a disposable account by name/Create; settings showed
retained encrypted recovery. Force-stop/relaunch preserved the account and
retained phrase. Harness changes added the actual mobile settings selector,
an observed-IME dismissal/guard and bounded scrolling inside the owned settings
pane. No retired identity or transport fallback was introduced.

The same `167280c` candidate built Android with zero warnings/errors and the
production-custody signer above. APK SHA-256:
`d0214cc08afbdc4910ef943ab846dd55bfe677d2c6a93f0223567f5c27d811d6`.
Guarded update preserved the existing account and retained recovery. Before/after
metadata and path hashes matched for all three protected packages. Network
verification still failed at AccountProof with SecureConnectionError /
AuthenticationException / Chain Unknown. No certificate or revocation check was
disabled. A transient post-action UI hierarchy capture failure was followed by
a fresh Inspect phase; the action was not blindly repeated.

Registry continued the existing signed history to head generation 20/tree 7
through ordinary renewal. Protected time was renewed with strict expected-hash
CAS to generation 11 after independent UTC/no-reboot checks. Neither genesis nor
account history was reset. Authenticated two-replica publication, contacts,
messages, files/images and groups on physical devices remain unverified.

## Full-history correction and physical publication boundary

Shared `14d7a9dbc0421f8b28b7218f0c3dbf404a247ce0` passed the complete Release
gate (172/172), including repeated full signed successor history. MAUI
`343ac23e780c21867caa588c5224e621693def6d` passed 119 smoke tests and built both
device candidates. Windows apphost SHA-256:
`232aa583aae50c9881fdfbe2fdd7a37e91fcd52f73f2d6990f4aca385f227832`;
assembly: `e7884598ef7cba9af4f994aab05c81ad3a25fcf0fb592dbbbc86a4ec88989fdf`.
The real retained account passed network verification and reached pre-key
publication, then received a canonical rejection before forwarding. A later
attempt failed locally with `entropy-ledger-failed`: the two rotating ONION
marker slots incorrectly used the create-only secure-storage write API when
reusing a slot. This does not establish an authenticated receipt pair. Exact
slot replacement is being corrected with atomic expected-value CAS, not
delete/recreate, custody reset or unconditional overwrite.

Android signed APK SHA-256:
`55cca6c780a20cdfa6a61b4175d7d7653ee7ed94f7cfdd46f43333cd41ecbcdf`.
Guarded update preserved the account and retained recovery; all three protected
packages remained unchanged. Fresh Inspect after a transient hierarchy-capture
failure reported `RevocationStatusUnknown` from the always-rejected TLS
diagnostic. The current public Registry edge certificate is issued by Google
Trust Services WE1 and advertises a CRL at `c.pki.goog` over HTTP. The next
diagnostic build gives only that exact host (no subdomains) a cleartext resource
exception to test CRL reachability. Default cleartext remains forbidden, trust
anchors remain system-only, all application service origins remain HTTPS-only,
and revocation checks remain enabled. The resource is excluded outside the
explicit non-Release DID2 HTTPS physical lane. This is an isolated diagnostic
change, not proof of the failure's root cause or of Android TLS success.

Registry renewed its existing head to generation 21/tree 7 without resetting
genesis or account history. Durable client DNH2 custody for advancing a changed
tip after restart, the actual ingress rejection, two-replica publication and
the contact/message/file/image/group vertical remain open release prerequisites.

## Atomic marker correction: committed device candidates

Shared `9485198325fa38a66993d76cf8fe16442a80ddbd` passed the complete Release
gate (181/181), including repeated slot replacement, independent-writer CAS,
rollback and marker-before-SQL crash rejection. MAUI
`c88f8cd4dc67b22fb4efadf61d4dc145271dda24` passed clean tests (35/35) and smoke
tests (119/119); both supported device builds completed without warnings/errors.
Windows apphost SHA-256:
`a93d50b3ec9f7ddb52d76c6261ea7269d125fbcbb84520fa188ed9ac147659bf`;
assembly: `295fd829c5c9dc82eb315a48d19e9c238bfa8857276e78abec313986e9b7d8e1`.
The retained Windows account reached the real ingress again; its canonical
closed error class is `Unavailable`, with `BeforeForward` certainty. The prior
local entropy-marker error did not recur in this run. This is not a publication
receipt or confirmation that the transport is ready.

Android APK SHA-256:
`69950838d451b51c87693840c6384b5711352a8bb7131536ff9e870333d9ee81`.
Guarded update retained the account and encrypted recovery; the other three
packages' before/after hashes remained equal. Two bounded verification actions
followed by fresh Inspect reported `DID2 AccountProof failed (Timeout)` rather
than a successful TLS/proof exchange. The exact CRL-host diagnostic exception
has therefore not established either the root cause or a successful workaround.
Do not extrapolate Android trust or message delivery from the build results.

Read-only inspection found all three actual production XNode containers running
but unhealthy. On the first seed, readiness reports `privacyRouting=unavailable`;
required terminals remain ready. Diagnose the live receive-authority/proof path
before another publication attempt; no floor reset or permissive fallback was
performed. Protected Registry time advanced through strict expected-hash CAS
to generation 12 after independent UTC, synchronized-clock and no-reboot checks.
Directory genesis and account history remained intact. Physical two-replica
publication and the contact/message/file/image/group vertical remain unverified.

## Fresh-account diagnostic and closed result reporting

After Mr. X removed both disposable accounts, the physical Windows and Android
HTTPS candidates created new accounts with name/Create. Encrypted recovery was
retained; neither phrase was revealed. These are device account-creation results,
not message-delivery evidence. Both candidates use Shared `7c5ee552` with the
corrected pre-key authoring bounds; Android APK SHA-256 is
`03aff095deca5695106e68e35f5002b08eb53681981b5328751cc0684f34e9f6`.

Further verification stopped at unavailable directory authority. Production
inspection identified checkpoint coverage and proof-budget pressure from idle
XNode polling. The continued checkpoint chain and service observations belong
to the [DevOps floor runbook](../../deep-devops/docs/DID2_FLOOR_PRODUCTION_CANDIDATE.md).
No signed staged inventory, account floor or genesis was silently replaced.

The Android harness now reports a closed `networkOutcome` separately from
`stageFailure`: a null wrapped-stage failure is not success. Fixed categories
distinguish verified publication, in-progress verification, unavailable proof or
admission authority, ingress rejection before forwarding, aborted requests,
unclassified redacted failure and no observation. UI error text is not exported.
The harness change passed all 35 clean tests and eight synthetic classifier
cases; those synthetic results alone are not physical publication evidence.

## Authenticated physical publication and restart

After the production proof-budget rollout recorded in the DevOps runbook,
Windows HTTPS QA completed verification at approximately 17:49 UTC; Android
HTTPS QA completed it at approximately 17:55 UTC. The Windows success status
and Android closed `verified-publication` outcome follow the account-owned
completion path, which verifies both selected XIC1 replica signatures and
records the exact pair durably before returning. The exact source used by both
devices is
`753ab13d87c93dbb45061cd19b7d66d585dd284b`.
Android used the APK hash above. Windows apphost SHA-256:
`701a8d9b513729c2a8385f3e73848f20a4d824e6678e8748f00159d5578b532e`;
assembly: `d3e7116cf81f5b409bc42c595f1009243731cd0e8f1554870755f93c7ed8c63e`.

Both diagnostic applications restarted without reset or another creation.
Windows retained the same observed identity; both settings panes showed the
retained account and hidden encrypted recovery. Recovery phrases were never
revealed. Android's post-restart network status was `not-observed`, not a new
publication success. The other Android packages were not reset or replaced.
No contact acceptance, XPK1 claim, DPH2, text, image/file or group delivery was
performed in these runs. Those physical release gates remain open.

## Protected completion reuse on both updated devices

MAUI `a4f56b3ae09bc6094a1c71b644597160b2025d06`, with Shared
`c5b97ef406ee9f949a80c31e06d7d5a484b5057d`, built both diagnostic clients.
The Android build completed with zero warnings/errors and the same verified
production-custody signer above. APK SHA-256:
`a6cec3d5f0db89cc5869f54700667fd7d0298155e0beda7990472fc36959ae1f`.
Windows apphost SHA-256:
`604f40af3de559c8a47428d1019484f50b51aeb919726c1dd9a7d3a398289503`;
assembly: `f5d8fb5e11a520e92d4a91d981f239387232fc044776875f324b8ff350d50f6d`.
Clean tests passed 35/35 and smoke tests passed 119/119 against the new Shared
source. These remain diagnostic Debug builds, not release packages.

The old isolated Windows application was closed through its UI; the new local
build reopened the same observed ID and retained hidden encrypted recovery.
The Android supported installer passed preflight, updated only its dedicated
HTTPS package without reset, and verified unchanged path/metadata hashes for
the three protected packages. Settings showed the retained account and recovery.

At approximately 18:57 UTC Windows network verification completed successfully;
at approximately 18:58 UTC a fresh Android Inspect returned
`verified-publication`. Two earlier Android hierarchy reads were unavailable
after the one verification tap; the tap was not blindly repeated. The updated
account-owned source reauthenticates an existing exact protected XIC1 pair with
fresh account/device and network authority instead of redispatching inventory.
This tests completed-operation reuse after process/build replacement, not new
replica retention or current service availability. No signed interval is extended
and no account, staged inventory, registered node key or rollback floor is reset.
Contacts, atomic V2 claim, DPH2, text, files/images and groups remain unverified.

## Follow-up on 2026-09-29: current proof unavailable

The existing Windows HTTPS QA account was preserved, not recreated following
the delayed account-creation confirmation. One fresh network action completed
with `proof-authority-unavailable`; the local account and hidden encrypted
recovery remained present. No reset or phrase disclosure was performed.

Android `VerifyNetwork` passed preflight, but its bounded execution could not
capture UI hierarchy. The action outcome was initially unknown and the tap was
not repeated. A subsequent read-only `Inspect` phase succeeded and observed
`proof-authority-unavailable`, not a verified publication. Path and stable
metadata hashes before/after matched for production, E2E and the separate DID2
account-probe packages. Account/recovery fields were outside the scrolled view;
their absence in this observation is not evidence of deleted account state.

Both devices still use the previously recorded diagnostic builds; they do not
contain the new account-owned durable DNH2 implementation. The current signed
view expiry and independent service observations are recorded in the
[DevOps runbook](../../deep-devops/docs/DID2_FLOOR_PRODUCTION_CANDIDATE.md).
No current physical contact, claim, handshake, text, attachment or group
delivery is established by these actions.

## DNH2 builds after the authenticated operational successor

MAUI `d1166446dd41f1df6b0592a29a81af53159d8b16`, with Shared
`72a78b7217a88232496c1749adfe9841641b395c`, built the dedicated Windows
ARM64 and Android ARM64 diagnostics. Clean tests passed 35/35 and smoke tests
119/119. Shared owner CI run `36529522694` succeeded. Windows apphost SHA-256:
`cf4b53e8733250f75366160b569ff7060c07eb7439f2726e14f8fbdabf01f69e`;
assembly: `fc51e73ca332899b0a96e3c78fa2f40c7e55e643336c6ebb3fedd29fe56b68d7`.
Android APK: `1224e9d5ed32d5b4c5b38e90547df684289451d6f57afefdd489312f8d4c0e79`,
with the production-custody signer recorded above and zero build warnings/errors.
The guarded update changed only the HTTPS package; path/metadata hashes of
the three protected packages remained equal. These are Debug diagnostics,
not Release packages or publication approval.

The old Windows projection-only custody was rejected as incompatible, without
migration. After action-time confirmation, only this isolated account was reset
through the application UI. Mr. X created the replacement account; subsequent
inspection showed its profile and hidden retained recovery. It was not created
again after his confirmation. Android was observed at fresh onboarding and,
later, with retained recovery; an unavailable SetName hierarchy was not blindly
retried. Fresh verification reported Android PreKeyPublication timeout and
Windows AccountProof timeout or aborted response. No new XIC1 pair is claimed.

A bounded Windows runtime trace subsequently recorded an HTTP 200 proof
response declaring 18,994 bytes, then an incomplete body and connection abort.
Identity-neutral controls reproduced second-response truncation with both
plain .NET HttpClient and Node.js on this workstation. Three Node.js requests
on one connection from the Registry host completed and hash-matched the
independent 14,449-byte public NCP2 export; three fresh local Node.js connections
also completed. Fresh .NET connections passed with TLS resumption enabled or
disabled; disabling resumption alone on a reused connection still failed.
Local TLS 1.2 and IPv4-only controls also reproduced truncation. This rules out
a .NET-only explanation for that control, but does not identify the failing
network/proxy/runtime component or prove device publication. No platform trust,
signature, deadline, exact H2, registered key or protected floor was weakened.

The diagnostic now wraps IOException with a closed stage and `TransportIo`
label. The original cause is retained internally, not copied into UI/evidence.
Unknown stage strings reject; this change neither retries nor completes an
interrupted operation. Clean tests passed 42/42 and smoke tests 119/119 after
the correction; these are local evidence only. Current-history
restart, live claim, DPH2 and Windows↔Android text/files/images/groups remain open.

### Corrected diagnostic on the physical devices

Source `00a089fbd7030f9e83f653c4660bfffe31f14447` built both actual DID2 graphs
through the supported scripts. Windows apphost SHA-256:
`8e51282b735126e1ff8ca6055a3c4ccec05c895712a99cc1fcf1702a197e3ec8`;
assembly: `092147bf31cd0d5ad51acb2f3478f4af3922b85bb0079c83741b586aaa4ca8cd`.
The former isolated QA was closed through its UI. The new process retained
the same observed profile/identity and hidden recovery without reset or creation.
One network action reported `DID2 AccountProof failed (TransportIo)`, demonstrating
the closed error wrapper in the real application, not successful proof completion.

Android APK SHA-256:
`2ec49e4814b7fa3842b056393dda6a70af8e89d1ae4bd806942e6957ada43189`.
The build completed with zero warnings/errors and the same pinned signer.
Install preflight passed; the guarded update changed only the dedicated HTTPS
package and launched it. All three protected packages' path/metadata hashes
were unchanged. Settings showed the retained account and hidden recovery.
Only wireless ADB was observed; USB transport is not confirmed. Connectivity
inspection identified Wi-Fi as the default route, even though cellular was also
validated. No network/security setting was changed by automation.

At approximately 08:07 UTC, a fresh read-only Android Inspect observed
`verified-publication` following one bounded verification action. Its immediate
post-action UI hierarchy was unavailable, so the tap was not repeated. This
establishes successful account-owned completion: independently authenticated
exact XIC1 signatures and protected pair custody, not contact/claim authority.
The observation alone does not distinguish a first dispatch from an exact
interrupted-operation retry or completed-pair reauthentication.

The supported Restart phase then stopped/relaunched only the diagnostic process,
without reset. Settings retained the account and hidden recovery. A single
post-restart verification action again had an unavailable immediate hierarchy;
the subsequent Inspect at approximately 08:13 UTC observed `verified-publication`.
The now-completed protected pair is reauthenticated against fresh account/network
authority without inventory redispatch. This is same-history process-restart
evidence, not the separate changed-signed-tip restart gate or proof of present
replica availability. Protected package snapshots remained unchanged.

A further identity-neutral control used the existing digest-pinned official
Node 24 image in local Linux ARM64 Docker: three responses on one H2 connection
completed and matched the independent NCP2 export. The Docker endpoint was a
local named pipe, not the remote production host. Native Windows ARM64 controls
still truncate. This further narrows the Windows-dependent path; it does not
identify a faulty driver, proxy or system component. No system setting changed.

## Resumed physical checkpoint — 2026-09-30

Mr. X resumed physical contacts/messages/attachments/groups testing and explicitly
allowed updating production for it. This checkpoint does not close that scope.

- Shared `6682437` adds an internal exact V2 claim transport boundary. Four
  focused tests cover eleven success/refusal/substitution/cancellation scenarios;
  they do not supply a durable caller journal, peer closure, DPH2 or device evidence.
- The first fresh HTTPS Android build failed because reconnect referenced a
  connectivity adapter omitted by the clean compile allow-list. MAUI `d1689e6`
  explicitly includes that platform primitive without restoring the retired
  runtime. Clean tests passed 47/47 and smoke tests 119/119.
- The supported scripts built Windows ARM64 and signed Android ARM64 diagnostic
  candidates from `d1689e6`, not Release packages. Windows apphost SHA-256:
  `ada4fd15475ed6d1f42f9fa8ea9534e1d8fea4ed364dc04d793203bff61cb0c2`;
  assembly: `084e5e4ef7cf92d1208a87994cfa2c55a3a5a77027b836701410e1b4e942d385`.
  Android APK: `4d6663df7265a5e2c79e6c1861c4bdddc12963610e7191e4fe9fbcd9cf069359`,
  with the production-custody signer recorded above and zero build warnings/errors.
- The guarded USB update changed only the dedicated HTTPS diagnostic package.
  Before/after path and stable metadata hashes matched for all three protected
  packages. Both real devices retained their QA accounts and encrypted, hidden
  recovery phrases; no account reset or phrase reveal/deletion occurred.
- One network action on each real device ended at `AccountProof / TransportIo`.
  Android's immediate post-action hierarchy was unavailable; the action was not
  repeated, and a later read-only Inspect observed the failure.
- An independent public HTTPS check returned **503** for production DID2 readiness
  and **200** for staking. This establishes production DID2 unavailability, not
  the exact cause of each wrapped client exception. The local retained DEV
  verification reported current Registry proof and three verified ONION nodes.
- New source-bound XNode/Registry image workflows built but failed GHCR upload
  with `permission_denied: write_package`. No new image was deployed, no old
  floor/key was reset, and no GitHub Release or main merge was performed.

Next: restore authorized image publication and live production authority, then
complete account-owned durable V2 claim/peer-closure/DPH2 composition before the
bidirectional text, restart/dedup/ACK, file/image and membership-change gates.
The current UI still explicitly marks those messaging functions unavailable.
