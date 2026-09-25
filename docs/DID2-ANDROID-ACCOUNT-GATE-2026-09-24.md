# DID2 Android physical account gate — 2026-09-24

## 2026-09-25 — account-scoped device-state remount

- The current dedicated Debug probe at MAUI commit `3802d50bd4fe8d9cf0082031ddb8a47445ebe127` built for Android with `DeepLocalDev=true` and `DeepDid2AccountProbe=true`: zero errors; one warning because the production Firebase configuration has no entry for the isolated probe application ID.
- Exact signed APK SHA-256: `81acc42fceae937a6e451a7fe24d3e8c6073ea45186e7dec5f36c1fe3353dca8`; the dedicated UAT signer remained `9fc17b2ba9e799700fa60f198da0defab5806f51db763a7a1fc321dc2f5886b9`. The bounded install script passed preflight, updated and launched only `network.xpoint.deep.did2probe`, and found the production and `.e2e` package snapshots unchanged.
- The prior isolated probe account failed closed with an incompatible protected current-account index. After explicit probe-only UI reset, a new local account was created by entering a test name and pressing Create. The app-private `deep-store-v2` directory contained both the DID2 SQLCipher account database and the separate encrypted device-state database. No recovery phrase was displayed.
- A physical force-stop/relaunch retained the same 90-character DID2 (SHA-256 of its UI text `27b693080ef9b58816e4b08c1782046d298426c4b8850b375a33f6d2fd71adb1`) and returned to Settings without an account error. The temporary Android UI hierarchy dump was deleted after verification.
- This closes only Android account/device-state restart continuity for this source/APK. The probe intentionally does not expose messaging, attachments, groups or production transport; none of those device E2E gates is claimed.

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
`Deep.Client.Maui.ViewModels.Tests` project did not compile because it
referenced removed V1 `Deep.Client.Shared.State` types; it has since been
removed from the release checkout. Contacts, messages, attachments, groups, Windows physical UI,
production authority and production transport remain unverified by this
account-only test.

## DR-0007 capability-commitment retest

- Source: `deep-client-maui` commit
  `d36c0e6a4515fd7f8fe38e904853b40e772a4244`, with the platform root
  pinning the clean-break `deep-protocol` and `deep-client-shared` commits.
- Debug DID2-probe APK SHA-256:
  `673a93b39a9dda64db309b88fff79afaefa7e191185f2943ce58410db7e6a0ba`;
  Debug signer certificate SHA-256 unchanged at
  `9fc17b2ba9e799700fa60f198da0defab5806f51db763a7a1fc321dc2f5886b9`.
  The Android build passed with 0 warnings and 0 errors.
- Read-only preflight and bounded dedicated-package update succeeded on
  `RF8M2082TFF`. Production `network.xpoint.deep` and UAT
  `network.xpoint.deep.e2e` package path/metadata digests were identical
  before and after; neither package or its data was cleared or reinstalled.
- The old raw-capability DID2 account failed closed. The probe's own reset
  button and confirmation removed only its isolated test account. A new
  `Android QA DR7` account was created from a display name and one tap.
- The new compact Deep ID was 90 characters. Its SHA-256 was
  `b544ddfa361e8dee214ed25e137155d158f243c4dcd4acb594548641bd9caeb3`
  on creation, after force-stop/relaunch, and after deleting the retained
  phrase through the application's confirmation followed by another
  force-stop/relaunch. Reveal and Delete were then both disabled. No phrase,
  raw resolver capability or address text was logged or committed; temporary
  UI hierarchy files were removed from the device after each observation.

This closes the physical Android **local account** continuity gate for
DR-0007 only. Windows account continuity, live Registry admission/proof and
Windows↔Android contact/message/media/group E2E are still open.
