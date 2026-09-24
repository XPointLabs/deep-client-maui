# DID2 Android physical account gate — 2026-09-24

This is an account-only UAT result, not a messaging, production-provider, or
release E2E result. No GitHub Release was published.

## Provenance

- Source: `deep-client-maui` commit `7cb0c92c4b639c558343229c20461cdf3ac92dd0`.
- Build: Debug `net10.0-android`, `DeepLocalDev=true`,
  `DeepDid2AccountProbe=true`; 0 warnings and 0 errors.
- APK SHA-256: `41d18390ff1da29bf7c69b7e03aa1e9cf9f66abd5087ca33d8c80b7b4a13b420`.
- Application ID: `network.xpoint.deep.did2probe`.
- APK signer certificate SHA-256:
  `9fc17b2ba9e799700fa60f198da0defab5806f51db763a7a1fc321dc2f5886b9`
  (Debug/UAT signer; not production approval).
- Device: physical Samsung SM-G970F, Android 12 / API 31; serial SHA-256:
  `646b5db4cc1367dedc94250521132caa90717404358558f5fac2b5aa025d083f`.

## Observations

The read-only preflight checked the exact serial, source commit, APK digest,
signer and package ID. A dedicated-package update then completed. Before/after
package-path and stable install-metadata digests matched for both
`network.xpoint.deep` and `network.xpoint.deep.e2e`. These snapshots do not
inspect the contents of either app's private data.

The real Android UI created a DID2 test account from a display name and one
button press. `Settings.Identity` appeared; its SHA-256 was
`96bc2c925db6556588fb1e1dbce46644e7f0ccab25a1dbe3c6781eb1e75acba3`.
The dedicated package had `deep-store-v2` and no `deep-store-v1` directory.
After force-stop/relaunch, the identity digest was unchanged, the retained
recovery phrase was hidden, and Reveal was enabled. Revealing showed exactly
24 words; the words themselves were not logged or added to this repository.
Hide was tapped and the phrase remained hidden after relaunch. The test
account's retained phrase was then deleted through the application's
confirmation dialog. After another relaunch, the DID2 digest remained unchanged
and both Reveal and Delete were disabled. The temporary UI dump containing the
revealed phrase was removed immediately and confirmed absent.

## Limits and follow-up

An earlier probe APK lacked the ML-DSA Android asset and showed a fail-closed
startup error. The APK above includes `assets/deep-native/libdeep_mldsa.so`,
and the app stages it through the existing exact-hash native-asset boundary.
The first package audit used full `dumpsys` output, which includes volatile
global counters. The bounded install script now hashes only stable exact-package
fields; the successful update matched those fields before and after.

Focused clean DID2 tests passed (3/3). The legacy
`Deep.Client.Maui.ViewModels.Tests` project currently does not compile because
it references removed V1 `Deep.Client.Shared.State` types; it is not a passing
release gate. Contacts, messages, attachments, groups, Windows physical UI,
production authority and production transport remain unverified by this
account-only test.
