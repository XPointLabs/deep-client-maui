# S05 — foreground scheduling deadline

The retained Windows build20 UI stayed Busy during a contact operation, while
its window continued responding. No duplicate submit or account reset followed.
A bounded live managed-stack observation captured only code names in memory,
not a heap dump or application payload. It did not identify the async wait;
do not claim a deadlock, exact cause or successful contact Store from it.

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
