# S05 — foreground scheduling deadline

The retained Windows build20 UI stayed Busy during a contact operation, while
its window continued responding. No duplicate submit or account reset followed.
A bounded live managed-stack observation captured only code names in memory,
not a heap dump or application payload. It did not identify the async wait;
do not claim a deadlock, exact cause or successful contact Store from it.
After activating the selected QA window again, an independent observation found
the terminal protected-state error. Its closed log reports
`ProtectedDid2MailboxGrantJournal.OriginalCeiling`, line624, during Initial
Deposit acquisition, at17:14:20Z. Thus the earlier Busy observations do not
measure the actual duration or prove an infinite wait. Matching build20 DLL/PDB
subsequently mapped IL758 to the no-possible-interval rejection, not the compound
binding check at line616. The Shared logical-ceiling/current-policy correction
is recorded in [its checkpoint](../../deep-client-shared/docs/testing/s05-logical-grant-custody-2026-10-16.md).
That rejection is separate from the independently verified scheduling defect;
the deadline change does not repair it or authorize replacing the saved intent.

Source audit independently found that `RunForegroundAsync` had no active-attempt
deadline, unlike the background pipeline's two-minute budget. It now cancels
the actual foreground token after owning the lane. Queue time is excluded,
existing account/offline/suspend cancellation remains, and the delegate must
drain before releasing the lane. A timeout neither proves noncommit nor changes
the original request/ciphertext/intent, crypto lifetime, protected floor or retry
authority. No background auto-send or replacement work is introduced.

Receipts: `artifacts/s05-foreground-deadline-20261016/`.

- `red-01`:1PASS/2FAIL/native1. The two active-timeout cases failed because no
  cancellation was delivered; the queue-time case already passed. Cleanup drains
  the original delegate, and the original failure receipt remains unchanged.
- `focused-02`:49PASS/0FAIL/0skip/native0. Reconnect and messaging ViewModel cases,
  including actual token cancellation, queue-time exclusion, lane ownership,
  uncooperative/late completion and forbidden timeout bounds.
- `clean-03`:254PASS/0FAIL/0skip/native0, the entire unfiltered MAUI Clean
  project. Both final builds contain no compiler warning/error. This is not a
  canonical Shared/Protocol/Node/Registry full or shipping-package qualification.

Windows PowerShell5.1, SDK10.0.301. Matching device builds/retest and canonical
source/package/business qualification remain separate. Installed build20/APK37
do not contain this change. Contacts/messages/files/groups physical acceptance0/4.

Matching Windows build21 from `267de20` completed native0, with no compiler
diagnostics. It has not been launched: the old QA window was minimized and its
bounded selection recovery repeatedly detected manual input. No close, reset or
input to another application followed. Build21's apphost SHA256 is
`96ff5b69e16618693a28ef935de16919184774e5de1acd3227a50a11e4830e62`;
assembly SHA256 `3bbc4703a52af4f30dc7c42202e39def926b39d99fd49f2ba0a00697ee0c4904`.
The existing build20 profile and operation remain untouched; do not count build21
as installed device evidence or evidence for the separate Deposit guard repair.
