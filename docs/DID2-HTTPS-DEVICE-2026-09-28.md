# DID2 HTTPS physical checkpoint — 2026-09-28

This records an incomplete real Windows diagnostic, not messaging or release
approval. Registry and seed1–seed3 are production infrastructure; Mr. X permits
testing there until he explicitly reports users exist. There is no remote UAT.

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
cases; it does not establish a successful physical publication. The required
next result remains a verified durable two-replica receipt pair, then the
Windows/Android contact and message vertical.
