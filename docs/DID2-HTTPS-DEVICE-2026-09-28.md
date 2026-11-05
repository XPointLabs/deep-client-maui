# DID2 physical harness boundary

This path remains in the installed-APK provenance allowlist of
[Invoke-PhysicalDid2AccountProbeAndroid.ps1](../eng/Invoke-PhysicalDid2AccountProbeAndroid.ps1).
A documentation-only change can be distinguished from a runtime change; the
allowlist does not qualify an older APK as matching current runtime source.

Current composition is described in [ARCHITECTURE](ARCHITECTURE.md), and supported
platform/release claims belong to [the current specification](../../docs/architecture/README.md#acceptance).
Physical account diagnostics do not prove contact/text/group/attachment delivery.
Device acceptance remains unreceived; retained QA profiles and original pending
operations must survive supported update/recovery without replacement or reset.

The driver requires exact device/APK/source/signer provenance, foreground ownership,
bounded individual actions and before/after protected-package observations.
A timeout or OutcomeUnknown cannot infer noncommit or authorize a new intent.
The earlier Windows interaction was stopped by the user's Escape; this document
contains no instruction to resume that physical interaction.

Historical builds, observations and rejected receipts are available in
[the pinned Git ledger](https://github.com/XPointLabs/deep-client-maui/blob/7a49211bd862e20d0ac69db8b58cc1a5353871a5/docs/DID2-HTTPS-DEVICE-2026-09-28.md).
