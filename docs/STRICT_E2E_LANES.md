# Strict client E2E lanes

Last updated: 2026-07-18.

Program revision: `ca5ad9f0c9d4dfb509dedcbf8133524c15867fce5534816da21ff86a07057383`.

## Purpose

Ordinary developer test runs may skip tests that need live infrastructure or a real desktop/device. A strict lane may not do that: missing endpoints, executable, device, runner, or configuration is a blocking failure, and the lane checks that the executed test count is nonzero and the skipped count is zero.

Machine-readable preflight and result files are written below `artifacts/survival/I01-MAUI-STRICT-GATES`. They contain check names and status only; endpoint values, identities, recovery material, tokens, and absolute user paths are excluded.

## Windows UIA3 spike

Build the Debug Windows app first, then invoke:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane WindowsUi `
  -AppPath .\src\Deep.Client.Maui\bin\Debug\net10.0-windows10.0.19041.0\win-arm64\Deep.Client.Maui.exe `
  -Bootstrap stub
```

The lane launches the actual MAUI executable and uses in-process FlaUI/UIA3 automation. It waits for a nonzero top-level window, fills the display name, invokes Create, and proves that an authenticated Conversations/Desktop Workspace root replaces Welcome. Each run uses a mandatory unique Debug-only application-data directory for both `stub` and `live` bootstrap.

Raw screenshots are disabled by default. `-CaptureStubWelcomeFailure` permits a raw bitmap only for an explicit stub Welcome failure; it is written below `windows-ui/quarantine/raw` and is never standard or uploadable evidence. The standard tree contains only allowlisted automation IDs and control types.

`stub` is an explicit UI-render bootstrap only. It is compiled behind `DEBUG`; Release configuration continues to reject stub transport and non-loopback cleartext URLs. This spike does not create accounts, self-chat, or claim message E2E.

Use `-Bootstrap live` only when all live endpoint variables are present.

## Live client acceptance

Set `XNODE_URLS` to exactly three distinct `<64-hex-routerId>|<absolute-url>` entries, plus `DEEP_FILE_URL`, `DEEP_PUSH_URL`, and `DEEP_CALL_SIGNALING_BASE_URL`, then run:

```powershell
.\eng\Invoke-StrictClientLane.ps1 -Lane LiveInfrastructure
```

Without `DEEP_STRICT_LIVE=1`, the live xUnit acceptance test is explicitly reported as `NOT-RUN` instead of silently passing.
`DEEP_STORAGE_URL` cannot satisfy this release lane. Direct storage has a separate opt-in diagnostic contract (`DEEP_STRICT_DIRECT_STORAGE=1`) and is never routed evidence.

## Android device lane

Build the Debug APK, attach exactly one authorized emulator or physical device, and invoke:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane AndroidDevice `
  -ApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep-Signed.apk `
  -AndroidRunner C:\trusted-tools\deep-android-runner.exe `
  -ConfigureAdbReverse
```

The preflight supports an explicit `-AndroidSerial` and configures `adb reverse` for local UAT ports when requested. The runner receives argument-list values for serial, APK, artifact directory, result JSON, JUnit, and commit SHA. The lane passes only when the runner exits zero, emits both files, binds the result to the current commit, and reports `executed > 0`, `skipped = 0`, and `failed = 0`. Missing runner/device remains `blocked`; building an APK is never counted as execution.

## Release evidence gate

`.github/workflows/strict-release-evidence.yml` invokes all three wrappers on the dedicated self-hosted Windows/device lab and then runs `eng/Test-StrictClientEvidence.ps1 -RequireComplete`. The validator rejects stale, commit-mismatched, counter-invalid, or payload-tampered evidence. Normal developer CI only publishes `NOT-RUN`; direct `dotnet test` or skipped tests cannot satisfy this gate.
