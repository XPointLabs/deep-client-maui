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

The lane launches the actual MAUI executable and uses in-process FlaUI/UIA3 automation. It waits for a nonzero top-level window and verifies the current Welcome page, display-name field, Create button, and Restore button. On failure it writes a sanitized screenshot, UI tree, and JSON result. Each run uses a unique Debug-only application-data directory.

`stub` is an explicit UI-render bootstrap only. It is compiled behind `DEBUG`; Release configuration continues to reject stub transport and non-loopback cleartext URLs. This spike does not create accounts, self-chat, or claim message E2E.

Use `-Bootstrap live` only when all live endpoint variables are present.

## Live client acceptance

Set `XNODE_URLS` or `DEEP_STORAGE_URL`, plus `DEEP_FILE_URL`, `DEEP_PUSH_URL`, and `DEEP_CALL_SIGNALING_BASE_URL`, then run:

```powershell
.\eng\Invoke-StrictClientLane.ps1 -Lane LiveInfrastructure
```

Without `DEEP_STRICT_LIVE=1`, the live xUnit acceptance test is explicitly reported as `NOT-RUN` instead of silently passing.

## Android device lane

Build the Debug APK, attach exactly one authorized emulator or physical device, and invoke:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane AndroidDevice `
  -ApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep-Signed.apk `
  -ConfigureAdbReverse
```

The preflight supports an explicit `-AndroidSerial` and configures `adb reverse` for local UAT ports when requested. No global Appium installation is performed. This package stops at the runner handoff and reports `blocked` with a nonzero exit code until a later harness actually executes the supplied real on-device runner. A missing authorized device is also blocking. Building an APK is never counted as device execution.
