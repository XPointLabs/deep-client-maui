# Business observation and physical provenance boundary

This documentation path is retained by the Android installed-APK source allowlist
and its CleanStartupComposition test. Product architecture and current release
state belong to [the current specification](../../docs/architecture/README.md#acceptance).
This note is not an alternative sprint queue or device acceptance record.

The QA-only observer records closed ContactStart/ContactStop/ContactAccept,
TextQueue/TextRetry/MailboxSync totals: elapsed time and actual protection counts/time.
It emits no addresses, slot names, keys, message contents, raw exceptions or trace
headers. Observation preserves original tokens/exceptions and grants no authority.
A source counter or background verification total cannot establish command success,
remote noncommit, or the sole cause of a physical timeout.

Matching APK/Windows binaries, signed inputs, retained account/native custody and
original operation must be established before drawing a device conclusion.
No earlier receipt extends the signed window, replaces an uncertain intent,
relaxes a protected-floor check or qualifies current shipping composition.
Actual protected package provenance takes precedence over a filename/version label.

Historical exact build hashes, original failed runs and matching observations
remain in [the pinned Git ledger](https://github.com/XPointLabs/deep-client-maui/blob/7a49211bd862e20d0ac69db8b58cc1a5353871a5/docs/S05-BUSINESS-WORK-2026-10-17.md).
