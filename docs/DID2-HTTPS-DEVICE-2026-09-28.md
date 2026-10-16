# DID2 HTTPS physical checkpoint — 2026-09-28

This records an incomplete real Windows diagnostic, not messaging or release
approval. Registry and seed1–seed3 are production infrastructure; Mr. X permits
testing there until he explicitly reports users exist. There is no remote UAT.

## Current retained-device checkpoint — 2026-10-16

### Local replica rollover retest

APK31 (MAUI `fcc84fc`, Shared `cbcd28a`, Protocol `b3d24cb`) was installed by
supported preflight/update phases without resetting the QA account. Its SHA256
is `3b2ca1dfa2ccd74492dfc5b69ba491d529cc608fa6d7223c0a866c7f86d6d410`.
The existing recovery copy remains stored and protected-package audits match.
Receipts: `artifacts/s05-service-rollover-20261016/android-preflight-12`,
`android-install-13`, `android-inspect-14`, `android-settings-15`,
`android-scroll-16`, `android-scroll-17`, `android-inspect-18`.
The local server uses DR-0132's fixed replica service transition; this older
client pair is diagnostic only, not latest-source package qualification.

Two complete background attempts hit the unchanged120s budget during local
protected-state reads in PreKeyPublication, not the old PreKeyInventoryExpiry
guard. First:120094ms total,913 unprotect calls,62965ms protection; second:
120070ms total,931 calls,73478ms protection. Their cancellation frames identify
account reopen/retirement or current-pointer/resolver reads. These aggregate
costs are not proof of a sole cause or of noncommit at a particular replica.
Current source groups repeated retirement registration reads under the actual
account lease; source and matching mobile qualification remain separate.
No first contact/text, attachment or group acceptance is claimed (0/4).
Windows input remains stopped after the user's physical Escape; automatic goal
continuation is not permission to resume it.

APK32 (MAUI `fa45de2`, Shared `1c5008a`, Protocol `fed7d30`) built with0
warnings/errors; SHA256
`12759d4d195e6c4dec940ecffa388c5260d6e820716e827c02da9f643359b40f`.
Supported preflight33/update34/settings35/scroll36–37 under
`artifacts/s05-prekey-read-path-20261016/` retained account/recovery and protected
packages. One complete attempt still cancelled at120071ms,897 unprotect calls,
69613ms protection (PreKeyPublication88819ms/658 calls/50890ms protection).
The cancelled stack was waiting for the account lease in directory-floor
restoration during a repeated current-proof acquisition; it does not by itself
prove a deadlock, a sole cause or a particular replica's noncommit. No registration
or business success is accepted. The next candidate removes complete per-fragment
network reacquisition while retaining ordinary currentness checks under DR-0131;
it is not installed by these receipts. Windows remains stopped after Escape.

APK33 (MAUI `e6418ff`, Shared `472457e`, Protocol `fed7d30`) built with0
warnings/errors; SHA256
`ab6442ab358ec13253b6c2d2c2f0f99b222a7a5667711d35f88d3b274a788e85`.
Supported preflight38/update39/settings40/scroll41–42 retained recovery and
protected-package audits. PreKeyPublication returned successfully before the
pipeline entered ContactPublication:65884ms/506 unprotects/40313ms protection.
By source order this requires both verified XIC1 signatures and durable pair
recording, not only local staging. The next command completed that phase in
21205ms/181 unprotects/0 protects; a further repeat used24306ms/181/0.
`android-pair-diagnostic-45` retains the closed code/stage metadata. No account,
key, inventory or original contact intent was reset/replaced.

Full verification still did not complete. First/second attempts cancelled at
120048/120011ms during contact publication. A third ended at113481ms with a
typed ingress dependency refusal and Rejected scheduling. A contemporaneous
node log reports IssuedLowerBound before backend forwarding; adjusting the
independently observed phone wall-clock offset puts it within24ms of the device
failure, but there is no request-identity correlation. No clock rule or issue
time was relaxed/backdated. Manual Verify47 also cancelled at120006ms during
the same retained contact renewal. Contacts/messages/files/groups remain0/4.

The next source candidate makes the existing public contact workflow complete
its own dependencies with one independently fresh context, retaining all owner
phase checks. MAUI no longer duplicates complete network/prekey acquisition.
Its scheduler also consumes the existing typed retryable dependency failure
instead of treating it as a permanent security rejection; an explicit typed
denial cannot gain retry from an inner HTTP exception. These changes require
new focused checks and matched APK; APK33 observations do not qualify them.
Shared workflow04 passed18/0/0/native0. MAUI focused06 passed79/0/0/native0;
the original focused05 has78 PASS/1 stale source-assertion FAIL: it searched for
the removed redundant UI-level `network.EnsureCurrent()` call. The replacement
still checks pinned bootstrap/admission order and follows verification, prekey
completion and final currentness into the actual account-owned workflow.
Unfiltered Smoke project07 passed119/0/0/native0. All three successful runs
used Windows PowerShell5.1/SDK10.0.301 without compiler warnings/errors. They do
not qualify canonical full/package gates or physical delivery. The renamed
ContactPublication work scope now includes the complete owned workflow; compare
whole verification totals, not old versus new isolated contact-stage timings.

APK34 (MAUI `438474b`, Shared `ca1df5f`, Protocol `fed7d30`) built with0
warnings/errors, unchanged signer and SHA256
`71e0ad10e46b6004de0c27cb721301499ce64302b13c31806c0f117bf07d66e7`.
Supported preflight49/update50/settings51/scroll52–53 retained the existing
account/recovery and unchanged protected-package audits. Independent Inspect54
observed Verified after the first complete workflow:65238ms total,
491 unprotects/42137ms protection/0 protects; ContactPublication45544ms.
Manual Verify55 followed by independent Inspect56 observed
`verified-publication / Verified`:62920ms total,491 unprotects/44823ms protection/
0 protects; ContactPublication47621ms. The source now requires the prekey pair,
contact commit and final currentness check before that success. These two actual
retained-account completions are not sustained, restart or business acceptance.
Closed metadata is retained in `android-workflow-diagnostic-58`; first launch's
earlier cancelled setup is retained, not relabelled successful. Contacts57 has
an empty address editor and disabled Add control; no new intent or Store was
submitted. Contacts/messages/files/groups acceptance remains0/4.

Matching Windows19 built/published with0 warnings/errors from the same source;
apphost SHA256
`541edf6ae4cd6c4e29c62f722e3019ce75f8da30c7d233b319785511f7748300`.
Receipt: `artifacts/s05-prekey-maintenance-20261016/windows-19`.
It has not been installed/launched or physically accepted. Windows input remains
stopped after physical Escape and requires a fresh human authorization to resume.
No production changes, GitHub Release or main merge occurred.

Android34 Restart59 -> Settings60/scroll61–62 -> independent Inspect63 retained
the same account/recovery and again reached Verified:66931ms total,
496 unprotects/45083ms protection/1 protect. Subsequent background completions
used64838/59496ms. Chats64 -> one RefreshConversations65 -> Inspect66 ended with
a visible error, not an authenticated empty page or incoming delivery. Closed
business metadata locates `RequireIssuerGrant` through retained-read success
verification; the failed field of its issuer/network/time guard is not yet proven.
No replacement acquisition, contact intent, reset or clock/lifetime change was
used as a remedy. Physical business acceptance remains0/4.

The next diagnostic candidate preserves every original ordered grant predicate,
CryptographicException/message and signature check, attaching only one fixed
reason without grant bytes, times or identifiers. The QA classifier accepts only
that exact built-in failure plus the closed reason set; arbitrary objects/text
and foreign failures remain Unclassified. Protocol focused01 passed89 and MAUI
focused02 passed160, all0fail/0skip/native0, under
`artifacts/s05-mailbox-grant-diagnostic-20261016/` in their respective repos.
It is not installed by APK34. Shared canonical-preflight05 (before these
diagnostic Protocol edits) completed build0/6 fixture PASS/0skip/terminal0,
2042 captured inputs, FullAccepted=false; it is not a full source gate.

APK35 (MAUI `2b8519f`, Protocol `d2e3d42`, unchanged Shared `ca1df5f`)
built with0 warnings/errors and the same signer; SHA256
`5c1183b81a2c76710e89e39cef13f2e32adf1b2b4ce99f53dd422dbcb025f0f5`.
Preflight68/update69/settings70/scroll71–72/Inspect73 retained recovery and
protected packages, observing Verified after61266ms; the next background cycle
completed56077ms. Chats74 -> one Refresh75 -> Inspect76 failed explicitly at
`MailboxGrantExpiry`, not a generic issuer/signature/network predicate. Closed
metadata is in `android-grant-expiry-diagnostic-77`. It does not prove whether
the caller is resuming an active read/ACK, restoring a result or acquiring new
work: the four reported frames belonged to one Protocol owner. No expired grant
was accepted, and no original/grant/counter/cycle was deleted or replaced.

The next QA-only diagnostic keeps four frames/error and four exception depths,
but takes only the first frame of each logical owner (async state machines are
grouped under their declaring owner). The first rejecting guard remains visible
without hiding the account caller behind redundant same-owner helpers. Focused
maui03/04 passed161/0/0/native0 but retained an xUnit assertion-style warning;
the assertion now uses its predicate overload without changing expectations.
Final maui05 passed82/0/0/native0 with0 compiler warnings/errors. Packaged
physical caller evidence remains required.

APK36 (MAUI `51eda49`, Protocol `d2e3d42`, Shared `ca1df5f`) built with0
warnings/errors; SHA256
`36f8bd64c43d1087f9f18f91d4e476b12fa2d1f574f6b392da53912eedbb4f47`.
Preflight78/update79 retained the QA account. Settings80 failed before a usable
hierarchy; fresh Inspect81 reobserved the workspace. Settings82 retained recovery.
Scroll83/84 overlapped in orchestration;83 failed a protected-package audit and
is not qualified. Both processes were terminal before independent Inspect85,
which passed its package audit and observed Verified. Publication completions
were63597/59703ms. No business acceptance is inferred from either scroll.

Chats86 -> single Refresh87 -> Inspect88 again failed `MailboxGrantExpiry`.
Distinct-owner metadata now identifies `RetrieveOwnMailboxAsync` IL3129. The
exact built Shared assembly SHA256
`2fd094b027e2c42fce6e6f1ae904b67c71a7429643e21699be56666eb1ab1448`
and its build-output portable PDB map this to source lines57–59, the `winner ??=` restoration of
an active read's original grant. The preceding branch acquires a new winner only
when Active is absent; this is active-cycle recovery, not a new-read renewal bug.
The exact subphase within1–6 is not observed. Metadata is preserved in
`android-active-read-diagnostic-89`; failed incomplete phases remain separate.
The metadata-only PDB probe exited0; earlier exploratory token/IL reader failures
do not qualify independent disassembly or change the physical outcome.
The Android-target assembly copy has the same digest as the inspected build
output. A direct raw-byte search in the signed APK's compressed assembly store
did not find that DLL; it is not extraction or exact packaged-IL qualification.
The caller inference combines runtime method/IL with this matching build PDB;
full package qualification remains open.

The unresolved implementation boundary is recovery of expired active Retrieve/
ACK while retaining original request/grant/counter/page custody and independently
current authorization. The existing new-work selector cannot replace an active
cycle. Do not bypass grant expiry, rewrite original MAU, discard active state or
reset the account as a remedy. The uncaptured allocation is now frozen in
[DR-0133](../../docs/survival-program/decisions/DR-0133-expired-owned-retrieve-attempt-closure.md);
captured-page/ACK continuation remains open. Publication readiness does not close
this gap; contacts/messages/files/groups physical acceptance0/4.

APK37 builds MAUI `71c973b`, Shared `2cd2f77`, Protocol `2bb61d0`;
SHA256 `71a987e64b9d311ce15cc4c9760710f364ac939d4a06b275044ec26f4db0b800`.
The supported nonincremental optimized-JIT Debug diagnostic build exited0 with
no compiler warnings/errors and the unchanged signer; this is not Release or
shipping-package qualification. Preflight90/update91/Inspect92/Settings93 retain
the existing QA account and recovery, with protected-package audits passing.
Scroll94/95 and independent Inspect96 ran sequentially;96 observes Verified.

Chats97 -> one Refresh98 -> Inspect99 observes busy, not completion. Independent
Inspect101 observes Messaging.Error and no completed synchronization. Closed
diagnostic100 records OutcomeUnknown in `Did2MailboxGrantOnionTransport.SendAsync`
and `AcquireRetainedMailboxReadUnderLeaseAsync`, rather than the prior active
grant-expiry guard. This is progress into grant acquisition, not successful
Retrieve/delivery or an independent observation of the private read subphase.
Publication completed in60969ms with491 unprotect calls and0 protects. Preserve
the exact newly pending issuer request too: OutcomeUnknown is not noncommit.
Receipts live under `artifacts/s05-prekey-read-path-20261016/android-37-*`;
sanitized failure metadata is in `android-37-closed-diagnostics-100`. No retry,
account reset, production update, Windows UI action or business PASS is implied
by those earlier observations.

The subsequent local [Registry correction and activation](../../deep-registry-api/docs/testing/s05-private-grant-hop-2026-10-16.md)
passed supported build10/Up11/smoke12 with all9 expected images and all54 original
mounts proved. APK37 Refresh106 -> independent Inspect110 now observes completed
incoming synchronization without messaging error. Settings111 again confirms
account/recovery; no reset or phrase reveal. These are empty-mailbox observations,
not a claim of captured-page/ACK recovery or message delivery.

After fresh human permission to resume only Deep QA, Windows build20 from MAUI
`bf154f4` / Shared `a06c121` (runtime `2cd2f77`) / Protocol `2bb61d0` compiled with0
warnings/errors, then launched the existing profile. Registration verification
and one incoming refresh completed; independent UI observation found no messaging
error and an empty conversation list. Its closed receipt is
`artifacts/s05-prekey-maintenance-20261016/windows-20/observation.json`.
No other Windows application was controlled. Physical contacts/messages/files/
groups remain0/4, and these diagnostic binaries are not Release packages.

Android Contacts112/Peer113 reopened the previous exact intent. Add114 refused
before input because the keyboard was visible; its native1 receipt is retained.
Dismiss115 -> Add116 restored the saved retry choice, not Store. Retry117 ->
independent Inspect118/Scroll121 reports protected-state failure. Its closed
caller metadata points to `PrepareInitialContactDraftAsync` IL4129; matching
build DLL/PDB maps that to line86, the exact retained metadata equality guard.
This identifies the compound guard, not which field differs or APK-extracted IL.
The original intent was not reset, stopped, rebound or reauthored. DR-0064's
exact endpoint/contact binding remains enforced; a fresh proof cannot rewrite
the old draft. Scroll120 was rejected by dirty-doc preflight before input and
rerun only after the documentation checkpoint commit. Both failures remain.
Closed receipts: `android-37-contact-diagnosis-123`, same artifact prefix.

Windows20's Android-peer form was filled. On the next observation the contact
operation was already Busy; no duplicate input followed. Fresh activation and
independent observation subsequently found terminal protected-state failure.
Closed metadata at17:14:20Z identifies `ProtectedDid2MailboxGrantJournal.OriginalCeiling`
line624 while preparing Initial Deposit custody, not a confirmed Store. Matching
build20 DLL/PDB now maps IL758 to the no-possible-interval rejection, correcting
the earlier compound-guard diagnosis. [Shared source correction and receipts](../../deep-client-shared/docs/testing/s05-logical-grant-custody-2026-10-16.md)
do not yet prove device delivery. The contact intent
remains retained; do not infer non-delivery, reset or loosen the guard. The
[foreground scheduling correction](S05-FOREGROUND-DEADLINE-2026-10-16.md) is a
separate source fix, not this physical rejection's cause or repair. Android
Refresh125 -> independent Inspect127 again completes empty incoming sync without
error; no incoming contact or physical business acceptance is established.

### Matched acquisition retest

APK30 compiles MAUI `c8732ab`, Shared `32de907`, Protocol `ef6b5f4`; SHA256
`b7046d13fafe09146ad8d12bfa08af3b829ad152901c9e37bc20b2fd5fcb06b2`.
Build30 completed with0 warnings/errors and the unchanged signer. Exact packaged
code contains the three separate fixed prekey refusals; supported dry/update
Install01 and Settings01 preserve recovery and protected-package audits.
The complete attempt recorded51435ms /482 unprotect calls /29805ms protection
and reached `PreKeyInventoryExpiry`: the unchanged authenticated upper-time
coverage predicate rejects the staged XPI1. No key replacement/reset occurred.
The original Protocol exception type/stage/rejection and verification conditions
are unchanged. Protocol focused9 and MAUI diagnostic-focused108 passed/native0;
the original ambiguous-message red1FAIL is retained. This diagnoses the missing
ordinary prekey lifecycle, not a completed renewal or contact exchange.

Windows UI access was separately revalidated: fresh selection confirms build17
running with the retained QA profile. Its previously unobserved launch is now
observed; do not carry the former tool-budget refusal forward as a current block.

The DR-0129 source successor changes the fixed genesis-only prekey diagnostic
to `ContactPrekeyScope`; MAUI's exact classifier and test mapping follow that
current source only, without a legacy alias. Focused `maui-05` under
`artifacts/s05-prekey-service-succession-20261016/` passed108/0/0/native0.
This diagnostic update is not installed on either device. The
[Protocol checkpoint](../../deep-protocol/docs/testing/s05-prekey-service-succession-2026-10-16.md)
records signed source evidence and the still-failing MAU2 package gate.
Multi-generation inventory custody and matched same-account physical recovery
remain open; do not count either installed profile as recovered by these edits.

APK29 compiles MAUI `893db64`, Shared `32de907`, Protocol `cacbd67`; SHA256
`3f29b8a4517ea17f5d55b6798aebae5bcc96d31507d31e88910b749e4f9dfe9c`.
Build29 completed with0 compiler warnings/errors and the unchanged signer.
Exact APK assembly-store inspection confirms the bounded acquisition observation
call and atomic journal-group read. Supported dry/update Install01 passed;
Settings01 confirms retained recovery and protected-package audits are unchanged.
The complete attempt recorded65241ms /763 unprotect calls /40120ms protection.
It passed NetworkVerification and PreKeyPublication but failed ContactPublication.
Exact packaged IL maps the new transport failure to the non-success HTTP response
branch, not the previous DTT binding guard. Matching local relay logs show504,
the exit reports backend503, and Registry reports route `Admission/busy`.
This identifies this attempt's refusal; it does not prove the prior physical
DTT failure's subcondition or sustained acquisition recovery. No Store/contact
acceptance, message, attachment or group effect is accepted (0/4).

After the matching local Registry route-admission update, one explicit
VerifyNetwork01 followed by independent Inspect02 completed
`verified-publication/Verified`, with recovery retained and protected-package
audits unchanged. Work metadata records99542ms /887 unprotect calls /51049ms
protection, within the unchanged two-minute budget. No new network failure was
logged for that attempt. This is one same-account verification/publication
success, not sustained readiness or contact/message/files/groups acceptance.

Windows17 with the same three source revisions built with0 warnings/errors;
apphost SHA256 `d67b1be24ae369ace45307613c90e59cd6b56b698474e9d20272f5283289a5d0`.
Windows16 was closed through its UI. The subsequent launch action returned
`computer-use request budget exhausted`; its launch outcome is unobserved.
Do not assume Windows17 is running or reuse the old window binding. Fresh
selection is required when the supported UI tool becomes available. No account
reset or automation fallback was attempted. Source/full/shipping gates remain
open, including Graph09's existing MAU2 blocker. Production is unchanged.

### Bounded work-cost diagnostic candidate

After Node build13 / supported Up14, Windows16's incoming refresh at05:24Z
completed synchronization with an empty list and no new closed failure. A second
refresh started05:39Z, was still Busy at05:41Z, and was observed completed by
06:00Z; its exact completion time/cause of delay was not measured. A bounded
sample-only trace had no interpretable owned frames and is not cause evidence.
These observations do not prove a contact/message effect or explain the earlier
exit failure.
Android25 Verify03 still ended failure-redacted with the same profile retained.

The HTTPS QA composition now measures verification stages and aggregate platform
protection call count/time. Output is fixed stage names, bounded numeric counts
and monotonic durations only: no slots, lengths, identifiers, paths, values or
exception text. An async-local scope follows the actual operation; the original
protector, ciphertext, lease/floor checks and two-minute attempt budget remain.
Observer failure cannot replace the operation result. It is absent from normal
and Release composition and does not cache plaintext or authorize work.
Focused work/storage/reconnect/composition tests passed52/0/0; the unfiltered
Clean project passed227/0/0, native0 under Windows PowerShell5.1 / SDK10.0.301
(`artifacts/s05-work-cost-20261016/focused-01`, `clean-02`).

APK27 compiles MAUI `1ba92e6`, Shared `104b24b`, Protocol `0867668`; SHA256
`7b3cea2ced7485235df064e7d64ab060703d71bfd539b4de66f60c8375726a17`.
Build27 completed with0 warnings/errors and the unchanged signer. Supported
dry/update Install01 passed; Settings01 confirms recovery remains stored. Build26
failed before compilation on a missing required lab-pin parameter, with no
installation; Build27 retained the existing compiled diagnostic pin, not a new
authority. All protected-package audits remain unchanged.

The actual cancelled verification recorded120067ms total,1226 unprotect calls
and66813ms in platform protection. ContactPublication alone recorded56456ms /
562 calls /31129ms protection; PreKeyPublication recorded25281ms /236 calls /
12995ms protection. These are inclusive local operation costs, not trusted time
or proof that protection is the sole cause. Shared's atomic journal read-group
candidate is qualified separately in
[owned local read groups](../../deep-client-shared/docs/architecture/owned-authored-counter-custody.md#actual-atomic-local-read-groups).
The matching post-optimization observation follows below. No account,
original intent, floor, key or timeout was reset/replaced; acceptance remains0/4.

APK28 now compiles MAUI `68caf0d`, Shared `f87fc97`, Protocol `0867668`; SHA256
`e5659419b1e2a4f0d382c748ab42480d16c75c7a8872fafa3e5849bd5b58857f`.
Build28 completed with0 warnings/errors and the unchanged signer. Exact APK
assembly-store inspection proves one atomic journal-group call in account reopen
and the QA observer. Supported dry/update Install01 and Settings01 passed;
recovery remains stored and protected-package audits are unchanged.

The first complete post-update attempt ended after103803ms with1008 unprotect
calls /53603ms protection, rather than the baseline's1226 /66813ms. It still
failed, so this is measured improvement, not successful registration. Closed
metadata now reaches `XPointOnionCapabilityProducer.VerifyDtt` at IL168 and the
UI reports Rejected. Exact packaged IL maps that offset to the existing
`dtt-binding-invalid` guard, not to a specific failed subcondition. All three
local nodes reported ready/recovered at06:13Z. One source hypothesis is a view
rotation between closure fetch and fresh DTT proof; it needs a signed controlled
reproduction before claiming a cause or changing acquisition. No further contact
send/Store, reset, deadline extension or physical acceptance is claimed.

The signed source race is now reproduced and corrected under
[DR-0128](../../docs/survival-program/decisions/DR-0128-coherent-network-view-acquisition.md):
Protocol `cacbd67` supplies the non-authoritative strictly-newer-view observation;
Shared `32de907` reacquires closure/own proof at most once under the existing
gate/budget, with complete verification and actual history/floor/CAS afterward.
Protocol observation14 / expanded204 and Shared acquisition5 / regression47
passed, native0; the Protocol Release solution build had0 warnings/errors.
Graph09 still fails the existing MAU2 shipping-token check, not waived.
The first APK29 retest is recorded above; Windows17 launch remains unobserved.
Old APK28's measured improvement is not evidence for this new acquisition branch;
sustained same-account recovery remains required. No server wire or production
deployment change is needed for this additive client observation API.

### Matched capped-successor correction

Windows16 and APK25 compile MAUI `7fb384d`, Shared `104b24b`, Protocol
`0867668`. APK25 SHA256 is
`98d69ca72fbe4d5d118139db7a2996a6746d74545ac76c35ad924b01653642c0`.
The exact signed APK assembly store was matched to its build manifest and
contains the corrected successor-chain predicate. Retained-update preflight /
Install01 passed. Windows16 apphost SHA256 is
`4a42fb442cb0ad067f12a85d47c3d63dd64f22766403fabeacb6f24f7695a3ee`;
MAUI assembly SHA256 is
`14a4a8f853c93e690487a7a287d4fc5c4a600c61442dc1921562b9e4fb83b1a8`.
The old QA window was closed through its UI and the new build opened the same
profile. Both settings views independently confirmed retained recovery.

The matching local server update and supported smoke passed without reset;
exact image and mount checks belong to the
[contact transport checkpoint](../../deep-devops/docs/S05-CONTACT-TRANSPORT-2026-10-15.md).
Android Verify01 -> independent Inspect02 reached verified-publication / Verified;
Windows16's explicit check also reached Verified with current proof saved.
This confirms same-account recovery past the earlier capped-successor guard,
not sustained readiness, a contact exchange or message delivery.

Android Contacts -> SetPeerAddress -> DismissKeyboard -> Add01/02 did not
observe Store or a business-failure record. The retry caption remained. Harness
`c9e6a7a` adds only exact-owned ScrollContacts; focused Clean05 passed23/0/0/native0.
Clean04 retained22PASS/1FAIL because its old static settings-ID assertion had not
yet been updated to require the closed settings/contacts mapping. After the
supported scroll, a closed UI read confirmed NetworkRequired=true, Busy=false,
not a successful contact dispatch. Settings later showed Unavailable. Verify02
-> Inspect05 ended failure-redacted; closed exact-process logs repeatedly show
Cancelled in local account/floor/compaction reads at the existing attempt bound.
An owned-process CPU snapshot reported44% CPU (35% user,9% kernel); it does not
identify the cause or prove network time authority. No deadline was extended.

Windows16 RefreshConversations really entered the network path after verified
registration, then ended with a closed business error. Metadata at04:59Z locates
OutcomeUnknown at PrivacyManagedIngressHttpTransport.ForwardAsync line304.
Existing bounded node diagnostics show two relay504/64-byte OutcomeUnknown
responses and the exit's ContactResolve custody-or-io failure. No correlated
coordination rejection was observed; absence of a log is not proof of absence.
The precise inner exit failure remains unproven. All originals, accounts,
recovery and node state remain retained. Contacts/text/files/images/groups
physical acceptance remains0/4; no new send or acceptance is claimed.

### Earlier retained-device observations

The next Shared candidate `acc65f1` carries independently verified contact
authority on one command's async stack and uses explicit existing borrowed
account/ONION custody for its single read and claim. Real protected floor,
endpoint, clock/window, selected-replica and final native custody checks remain;
no cache flag, implicit retry, new journal/wire or extended lifetime is added.
The owning mapping and exact limits are in
[owned contact start](../../deep-client-shared/docs/architecture/owned-authored-counter-custody.md#current-same-command-proof-and-owned-transport-candidate).
Its first focused run was11PASS/1FAIL/native1: a new bad-signature test expected
the wrong exception type. The failed receipt stays unchanged. The corrected/
expanded focused run passed8/0/0/native0 (`scoped-contact-start-02`), including
first claim/native completion/Store/cold exact retry, exactly two preclaim network
acquisitions, expired/cancelled/invalid replies, late response after lease end,
changed floor, expired proof and foreign-owner refusal. Existing eight affected
regressions passed on the same production source in the first run. MAUI Clean193
and Smoke119 passed0fail/skip/native0 (`scoped-contact-clean-01`,
`scoped-contact-smoke-01`). These are native in-process source fixtures, not
physical delivery, an Android speed measurement, or final full acceptance.
Matching Windows15 and APK22 built at MAUI `7cf9d21` / Shared `acc65f1` /
Protocol `605090b`; APK22 passed supported dry/install and restored the existing
profile with protected-package audits. Windows15 restored the same profile and
retained recovery status. Its explicit registration check reached Verified,
then background state returned to Rejected. The single earlier Chats refresh
was local-history-only, not a mailbox request. Android Add20 observed neither
Store nor retry-choice; do not infer dispatch or noncommit from that projection.
Settings/scroll/Verify27 subsequently observed a live verification, not success;
closed latest Android metadata identifies genesis-admission I/O. No further
business send or account reset followed. This build does not yet qualify the
scoped command physically. Older evidence below retains its original inputs.

The next same-account Windows registration check and Android Verify28 followed
by independent Inspect88 both ended Rejected with
`DID2 AccountProof failed (AdmissionAuthorityUnavailable)`. Android's supported
receipts retain recovery and report no contact Store or delivery. Windows was
observed and checked through its existing settings UI only. No create/delete,
business send, identity reset or recovery deletion occurred in this check.

Later local Node `7520654` diagnostics observed native generations468/455 and
then Retrieve456 on a retrying node. At03:00Z all three nodes independently
reported recovered mailbox plus ONION/contact readiness; supported smoke09
exited0. This is one current recovery check, not sustained/physical acceptance.
Android Verify29 -> Inspect89/90 preserved recovery but ended Rejected, now with
ProtectedState/Unclassified at contact-object authoring (AuthorClosureCoreAsync
IL2719 and AuthorGenesisCoreAsync IL315), not the preceding AccountProof503.
The matching build-output IL places that area near bundle authoring but does
not establish the exact guard in the packaged Android assembly. Do not reset
the account or infer a particular failing predicate from that offset alone.
Windows remained on its saved profile: both Verify clicks were rejected by the
Windows helper with `coordinate input geometry is unavailable`, including its
one recovery attempt. No click outcome/success is claimed; further Windows
input is stopped pending the operator restoring/maximizing that same window.

APK23 at MAUI `258a3e3` / Shared `acc65f1` / Protocol `605090b` was signed,
installed over the same QA package and launched without reset. Its SHA256 is
`709572933665f2ce4a12f779776c6f9452430e21e268224c729ddbdac3ef4fd7`.
Inspect92/93 retained the account/recovery; Verify02 finished Rejected with the
same ProtectedState/Unclassified authoring location. A first Verify phase had
no enabled control in the viewport and was rejected before input. An initial
protected-package audit transiently failed; the independent exact-device audit
and subsequent guarded inspection succeeded, without treating the failed phase
as success. No contact Store or delivery is accepted.
Read-only inspection of the exact APK assembly store, bounded and matched to
the build manifest, confirms the new closed classifier is packaged. Earlier
companion-only IL and a reader attempt with prematurely disposed code streams
do not prove Android binary identity; only the corrected exact-APK read does.
The code offset still does not identify an exact guard. A later Windows retry
again failed before input with the same geometry error; no account changes or
successful network check are claimed.

The subsequent screenshot-backed Windows recovery restored input geometry.
One VerifyNetwork action completed Rejected at ContactPublication /
OnionCompletionUnknown. Closed code metadata locates it at
PrivacyManagedIngressHttpTransport.ForwardAsync line304, handling an
unclassified/outcome-unknown HTTP error response. It is not a completed
publication, proven noncommit or evidence of an Android authoring failure.

APK24 (`3505482`, same Shared/Protocol) built without compiler warnings/errors;
SHA256 `80336e62f5a61966f556f2daa26223a9dde4f66ed557cadbf425aa1d6ad381ae`.
The first Install was refused before mutation because AllowProbeUpdate was
absent. Explicit retained-update preflight and Install02 then passed. Settings
confirmed retained recovery; the two guarded scrolls exposed VerifyNetwork.
Verify01 and separate Inspect03 ended Rejected, without contact Store/delivery.
Exact-PID/tag closed logs identify ContactSuccessorAdvance during local
successor authoring. The matching signed source reproduction and semantic fix
belong to [DR-0127](../../docs/survival-program/decisions/DR-0127-capped-contact-object-successor-validity.md).
APK24 predates that Protocol correction; no fixed-device success is claimed.

Supported current-dev Verify failed with Registry readiness timeouts/503;
independent node reads found all three mailbox hosts unavailable while ONION
and required contact terminals remained ready. Closed Registry NTS acquisition
logs contain quorum/interval/floor denials; no individual mailbox cause is
claimed. Recover this retained runtime before another contact retry.

The current local nine-service, three-node contour and source matrix are owned
by the [contact checkpoint](../../deep-devops/docs/S05-CONTACT-TRANSPORT-2026-10-15.md).
Android and Windows each reached `verified-publication` on retained accounts
with DR-0125 and the bounded Registry admission fix. Android's first verification
after another process restart returned ContactPublication/TransportIo; preserve
that failure and the account. It is not a completed cold-reopen gate, loss of
commit, or permission to reset. Cross-device contacts/text/files/groups remain
unproven; the older dated observations below do not override current evidence.

Current installed-source matrix belongs to [NEXT-SPRINT](../../docs/NEXT-SPRINT.md).
MAUI `f6e57b7` keeps optimized Debug JIT/isolation/strict trust, regenerates native
registration nonincrementally and excludes only the unused QA production-package
Google Services resource. Clean186 and Smoke119 passed0fail/skip/native0 with
Shared `23fbca3`; the first smoke run changed HEAD mid-harness and is unqualified.
Matching fixed-source focused1 and smoke02 passed, without replacing that FAIL.
QA/default Debug resource counts0/1 and the unchanged Release rejection were
actually evaluated. These receipts do not qualify changed Shared inputs.

APK15's exact-QA JNI startup failure and recoverable generated-only cache are
preserved. Same-source fresh-cache APK16 started; APK17 was refused installation
after the harness advanced to a docs commit, and its package-mismatch warning
was real. Supported APK18/19 both built0warnings/errors and started without
manual cache cleanup or account reset. Optimized QA sandbox/run-as permissions
were not changed; future exact-process/tag logcat emits closed code metadata only.

The retained newer intent's retry13 failed at `XurWindow / RequireCurrentIssuer /
IL87`, without Store observed. Its StopContact03 on APK18 then proved
`ArithmeticOverflow / StopInitialContactIntentAsync / IL4274`. APK19 uses Shared
`cb7893c`, Protocol `605090b`; SHA256
`5a9d587a78e4e84be6a51ee36544d21fb657361bf75093691782a2702126456a`.
It restored the same account/intent, preserved all three protected-package
snapshots, and StopContact04 -> independent Inspect75 observed stopped retries,
no error and no StopRetry control. Shared focused13 covered ten arithmetic and
three real SQLCipher/native cancellation cases; the physical fix is confirmed
in this scope, without changed signed windows, reset, recall or noncommit proof.
Only the subsequent separate AddContact16 created new work. It failed reading
network closure (`Io / FetchCurrentAsync / IL453`); matched managed input places
this at the exhausted eight429 attempts. Registry `a4805bf` is now on the retained
local current-dev; signed readiness recovered, while its raw mount comparison
remains unqualified as recorded by the runtime owner above. Exact AddContact17
reuses that same intent. Inspect78 could not read the hierarchy; Inspect79
observed busy controls. Inspect80 then observed an error without Store; a new
closed logcat timestamp identifies `RequireClaimContactTimeAsync / IL509` during
claim preparation. Matching input maps this to a combined clock/boot/request
window guard, not a proven individual expiry predicate. It was not replayed as
another send. Log time is not trusted-time authority. No contact/message
delivery is claimed.

The next source candidate removes duplicate draft preparation inside one
`StartContactAsync` call: completion consumes that command's already owned
draft bytes, while its current protected draft/endpoint/claim checks still run.
Independent completion callers continue preparing their draft. The combined
claim-read guard now has four fixed diagnostic reasons with unchanged predicate
order/boundaries; no time values or identifiers are emitted. Neither source
change is installed evidence or a confirmed fix for the physical refusal yet.
Shared focused runs passed10/0/0 and2/0/0/native0, including the actual owned
application StartContact/receive/lost-ACK fixtures. MAUI `a2bc906` passed
Clean190/0/0 and Smoke119/0/0/native0 (`prepared-contact-clean-01`,
`prepared-contact-smoke-01`) with Shared `09f5c1d`, Protocol `605090b`.
Matching APK20 SHA256
`ed05ca5cd5ff379af9434537671ecf78f1574b97525b5641fce4e87a29437d9b`
was installed, launched and preserved the protected packages. Cold Add18 restored
the existing retry choice without dispatch. Add19 retried that same intent;
Inspect84 observed an error, no Store, retry/stop still available. New closed
metadata identifies `DeepIdV2PermanentContactResolveVerifier.VerifyAsync / IL1039`
before draft preparation. Matched input places it at the combined current
view/placement-hash/request-within-placement-lifetime guard. It does not prove
which predicate failed, or a completed speedup; no new intent was created.

Windows build11 retained its profile/recovery and reached background Verified;
one mailbox refresh at19:09:34Z ended in OutcomeUnknown at
`PrivacyManagedIngressHttpTransport.ForwardAsync / line304 IL1509`. Build12 with
Shared `cb7893c` is built but not launched: Windows automation was again stopped
by physical Escape. After a new explicit human resume, the same build11/profile
completed one UI Chats refresh on the current local contour: synchronization
completed, no visible error, empty conversations. This does not erase the prior
unknown outcome or prove contact delivery/noncommit. No new contact acceptance
or send occurred. Protocol
boundary/public-surface focused15 passed. Final full gates and physical
contacts/messages/files/groups remain open; none of these diagnostics is release
acceptance or permission to publish.

Windows build13 (`a2bc906`/`09f5c1d`) replaced build11 through normal close/launch,
without reset; the retained profile/recovery remained present. Its first refresh
failed `OutcomeUnknown / ForwardAsync / IL1509` at20:33:35Z, with no contact
delivery claimed. Background diagnostics then found a separate source bug:
`ReadBootstrapAsync` calls `HttpStageAsync("BootstrapAuthority")`, but the closed
I/O wrapper did not accept that stage. Its `ArgumentException` discarded the
transport cause and could stop reconnect as Rejected. The new two-case regression
failed exactly there for typed429/503 (`bootstrap-stage-red-01`, native1), then
Clean193/0/0/native0 passed after accepting the already used fixed stage
(`bootstrap-stage-clean-01`). Tests retain the typed cause, actual scheduling,
RetryAfter and existing malformed-floor no-retry coverage. The original red
receipt stays unchanged. This source fix is not installed/physically qualified
yet and does not fix the independent ContactResolve placement refusal.

Source `229c3da` subsequently passed Smoke119/0/0/native0
(`bootstrap-stage-smoke-01`). Matching Windows build14 SHA256
`b411f8a4e06fb4e5bcbcc0ff05227f2187722d53dd96e8308f2c748a765ad858`
was launched without reset, retained the same profile/recovery, reached actual
background Verified, then completed one Chats synchronization without visible
error. No transport fault was injected: this does not physically qualify the
typed429/503 recovery case or contact delivery. The settings registration-status
sentence still says not checked alongside the background Verified state; that
UI projection inconsistency remains open, not a false proof rejection.
Matching Android APK21 SHA256
`c312f46c95f27caf6ef94563670527f2fd7361bd0c6fdbb5904178aff3f7c16e`
passed dry-run, installed/launched, preserved protected packages and reopened
the existing workspace (Inspect85). No new contact retry was issued on APK21
yet. Its retained intent and APK20's original refusal remain intact.

`eng/Invoke-PhysicalDid2AccountProbeAndroid.ps1` adds HTTPS-only phases:
Contacts/SetPeerAddress/AddContact, Chats/RefreshConversations/OpenOnlyConversation,
AcceptContact, SetQaText/SendQaText/RefreshMessages. SetPeerAddress requires its
explicit public QA peer; SetQaText accepts only `DID2 Android QA 20261015 A`.
Inspect each fresh result, dismiss only an observed keyboard before another
control, and never repeat an unknown send. The result records only closed
statuses/marker-presence flags, not addresses, drafts or private error text.
Selecting an ambiguous list or replacing an existing draft rejects.

Dry-run before the first new phase. Current committed harness-only changes may
use `ExpectedApkSourceCommit` for the existing signed APK only under the exact
ancestor/changed-path checks; installation still requires a matching build.
This does not allow compiled source differences or a stale signing/CA pin.

One bounded same-account retry after the cold-reopen TransportIo reached
verified-publication; it does not establish the cause of that first IO failure.
The next observed blocker was UI readiness: after successful publication the
contact action stayed disabled, and Windows displayed its offline-history hint.
The source candidate now makes manual rechecks join the actual reconnect flight
and preserves advisory readiness during routine refresh, while real failure,
offline and account/lifecycle invalidation still clear it. No contact request or
message was submitted from the disabled controls. New matched builds and a real
two-device run are required; a historical APK cannot qualify this compiled change.

Matched `4fafe7d` Windows build02 and Android build03/install02 both reached
verified-publication on the retained profiles; Clean140 and Smoke119 passed.
Android's next public-peer input still removed UI readiness before any contact
submission. That observation is not a sent request, nor proof that keyboard focus
was the exact cause. Source review additionally found network suspension bound to
every Window.Deactivated and non-idempotent activation. The next candidate binds
actual Stopped/Resumed visibility, keeps activation idempotent, and displays only
the closed reconnect enum so physical retest can distinguish lifecycle, offline
and actual authority/transport failure. Clean142 passed.

Matched `661d318` Windows build03 and signed Android build04/install04 are now
running on the same retained profiles; Smoke119/0/0/native0 also passed. Android
Verify07 followed by independent Inspect20 reached verified-publication and
Verified. Public-peer input03 and keyboard dismissal03 both left Contacts.Add
enabled. Exactly one AddContact action was submitted; Inspect22 found a terminal
messaging error without contactRequestStored. Do not repeat the unknown request,
infer non-delivery, reset the profile or count an enabled button as exchange.

Windows performed two bounded mailbox refreshes, without contact/text submission.
The second ended with the protected-state rejection. A short QA-process-only
exception trace localizes its exception chain to RequireRetainedRequestWindow
via RestoreRetainedReadRequestAsync, owned retained grant acquisition and
SynchronizeOwnMailboxAsync. Only exception types/method names were inspected;
private trace bytes are outside the repositories. This is a current-window
rejection while restoring pending Retrieve custody, not proof of a bad signature
or mailbox emptiness. DR-0084 already defines closed-unresolved acquisition and
late-result/namespace settlement; its internal producer is not composed into
ordinary namespace maintenance. Audit that exact closure/recovery path next,
preserving original request/holder/floors. Do not extend the120s window or infer
non-issuance from expiry. No Android failure cause is established by the Windows
trace. Physical contacts/text/files/groups remain0/4; production was not changed.

Matched Shared `d2ef134` / Protocol `f0ef288` / MAUI `fc7b86a` Windows build04
and Android build06/install06 preserve those same accounts. Windows reached
verified-publication after a bounded manual verification; two mailbox refreshes
still failed. Its second bounded private exception trace confirms the closure
composition ran, then `AcquisitionForNewWork` refused the retained closed tail.
Android refresh01 also ended with a terminal messaging error; no contact request
was repeated. Build05 failed its expected-source preflight before compilation or
installation, not on the device. Matching source Smoke119 passed.

[DR-0126](../../docs/survival-program/decisions/DR-0126-independent-grant-after-unresolved-acquisition.md)
corrects that new-work selection in Shared, preserving old uncertainty and every
active transport intent. Focused16 and affected regression58 passed; new matched
builds and retained-device retests remain required. The older APK's MAUI-only
ancestor guard cannot qualify a changed Shared/Protocol dependency. Bounded
mixed-history retirement is also still open. This is not a device-delivery PASS.

Matched Shared `72a86c5` / Protocol `0f295cd` / MAUI `9304da3` Windows build05
completed its first mailbox refresh with synchronization completed, no error and
an empty conversation list on the retained profile. Background state was Verified;
recovery remained stored and hidden. The exact executable/assembly hashes and
dependency commits are in `artifacts/s05-current-publication-20261015/windows-observation-02.json`;
that is a manual observation, not a qualified release receipt or contact delivery.

Android build07/install07 preserved the same account/recovery and all three
protected packages. Verify08/Inspect26 reached verified-publication/Verified;
refresh02/Inspect28 then ended with an incomplete-redacted messaging error,
not the UI cancellation/integrity category. Its actual inner cause is unproven.
Subsequent settings inspection observed background Rejected and Contacts.Add
disabled even after the exact Windows QA peer input and keyboard dismissal.
No second AddContact or text send occurred. Source Smoke119 passed. Continue
diagnosing that retained device; do not infer noncommit, relax authority, reset
the account or transfer the successful Windows read claim to Android.

Manual Verify09/Inspect30 recovered verified-publication/Verified. Contacts05
then observed the retained exact peer and Add enabled; one AddContact02 retry
ran without resetting/replacing the logical intent. Inspect32 ended in the closed
operation-cancelled UI category, without contactRequestStored. No third send
occurred. The existing UI already cancels active business work when background
verification loses readiness. Source review found those two pipelines were not
serialized despite sharing protected account/network custody; a MAUI-only
coordination correction is being qualified. Neither this observation nor a
scheduler unit test proves that concurrency was this physical failure's cause.

Matched MAUI `845a666` Windows build06 completed another real empty mailbox
refresh without error on the retained profile (`windows-observation-03.json`).
The bounded private trace05 contains only general IO/cancellation and scheduler
wait cancellation, not the former acquisition-selection rejection. Android
build08/install08 retained account/recovery/protected packages; Inspect33
observed automatic Verified, peer input05/dismiss05 left Add enabled. Exactly
one AddContact03 retry ran on the same intent; Inspect35 ended in the closed
protected-state-rejected category, without contactRequestStored. No fourth send
has occurred. This is not the prior cancellation category, and its precise
source guard remains unproven. Source Clean150 and Smoke119 passed. A QA-only
bounded metadata observer is being qualified to locate that error without
logging exception text, payloads, identities or file paths. No reset or crypto
guard weakening is authorized by this observation.

QA-only observer MAUI `ab1b15f` compiled with Windows0 warnings/errors and
Clean160; Android build09/install09 preserved the profile. AddContact04 reused
the same intent and Inspect37 again ended with a protected-state UI failure.
Bounded sandbox metadata locates it in
`ApplicationCoreVerifier.RequireContactHelloEndpointBindingsAsync` called by
`PrepareInitialContactDraftAsync` / `PrepareOwnInitialContactDraftAsync`.
Android supplied no PDB source line. No fifth send has occurred. The next QA
metadata slice distinguishes the existing endpoint/XUR/freshness guards and
records a static IL offset; it does not loosen any of those checks or authorize
retained/historical facts as new-work authority. Windows independently completed
another empty read after the attempt; absence of a local conversation is not
remote noncommit evidence.

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

The signed `e745b63` Android update (APK SHA-256
`2a5c1c8d1016eddfd392535bfb675351f44b114940d05186aaf2016f7605ac14`)
was installed only over the dedicated HTTPS QA package. The existing account
and encrypted recovery reopened without reset; all three protected package
snapshots matched before/after. One guarded network action then rejected
`XRA1 / Expiry`, confirmed by independent terminal inspection. Thus the earlier
fresh-account success is not durable account recovery after route expiry.
DR-0051 expired-intent renewal remains the next product correction; do not
reset the account, silently replace stable intents/nonces, relax expiry, or
claim current Android publication/delivery from its prior completed action.

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

## Retained-device retry — 2026-10-03

The proxy-aware `e745b63` Windows client was observed, then its network action
was repeated once through the UI. It completed with `XRA1 / Expiry`, with the
same visible QA identity and encrypted recovery retained. No account reset.
Guarded USB Android Inspect -> VerifyNetwork -> Inspect also completed with
`XRA1 / Expiry`; encrypted recovery remained retained and all three protected
package snapshots matched. Immediate post-action `verifying` was not success.

These installed candidates predate the issued-head and object/publication
successor APIs. No same-account renewal, peer/Hello/Accept, text/files/images or
groups delivery is established. The next connected step is protected successor
adoption and permanent per-generation private issuance, followed by matched
build/deployment and a retained-account recovery run. Production was not changed.

## Proxy/VPN retained-account recheck — 2026-10-03

With the Windows QA window made available by Mr. X, the installed `e745b63`
client was reobserved and its network action invoked once. The initial displayed
`XRA1 / Expiry` was a previous result, not the new action's outcome. Independent
terminal inspection of this retry found `NetworkVerification / SecureConnectionError`
with `IOException` and the diagnostic `Chain PlatformChainAccepted`. This records
a protected-connection failure despite platform chain acceptance; it neither
proves a certificate defect nor identifies VPN/proxy as its exact cause. One
initial click failed because geometry was unavailable; selection/state were
refreshed before the single successful input. No further Windows input occurred.

USB Android Inspect -> VerifyNetwork -> independent terminal Inspect, after
Mr. X enabled VPN on the phone, finished at `XRA1 / Expiry` again. Immediate
`verifying` was not counted as success. All three protected package snapshots
matched before/after each phase, and encrypted recovery remained retained.
Neither account was reset, deleted or recreated; no recovery phrase was revealed.
These are network/recovery diagnostics of the older installed candidates, not
same-account successor recovery or contacts/messages/files/images/groups E2E.

## Authorized Windows retry on retained account — 2026-10-03

Mr. X provided a five-minute no-input interval. Fresh observation found the
previous NetworkVerification failure; one initial input failed for unavailable
geometry and was not treated as completion. A screenshot-backed refresh then
preceded exactly one successful VerifyNetwork input. Independent subsequent
inspection found `PreKeyPublication / SecureConnectionError`, `IOException`
and the reject-only diagnostic `Chain PlatformChainAccepted`. This moved past
the previous stage but did not complete publication or identify the exact cause
of the protected-connection failure. The same QA account and encrypted recovery
remained present; no reset, phrase reveal, deletion or VPN-setting change.
The installed `e745b63` candidate still predates connected successor custody;
this result is not same-account renewal or messaging/assets/groups delivery.

## Retained Windows/Android retry reaches route expiry — 2026-10-03

Mr. X renewed the five-minute no-input interval. Selected exactly the returned
running DID2 HTTPS QA window, independently observed the previous failure,
then submitted one VerifyNetwork action without reset. A subsequent separate
observation found `XRA1 / Expiry`, not the former protected-connection error.
The stored account and encrypted recovery remained intact; no phrase reveal,
deletion, account creation or VPN setting change. This proves only that this
attempt progressed to the route-time check, not the exact cause of earlier IO.

Guarded USB Android VerifyNetwork followed by a separate terminal Inspect also
reported `route-time-coverage-rejected`, `XRA1 / Expiry`, verifying=false and
recoveryRetained=true. Protected package snapshots matched. No install/reset
or production mutation occurred. Both installed candidates remain the recorded
older source, not the new protected successor implementation. Full contacts,
messages, attachments/images and groups physical E2E remain unproven.
