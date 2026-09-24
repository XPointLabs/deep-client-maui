# DID2 Windows account probe — account surface observed

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
