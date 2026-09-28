# DID2 Windows account probe — account surface observed

## Windows canary network isolation

The loopback canary now uses a separate `DeepDid2CanaryProbe/<network-id>`
local custody root. The ordinary development account probe still uses its own
root; neither mode reads, migrates, resets or aliases the other mode's account.
Switching a build from the development network to a canary must not reuse its
STORE-V2 state. A canary account is created separately after operator approval.
This diagnostic remains account/proof-only, not a messaging release candidate.

The corrected canary built for Debug Windows ARM64 with zero warnings/errors.
Its apphost SHA-256 is
`4B76713F0FA257DED9AF3E437909A7D70F19257838975248C266AA7C2AAFB8F6`;
the application DLL SHA-256 is
`AEFEDEA58BC3CC233017821695D4AA60E921D516402DD6A10B4379AC7E3A792A`.
The observed interactive window displayed the empty welcome/create-account
screen without an incompatible-account error. No canary account was created
at this observation; Windows live admission/proof remains unverified.

## 2026-09-25 — current-source device-state and phrase-deletion check

- The dedicated `win-arm64` Debug probe built from MAUI code commit `3802d50bd4fe8d9cf0082031ddb8a47445ebe127` with `DeepLocalDev=true` and `DeepDid2AccountProbe=true`: zero warnings and zero errors. The exact executable SHA-256 was `f25cd5a2b95710af9384187a7e587a5f071df6e7013cf573ac48777a14810702`.
- The previously isolated probe account failed closed because its protected DID2 current-account index had an incompatible scope/version. After explicit operator approval, only this probe account was reset through its confirmation UI. The production and `.e2e` applications were not touched.
- A new test account was created by entering a display name and pressing Create. The private probe root contained both `deep-store-v2-account.dsv2` and `deep-store-v2-account.dsv2.devices.dvs1`. After closing and relaunching the exact executable, the same 90-character DID2 was displayed (SHA-256 of its UI text `d0bb4b61e0b3f4540bd2ffc119be83ec5f186c36f8f3da79eab8b989ce2b0799`).
- With separate operator approval, the retained local recovery phrase was deleted through the probe UI without revealing it. A further close/relaunch kept the same DID2 and showed the deleted status with Reveal and Delete disabled. No phrase was printed, copied or logged.
- This closes only Windows local-account/device-state restart and phrase-deletion continuity. This account-only probe explicitly disables contacts, messaging, attachments and groups; no Windows↔Android device E2E is claimed.

The isolated Windows Debug probe at `deep-client-maui` commit
`2e1869ba40dfcde1806f858805f3e0d97633758e` built for
`net10.0-windows10.0.19041.0/win-arm64` with `DeepLocalDev=true` and
`DeepDid2AccountProbe=true`: 0 warnings, 0 errors. Its account state and
diagnostics resolve to the private, dedicated
`%LOCALAPPDATA%/XPointLabs/DeepDid2AccountProbe` directory, not the normal
MAUI application-data root.

This is **not** a Windows physical account result. The Windows computer-use
launcher timed out before exposing a window. The exact newly spawned probe
process had one suspended thread, no window and no account-data directory;
that launch artifact was stopped. The existing packaged `.e2e` client process
was not stopped or modified. No Windows account was created, and no Windows
contacts, messages, attachments or groups were tested. Re-run the physical
Windows probe from a targetable interactive window before counting this gate.

On a later retry, the same isolated executable did expose one targetable
window. The captured surface was the Windows lock screen, not the MAUI
application. Computer-use stopped without input; no unlock or account action
was attempted. The gate remains pending an unlocked interactive desktop.

On 2026-09-24, the desktop was unlocked and the already-running isolated
`win-arm64` probe exposed its MAUI welcome window. The observed accessibility
tree and screenshot showed the display-name field and local account-creation
button. No account-creation action had been taken at this observation, so this
is a UI-availability result only; it does not close the Windows physical
account, contact, message, attachment or group gates.

At a subsequent read-only inspection on the same date, the isolated probe
showed an existing `Windows QA` account and recovery-phrase show/copy/hide/delete
controls. The phrase was not revealed or copied during inspection. This proves
only that the account/settings surface is visible in the running process; the
inspector did not witness creation or restart persistence. The screen itself
states that contacts, messages, attachments and groups are unavailable in this
account-only probe. None of those Windows↔Android device E2E gates is closed.

## Current-source restart and rebuild check

The operator explicitly reset the isolated incompatible probe account and
created a new test account through the application UI. The inspector did not
witness those two actions and makes no creation-flow claim. Before and after a
window close/relaunch, the probe showed the same 90-character DID2 in
`Settings.Identity`; the retained phrase remained hidden and its Reveal
control remained enabled. The phrase was not opened, and neither value was
printed or persisted in evidence.

The exact current source at `0a76c1af089836033cedc3164d8f118e4d980fb4`
then built as Debug `net10.0-windows10.0.19041.0/win-arm64` with
`DeepLocalDev=true`, `DeepDid2AccountProbe=true`, sequential compilation and
zero warnings/errors. The rebuilt executable SHA-256 is
`b9cc7d976be490d73cec53ed3791620212b7c7e2b69d64db69df3c799b6ef02d`.
Launching that exact executable reopened the same DID2, with no startup error;
the recovery phrase remained hidden and transport-unavailable status remained
explicit. This closes the Windows local-account **restart continuity** check
only. It does not prove observed account creation, production composition,
Registry admission/proof, contact, messaging, attachments or groups.

## Portable runtime startup check — 2026-09-28

The previously framework-dependent ARM64 publish failed before MAUI startup
with `REGDB_E_CLASSNOTREG` in the Windows App SDK Deployment Manager initializer.
The unpackaged project now carries both .NET and Windows App SDK, omits package
deployment initialization and retains SDK-owned registration-free activation.
MSBuild evaluation confirms those properties are scoped to the Windows `None`
profile, not Android or the optional MSIX lane.

The Debug ARM64 account-only probe built with zero warnings/errors. Its exact
executable SHA-256 is
`05a6ef73cdb7586e75704e8ff5a73bcd662ae08de372fd5672bf2b9f038c2092`.
The real Windows window opened after runtime loading and displayed the existing
local test account. Recovery remained deleted and both recovery controls remained
disabled; no phrase was revealed, no account was reset or created. The identifier
is omitted from this evidence. The UI still explicitly states that verified
DID2 transport is unavailable.

The complete smoke assembly passes 119 tests, including the portable runtime
configuration contract; the clean account assembly passes 19 tests. This closes
local Windows startup and account-state continuity only. It is not an APK test,
signed portable release, TLS publication, messaging or cross-device E2E claim.
