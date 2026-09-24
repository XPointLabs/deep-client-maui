# DID2 Windows account probe — pending physical UI

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
