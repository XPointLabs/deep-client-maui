# S05 — bounded business resource observation

Windows23's fresh contact command returned OutcomeUnknown with cancellation
inside protected directory-floor validation; Android39 Retrieve also returned
OutcomeUnknown. VerificationTotal already measures background registration work,
but it does not measure the foreground business command. Those counts cannot
establish which part of StartContact is expensive.

The existing QA-only observer now surrounds the actual conversation runtime
command with one of six fixed totals: ContactStart, ContactStop, ContactAccept,
TextQueue, TextRetry and MailboxSync. Output remains elapsed milliseconds,
actual Protect/Unprotect call counts and protection milliseconds. No addresses,
slot names, keys, event/message contents, exception messages, activity context
or outgoing trace headers are added. Original authority, callback, cancellation,
request/ciphertext and deadline behavior are unchanged; observation cannot turn
failure into success or infer noncommit.

`artifacts/s05-business-work-20261017/focused-01/`:14PASS/0FAIL/0skip/native0,
Windows PowerShell5.1/SDK10.0.301, no compiler diagnostics. Covers all six closed
business labels, async context isolation, original cancellation token/exception,
throwing observers, parent restoration and rejection of unknown/private labels.
This is diagnostic source evidence, not physical or canonical full acceptance.
Matching builds and same-account observations are required before selecting
the next performance correction. Do not replace an expired/unknown intent,
increase a signed lease, cache plaintext or bypass a protected-floor check to
make a timing test pass. The existing normal owned stop remains a separate
explicit operation and does not revoke possible remote effects.

## Matching retained-account observation

Windows24 and signed APK40 compiled from MAUI `826d337`, Shared runtime
`5fa8789`, Protocol `2bb61d0`; both build native0, no compiler diagnostics.
Windows apphost SHA256:
`7465964b6b60752f205cc9ad2c0613b7777cc59964f4978f0bf6635e97430b3e`.
Android APK SHA256:
`6751aef04d6d74312c0960aa26d4cfa7d77e763069065dbd49f6ba4cb7d0c815`;
signer SHA256:
`bf8abed56e852d0902796f9e0131789f188688784a07ad516760f127684204c2`.
Android dry143/update144 retained the profile; Settings145 confirmed account
and encrypted recovery. No account reset, owned stop or new contact intent.

Windows first Add restored the pending command; its explicit exact retry
ended at19:20:44Z with cancellation. ContactStartTotal120031ms,
3 Protect/1738 Unprotect, protection78880ms. The retained intent remains in UI.
Closed stack locates cancellation in application audit opening before Initial
Store evidence retention; it does not establish all earlier work or noncommit.
Android Refresh147 followed by independent Inspect148 ended in messaging
error. MailboxSyncTotal40780ms,2 Protect/304 Unprotect, protection26971ms;
the closed failure is Retrieve OutcomeUnknown in managed ingress forwarding.
No contact reception/text/attachment/group acceptance is claimed: physical0/4.

The measured resource cost selects a cache-free
[owned-read correction](../../deep-client-shared/docs/testing/s05-owned-read-batch-2026-10-17.md).
Source-focused checks do not replace matching device verification or canonical
full/package qualification. Diagnostic totals alone do not identify every
remaining transport failure.

## Owned-read correction retest and current refusal

Windows25/APK41 contain Shared `7d64fe7`; MAUI `fa927e2`, Protocol `2bb61d0`.
Both build native0 without compiler diagnostics. Windows apphost SHA256:
`115221707b2c44376a4f9a11ddcea45bd99b7caaaf1888d0df6f9a75b1e2175d`.
Android APK SHA256:
`500ba1f13fa880068a87ac8d2e257d5485bf2fda129a2aae80782f06921cd668`;
signer unchanged. Dry149/update150 and Settings151 retained account/recovery.

Exact Windows retry rejected expired XUR; the normal owned stop succeeded.
A separately explicit fresh command reached managed forwarding and returned
Store OutcomeUnknown at19:45:41Z (116234ms,32 Protect/2058 Unprotect,
70861ms protection). Closed peer metadata reports504, not verified Store or
noncommit. The current new intent is preserved. Android Refresh153 followed
by independent Inspect154 ended in error (MailboxSyncTotal47635ms,
4 Protect/256 Unprotect,23281ms protection).

Actual APK41 build DLL/PDB identifies the Android failure precisely:
Protocol `RequireRetainedReadSuccessAsync` IL502→source216 (the compound
request/result/current-time guard); Shared acquisition IL3783→source108
(first newly received result verification). Protocol module/PDB SHA256:
`d188acf9e6f908330c45b18b3d9a0848ec130bf3358edc11972c361d81ac2056`,
`71fe16eedff26bfa78bbc039305b0c31553fba21cb7084ae972cf73d4e750b7e`.
Shared module/PDB SHA256:
`ce02629b312508ac5dabeaf8b0c5cb2a73b76c0f6c79a9641d20f075081c0850`,
`4a95405e0fb85713e52e52e41019b1bc36c5d2773a399d086f647215e1df28cd`.
This does not identify the failing predicate or establish a clock defect.

The next diagnostic candidate attaches one of eight fixed reason labels to
that same ordered guard, using the existing exception-data/QA allowlist
pattern. No timestamp/value, address, capability, message or exception text
is emitted. Predicates/order/refusal and wire/API/state generations are
unchanged; no reset or consumer data migration is required by this
metadata-only change. Consumers still require matching rebuilt binaries. Protocol
focused140 and unfiltered MAUI Clean271 PASS/native0, no compiler diagnostics.
Includes a real signed winner rejected by an independently earlier client
upper bound, then verified unchanged once that test clock advances. This
proves guard/diagnostic behavior only, not the actual device's failing reason
or permission to wait out a future result in runtime. Matching Android
observation, source canonical full/package and physical acceptance remain open.

## APK42 and UI-context correction candidate

APK42: MAUI `3bf452f`, Shared runtime `7d64fe7`, Protocol `8ea886f`;
native0, no compiler diagnostics, signer unchanged. APK SHA256:
`c42e940ecc571fb3b020100b3c4cce1b895ef86b616af468d456c75885cbbf7c`.
Dry155 passed; install156 failed on a protected-package ADB preflight, before
installation. Settings157 therefore does NOT qualify APK42. Fresh install158
passed and an independent `pm path`→base-APK SHA256 read matched the exact
local APK. No account reset or protected-package mutation was requested.

Settings159/Inspect160 failed UI bounds; a real device screenshot revealed the
Android system ANR dialog over Deep Chats. The device was awake and unlocked.
Supported restart161 completed without reset; Inspect162 observed Chats;
Refresh163 failed the initial UI read, before any confirmed Refresh action.
No APK42 mailbox synchronization or retained-result reason is established.
The earlier APK41 refusal is still the last qualified business observation.

Source reproduces a concrete UI scheduling defect: reconnect's background
worker inherited the constructing synchronization context through its first
wait. A synchronously completing gate does not move the reconnect delegate
off that UI thread. `artifacts/s05-ui-crypto-context-20261017/red-01` records
the new test's0PASS/1FAIL/native1: both observed background entries used the
test UI context. This establishes the source defect, not an ANR stack cause.

The correction starts the process-owned worker with Task.Run. Foreground
delegates retain the caller context and actual lane until complete; unchanged
deadline, cancellation and stale-success tests remain. The MAUI conversation
adapter similarly runs domain/native crypto and local history reads off UI,
not its caller's ViewModel mutations. No new authority, retry, fallback,
generation, payload cache or timer. `clean-02`:unfiltered272PASS/0FAIL/0skip/
native0, Windows PowerShell5.1/SDK10.0.301, no compiler diagnostics. Matching
Windows/Android compilation and physical ANR/receive retest are still required.
The same correction's unfiltered `smoke-03` also passed119/0/0/native0, no
compiler diagnostics. Smoke is source/harness evidence, not device acceptance.

## Matching UI correction observation

Windows26/APK43 compiled from MAUI `443e9b7`, Shared runtime `7d64fe7`,
Protocol `8ea886f`; both native0 without compiler diagnostics. Windows26
opened the existing QA profile; no new Windows business request was submitted.
Apphost SHA256:
`a596798c36a1f40f1cd845b811ab24cd4490b1276355cb0bc124ae7ee81843ae`.
APK43 SHA256:
`4ea56851b5f8d1d301754932e7eced954b1225d7faa98a36625422dd17d18b5e`;
signer unchanged. Dry164/update165 passed. An independent installed base-APK
SHA256 read matched; Settings166 confirmed retained account/recovery.
Chats167, Refresh168 and independent Inspect169 all returned UI observations.
No ANR was observed in this bounded sequence, including the active receive;
this is not sustained responsiveness qualification or an ANR stack diagnosis.

MailboxSyncTotal69575ms,10 Protect/332 Unprotect,30534ms protection. The command
passed retained-result verification but returned Retrieve OutcomeUnknown.
Independent Inspect169 confirmed messaging error, not synchronization. Actual
Shared module/PDB maps ForwardAsync IL1150→source304 (received non-success
response), not a local cancellation throw. Closed node1/node3 peer metadata
reported504. Shared module/PDB SHA256:
`a2e34d7ba6a1075a26645d688ab3c169e35084052b4a6dae53c9c41cd439cf11`,
`7e02ea508715215affc13ef61aa54edc87b22d4226c11a8516b5c1426bfe2353`.
The APK41 time refusal did not recur here; its exact predicate/cause is still
unknown. Do not infer a clock fix, noncommit, Store/Retrieve success or text
delivery. Physical acceptance remains0/4. The next narrow investigation is
the actual local native mailbox terminal refusal behind the bounded504.

## Closed native terminal observation candidate

XNode now observes the existing uncertain terminal branches through its existing
structured logger. Only fixed stage/category, bounded inner depth and an owned
assembly code method/IL are emitted; no exception text/data, request, route,
identity, payload, path or authority time is logged. Emission is limited to one
event per ten actual seconds. Absent/throwing logging cannot change the original
unknown result or caller cancellation. Quorum, deadlines and retry rules remain
unchanged; this is not an HTTP response or protocol change.

`xnode/artifacts/s05-native-terminal-observation-20261017/focused-02`:
43PASS/0FAIL/0skip/native0, Windows PowerShell5.1/SDK10.0.301. Includes the
native ingress/onion suite, real three-host Store/Retrieve/ACK and three new
closed-observer cases (including unreadable private exception fields, throwing
logger and preserved caller cancellation). The initial compile failure in
focused-01 is retained separately. This is focused source evidence, not a full
XNode gate, local runtime observation or physical business acceptance.

## Retained local deployment and native pending Store diagnosis

Local XNode `17cee31` was built and activated through supported current-dev
Build13/Up14/Verify15, each native0. All nine services run their captured desired
images. All54 mounts (29 volumes/25 binds), including read/write modes, are
preserved. The raw representation comparison did not pass: seven bind sources
changed between Windows and Docker Desktop path notation. A separate finite
alias comparison resolved each target within the named workspace and matched
all54 original identities; it does not overwrite or relabel the raw failure.
Receipts: `deep-devops/artifacts/s05-native-terminal-observation-20261017/`.
No enrollment, key/floor/genesis replacement, account reset or production change.

Android43 remains the independently hash-qualified installed APK above.
Inspect170 refused before device action because the new exact evidence document
was absent from the harness's documentation-only allowlist. A harness-only
correction adds that single path, not a wildcard or application source exception;
its focused UI/source contract passed1/0/0/native0. Strict ancestry, clean source,
APK/source/signature and install checks remain. Dry171, Inspect172, Refresh173
and independent Inspect174 then passed their scoped UI observations. The final
screen has messaging error, not synchronization. MailboxSyncTotal34793ms,
2 Protect/248 Unprotect,22929ms protection; Retrieve OutcomeUnknown remains.

During this bounded receive, the closed native logger observed a node terminal
failure in `MailboxPeerMutationStore.ReadCurrentWindowAsync` IL1074. The actual
container module/PDB maps it to source47: live unresolved native Store custody.
Module SHA256 `9d558e9404d3c176358a213dbebb7ff93826f52e0de6b3c8d61156cbcacbbbc4`;
PDB SHA256 `b0c47d4a58801f003bc3551c4c6ae039a55b6ad413c60100d1d61a00880de992`.
This identifies an actual native refusal, not a local managed cancellation.
Closed observations do not correlate by operation ID and do not prove which
earlier sender request created that pending mutation or why its Store failed.
The existing pending-prefix guard must remain: skipping it can lose a later
completion below a cross-replica cursor. An empty successful page is not a fix.

Windows26 selected the same existing peer. The first Add action projected the
retained pending request; a separate explicit Retry ended at XurWindow in
`PrepareInitialContactDraftAsync`, before another native Store. ContactStartTotal
42690ms,2 Protect/498 Unprotect,23382ms protection. No stop, replacement intent,
account reset or repeated submission followed. This does not qualify Store
reconciliation or associate the native pending mutation with this exact request.

The immediate S05 investigation is the real original pending Store's bounded
settlement/recovery and its expiry boundary, alongside the retained sender's
application deadline. Preserve exact native intent/replay and client uncertainty;
do not make XUR/grant freshness renewable, remint a peer nonce, skip the cursor
barrier or use a new account to qualify retained recovery. No new wire/API or
recovery bypass is frozen by this checkpoint. Physical acceptance remains0/4;
canonical full/package, remaining lifecycle and release gates are still open.

## Original Store recovery runtime observation

The [native recovery candidate](../../xnode/docs/testing/s05-native-store-recovery-2026-10-17.md)
was activated locally as Node `30cd519`, with9 desired images/54 retained mounts
and successful supported readiness. It does not renew expired original grants.
Android43 Dry175/Inspect176/Refresh177/Inspect178 used the same installed APK and
account, no reset. Final UI has messaging error, not synchronization.
MailboxSyncTotal60611ms,10 Protect/345 Unprotect,32642ms protection;
ForwardAsync IL1150 still reports Retrieve OutcomeUnknown. A native pending
Store refusal was observed again. Generic unavailable worker events do not
identify the failed original-admission guard or correlate an old sender intent.
This is not recovery/delivery acceptance; physical0/4 remains unchanged.

## Original Store expiry exclusion: retained read recovered

The [native DR-0134 consumer](../../xnode/docs/testing/s05-store-expiry-exclusion-2026-10-17.md)
was activated only in retained local deep-dev-current as Node `3fe03ab` /
Protocol `9d6df07`. Supported Build25/Up26/Verify27 all native0, nine desired
images and54 original mounts retained. Raw path representation FAIL and a
separate finite equivalence proof (one alias) remain separate evidence. No
production deployment, new ceremony, APK replacement or account reset occurred.
Installed Windows26/APK43 binaries and their previously recorded hashes remain.

APK43 dry179 passed. Refresh180 initially showed busy; independent Inspect182
showed SynchronizationCompleted=true, MessagingError=false and no busy control.
MailboxSyncTotal68814ms,13 Protect/384 Unprotect,36851ms protection. Contacts183
and Settings189/190/191 retained the account/recovery; reconnect was Verified
when its section became visible. Recovery/account flags on unrelated pages
mean only visible controls, not deletion. This is retained incoming read
recovery, not contact/text delivery or a sustained acceptance run.

Windows26's first explicit Refresh ended OutcomeUnknown after33095ms,
9 Protect/267 Unprotect,9751ms protection. An uncorrelated node1 native-deadline
event identified LinuxBootOnionMonotonicClock.ReadAsync IL24, not noncommit or
an authenticated grant failure. One ordinary UI retry completed without error:
MailboxSyncTotal24834ms,4 Protect/218 Unprotect,10742ms protection; independent
UI showed synchronization completed and four existing outgoing awaiting-acceptance
rows. Those rows and their uncertainty were preserved; no Stop/new intent.

Android SetPeerAddress184 used the preserved public Windows QA address, then
Dismiss185/Add186/Inspect187 projected the existing unfinished contact request.
The first Add is not a new Store or contact-delivery receipt. Separate exact
Retry188 entered actual business work and failed in the same retained-draft
metadata guard as earlier diagnostic117: PrepareInitialContactDraftAsync IL4129
(matching installed source line86), before another native Store. ContactStartTotal
69531ms,3 Protect/382 Unprotect,35200ms protection. Contacts192 independently
showed the error; original request/account/seed custody remain. No repeated
submission, stop, replacement intent or reset followed that failure.

Physical contacts/text/attachments/groups remain0/4; S05 is unaccepted. Next
implementation is the actual retained Initial draft/current dependency boundary,
not another node reset or expiry bypass. Which metadata binding differs is not
yet established. Full source/package, cold/sustained transport and complete
physical business acceptance remain open. The first Windows timeout remains
a failure receipt, not relabelled by its later successful retry.

## Original contact retry expiry entry: source candidate

[DR-0135](../../docs/survival-program/decisions/DR-0135-owned-initial-retry-expiry-entry.md)
uses Shared's actual owned expiry result before peer resolution. MAUI clears
only the matching in-memory retry choice and explains that the original may
already have reached the recipient. No automatic new intent or dispatch follows.
A separate Add rereads protected operations; arbitrary failures retain the old
choice. Account/recovery and unknown-delivery facts are not reset.

Unfiltered MAUI Clean273 passed/native0/0 skipped, no compiler diagnostics.
Receipt: `artifacts/s05-initial-retry-entry-20261017/clean-01`; this is UI source
plumbing evidence with a stub expiry outcome, not an authenticated native stop.
[Shared's actual PQ/SQLCipher qualification](../../deep-client-shared/docs/testing/s05-initial-retry-entry-2026-10-17.md)
is separate. Windows26/APK43 are unchanged; matching new builds and physical
request/accept/text are still required. No physical business acceptance claimed.

Matching Windows27/APK44 were built from MAUI `8181f0a`, Shared `f63b432`,
Protocol `9d6df07`, native0 and no compiler diagnostics. The first Windows
wrapper failed parameter binding before compiler launch; the fresh build-02
receipt succeeded. Windows apphost SHA256
`b1ef531581c305ba26f2905faaa88ca682081ab635124c1c989a7630109e794e`;
assembly `8be67b75a909765ffe131dcc548470aa611f5ad4bf7cf99b6a2ef66fb5deced7`.
APK44 SHA256 `6e5a7e4e5b760ecfd486791ad396dd7d25e0e1cb72c2f6a4328bc7e80964794a`,
same previously pinned signer. Supported dry193 passed; Install194 correctly
refused an existing package before update permission, then explicit update195
installed/launched without reset. Settings196 retained identity/recovery;
Scroll198 exposed Verified. Unrelated off-screen account flags mean visibility,
not account loss. Production packages were preserved by the harness snapshots.

Windows27 retained the exact previous account and encrypted recovery. Add first
projected the existing unfinished request. One exact Retry completed the owned
expiry path: the UI showed original expiry/possible remote receipt, no error,
no retry choice and no automatic replacement. ContactStartTotal17668ms,
6 Protect/1028 Unprotect,13985ms protection. Only a separate explicit Add began
new work. It failed during permanent-contact read, at ForwardOnceAsync line146 /
PrivacyManagedIngressHttpTransport line299 (46429ms,26 Protect/1173 Unprotect,
23944ms protection). One exact UI retry failed at the same boundary (5650ms,
1 Protect/372 Unprotect,2637ms protection). The original new intent remains;
neither operation is a delivered contact or an authenticated new native Store
receipt. The read refusal does not establish the absence of any earlier effect.

The existing QA observer loses the already decoded ingress error class. A
display-only correction maps complete fixed literals from the exact sealed
Shared-owned ingress exception to closed labels. Foreign exception getters and
unknown text remain unread/unreported; no route, retry, certainty or authority
decision consumes a label. Unfiltered Clean284 passed/native0/0 skipped without
compiler diagnostics (`clean-02`). Matching diagnostic builds/retest are pending.
No further blind retry, account reset or server authority change was performed.

Windows28 diagnostic build from MAUI `a2a758a` / Shared runtime `f63b432`
(docs-only head `ce65494`) / Protocol `9d6df07` passed native0 without compiler
diagnostics. Apphost SHA256
`b31c4df5be6cbda043c73e26ca7a938effc5e70388774a2c275d9d7886080aa3`;
assembly `ef2fe8cd8629896b3fcd00d927e4de3b7d4f81c5327aae3488e53f8093399d7e`.
The unchanged account reopened. Add selected an actual protected pending request;
one separate exact Retry did not reproduce the prior ingress-class failure.
It ended at the existing foreground budget: ContactStartTotal120050ms,
9 Protect/1809 Unprotect,75840ms protection. Closed locations identify
OpenInitialSessionCustodyAsync:28 / OpenCurrentDeviceStateStoreAsync:109 /
RequireRetainedInitialDraftAndRecipientUnderLeaseAsync:208 /
DeliverOwnInitialContactAsync:108. Independent UI showed cancellation, retained
retry choice and no busy command. No additional retry or reset followed.

Receipt: `artifacts/s05-initial-retry-entry-20261017/windows-28-build/closed-business.json`.
The ingress class remains unobserved, not fixed by a successful read. The next
bounded source inspection is repeated fresh device-source/retirement/checkpoint
inventory opening under the held account lease. Keep every actual SQL/native/
retirement binding and reread; no plaintext cache or time-window extension.
APK44 remains installed; no diagnostic-only Android rebuild was launched.

### Device-source batch: matching builds and keyboard harness

Windows29 and APK45 were built from MAUI `5b7132d`, Shared `c9fbb0f`,
Protocol `9d6df07`, native0 with no compiler diagnostics. Receipts are under
`artifacts/s05-device-source-batch-20261017/{windows-29-build,android-45-build}`.
Windows apphost SHA256
`c2b07d5d3640999a1ddcc784f3754e0ef28a7af4f02a7e5a723c9759f12ee6d2`;
assembly `1cfedd39c468b92f83470dcb31aa4964b65cadf3342e446e22c5ba5d7b1034e3`.
APK SHA256 `b11ab05e9d8ece0c676df50046cfa39b2f91372fe3423987c7cc39a1069066fb`,
same pinned signer. Both installed without reset; Windows independently retained
its identity and encrypted recovery. Android dry201/update202 passed with
protected-package snapshots unchanged; account/recovery readback on45 is pending.

Windows29 Add selected the actual protected pending request; a separate exact
Retry reached native Store transport but ended OutcomeUnknown. ContactStartTotal
65429ms,4 Protect/1615 Unprotect,11204ms protection. Closed client locations:
PrivacyManagedIngressHttpTransport.ForwardAsync:304,
OwnedStoreIngress.StoreOnRouteAsync:781, ClientMailboxAdapter.DispatchStoreAsync:887.
Independent terminal UI was not busy and retained the retry/error. This is not
delivery, noncommit or a controlled latency comparison. A temporally nearby
node2 native-deadline at ReadNetworkAsync IL163 is not correlated by identifier;
its exact awaited stage is not yet mapped. No subsequent blind retry occurred.

Android Settings203/206 refused before tapping because WindowManager reported
`mImeShowing=true` while input-method service reported `mInputShown=false`.
Dismiss204 lost QA foreground after Back; it is a failed phase, not successful
keyboard dismissal. Supported Restart205 returned the QA package without reset.
The harness now reads one exact, bounded input-service state row after the
unchanged package-owner check; missing, duplicate or unknown state rejects.
No raw dump/editor text is recorded and no foreground/typing guard is relaxed.
Parsed-function true/false/stale/hostile tests and unfiltered MAUI Clean285
passed/native0/0skip (Windows PowerShell5.1, SDK10.0.301), receipt
`artifacts/s05-device-source-batch-20261017/harness-clean-01`.
This is harness source evidence, separate from the physical reobservation below.
The existing exact ancestor/harness-only provenance exception keeps APK45 tied
to its actual source, not to an unbuilt application revision. Physical business
acceptance remains0/4; source full/package qualification is separate.

Harness `b5fcd98` subsequently passed dry207 against the unchanged APK45 source
`5b7132d`. Settings208 retained account/recovery; Scroll210 showed Verified.
Chats211 and one Refresh212 were followed by independent Chats216: no error,
no Busy, synchronization complete. Closed MailboxSyncTotal70937ms,
12 Protect/362 Unprotect,34456ms protection. Closed read-only list observation
counted0 incoming/0 outgoing/0 accepted rows. OpenOnly217 correctly refused
before tapping because the list did not contain exactly one conversation.
This is successful empty-mailbox synchronization, not request delivery.
Settings215 also remained usable during the earlier busy observation; no account
or process reset was needed to complete this attempt. Windows29 independently
loaded its local history without network authority; that is not remote receipt.

After the local Node diagnostic activation described in
[its checkpoint](../../xnode/docs/testing/s05-private-grant-hop-2026-10-16.md#2026-10-17-native-network-gate-observation),
one explicit Windows network recheck retained account/recovery and reached
Verified (VerificationTotal13088ms,2 Protect/364 Unprotect,7291ms protection).
At00:26:43Z one exact original contact Retry was started; no replacement or reset.
It completed the existing owned expiry path at00:27:12Z:29330ms,
6 Protect/1025 Unprotect,25938ms protection. Independent UI cleared the retry
choice, showed expiry/possible recipient receipt and no error. Only a separate
explicit Add at00:28:03Z began new work for the same QA peer. It ended at the
unchanged foreground budget:120047ms,30 Protect/1717 Unprotect,69458ms protection.
Closed cancellation locations: ReadOwnedBatchAsync:101,
ProtectedInitialKeyRetirementJournal.ReadAsync:242,
SqliteDeviceStateStore.ReconcileInitialSessionsUnderGateAsync:308 and
ReadOwnedMailboxEnvelopeUnderLeaseAsync:91. Independent UI retained the new
retry choice, showed cancellation and was no longer busy. No further retry or
reset followed. No delivery/noncommit inference follows from these locations;
the prior empty Android read predates this new command.

The next bounded candidate batches segmented protected custody: actual idle
compaction's16 part reads and pending payload's floor+18 parts, preserving
missing/extra/length/hash/owner/cancellation checks and fresh per-call inventory.
It is not installed or physically qualified here. No timeout/cache/authority
change, and no additional native Store claim.

### Segmented custody matching builds

Windows30/APK46 built from MAUI `a3fc002`, Shared `94bc6d8`, Protocol `9d6df07`,
native0 without compiler diagnostics. Receipts:
`artifacts/s05-segmented-custody-batch-20261017/{windows-30-build,android-46-build}`.
Windows apphost SHA256 `d9bb32a862f9e7d1f9d1df6d650f899824d2c0c073b7192b574042f30df23bbc`;
assembly `2171f68afec89e64f3c6b9a920b9154439fa788a71b6c87422e24d27d21abdf9`.
APK46 SHA256 `be10d91e05b5feff533f8f7697b12aedc042f7c4ba5680309e6b4999e7537b5d`,
same pinned signer. Windows29 closed normally;30 retained the exact identity,
encrypted recovery and Verified state. No reset or new account.

Add selected the actual pending command; one separate exact Retry was clicked
at00:46:33Z. It waited behind background verification until the business delegate
entered at00:46:54Z. ContactStartTotal114784ms,6 Protect/1353 Unprotect,
58968ms protection; result OutcomeUnknown at ForwardAsync:304 → SendAsync:75 →
StoreOnRouteAsync:781 → DispatchStoreAsync:887. Store transport was reached;
durable storage/recipient receipt is not proven. This attempt overlapped the
Android46 build and is not a controlled device performance comparison. Future
Windows business attempts must not overlap heavy host builds/gates.

Before update, APK45 Refresh220→Inspect221 completed without error; closed
MailboxSyncTotal77176ms,12 Protect/366 Unprotect,35838ms protection. Independent
list counts again0/0/0. This read predates the Windows30 retry and does not settle
its uncertainty. APK46 dry222/update223 passed without reset, preserving protected
packages; Settings224 retained account/recovery and Scroll226 showed Verified.
No request/accept/text acceptance claimed; physical business acceptance0/4.

After matching native diagnostic activation, Windows30 exact Retry at01:03:41Z
completed owned expiry (49831ms,6 Protect/926 Unprotect,46119ms protection), with
no automatic replacement. The separate explicit Add at01:05:48Z ended
OutcomeUnknown after107650ms (33 Protect/1580 Unprotect,59140ms protection), again
at actual Store transport. Independent UI showed the retained retry choice,
terminal error and no Busy state. No reset or further retry followed this
observation. The node's closed reason is `BlobTtl`; the shipped7-day cap conflicts
with current30-day object retention. Node fix and its148 focused tests are
recorded in the [native checkpoint](../../xnode/docs/testing/s05-private-grant-hop-2026-10-16.md).
Matching local Build34/Up35/Verify36 subsequently exited0, preserving all54
mounts and using all9 desired images. After the node restart, Windows network
reverification returned Verified without changing identity or recovery. Exact
retry was clicked at01:21:03Z, with no overlapping heavy build/gate. Its terminal
result:30900ms,3 Protect/1191 Unprotect,8662ms protection, ProtectedState at
RetainInitialStoreEvidenceUnderLeaseAsync:168 before dispatch. Independent UI
preserved the retry and showed no Busy state. A native encrypted fixture
reproduces that guard across an ordinary view renewal; the
[Shared correction](../../deep-client-shared/docs/testing/s05-logical-grant-custody-2026-10-16.md#2026-10-17-pending-initial-across-ordinary-carriage-renewal)
passed23 focused source cases, but still needs matching client builds and physical retry. APK46 dry227/Inspect228
passed provenance/foreground checks, recovery retained and Verified; no reset.
No recipient receipt or contact/text acceptance follows.
