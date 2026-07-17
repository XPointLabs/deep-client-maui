# Strict client E2E lanes

Last updated: 2026-07-18.

Program revision: `ca5ad9f0c9d4dfb509dedcbf8133524c15867fce5534816da21ff86a07057383`.

## Purpose

Ordinary developer test runs may skip tests that need live infrastructure or a real desktop/device. A strict lane may not do that: missing endpoints, executable, device, runner, or configuration is a blocking failure, and the lane checks that the executed test count is nonzero and the skipped count is zero.

Machine-readable preflight and result files are written below `artifacts/survival/I01-MAUI-STRICT-GATES`. They contain check names and status only; endpoint values, identities, recovery material, tokens, and absolute user paths are excluded.

Create one lowercase 32-hex release invocation and pass it to all three lanes and the final validator:

```powershell
$releaseInvocationId = [Guid]::NewGuid().ToString('N')
```

Each lane generates its own distinct lane invocation. A result is valid only with the matching lane name, source commit, common release invocation, unique lane invocation, and final `passed` preflight. Missing, stale, cross-lane-copied, duplicated, or mismatched evidence is rejected.

## Windows UIA3 spike

Build the Debug Windows app first, then invoke:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane WindowsUi `
  -ReleaseInvocationId $releaseInvocationId `
  -AppPath .\src\Deep.Client.Maui\bin\Debug\net10.0-windows10.0.19041.0\win-arm64\Deep.Client.Maui.exe `
  -Bootstrap stub
```

The lane takes a canonical, repository-contained, no-reparse snapshot of the complete just-built payload before launch. It launches that exact executable, uses in-process FlaUI/UIA3 automation, and then requires the identical normalized path set, sizes, and hashes after the run. Duplicate/case-colliding paths, alternate separators, substitutions, and payload changes fail the lane. It waits for a nonzero top-level window, fills the display name, invokes Create, and proves that an authenticated Conversations/Desktop Workspace root replaces Welcome. Each run uses a mandatory unique Debug-only application-data directory for both `stub` and `live` bootstrap.

Raw screenshots are disabled by default. `-CaptureStubWelcomeFailure` permits a raw bitmap only for an explicit stub Welcome failure; it is written below `windows-ui/quarantine/raw` and is never standard or uploadable evidence. The standard tree contains only allowlisted automation IDs and control types.

`stub` is an explicit UI-render bootstrap only. It is compiled behind `DEBUG`; Release configuration continues to reject stub transport and non-loopback cleartext URLs. This spike does not create accounts, self-chat, or claim message E2E.

Use `-Bootstrap live` only when all live endpoint variables are present.

## Live client acceptance

Set `XNODE_URLS` to exactly three distinct `<64-hex-routerId>|<absolute-url>` entries, plus `DEEP_FILE_URL`, `DEEP_PUSH_URL`, and `DEEP_CALL_SIGNALING_BASE_URL`, then run:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane LiveInfrastructure `
  -ReleaseInvocationId $releaseInvocationId
```

Without `DEEP_STRICT_LIVE=1`, the live xUnit acceptance test is explicitly reported as `NOT-RUN` instead of silently passing.
`DEEP_STORAGE_URL` cannot satisfy this release lane. Direct storage has a separate opt-in diagnostic contract (`DEEP_STRICT_DIRECT_STORAGE=1`) and is never routed evidence.

## Android device lane

Build the Debug-only E2E APK, attach a dedicated managed emulator or physical test device, and invoke:

```powershell
dotnet build .\src\Deep.Client.Maui\Deep.Client.Maui.csproj `
  -f net10.0-android -c Debug -p:DeepPhysicalE2E=true

.\eng\Invoke-StrictClientLane.ps1 `
  -Lane AndroidDevice `
  -ReleaseInvocationId $releaseInvocationId `
  -ApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk `
  -AndroidRunner C:\trusted-tools\deep-android-runner.exe `
  -DedicatedManagedDevice `
  -ConfigureAdbReverse
```

Release always keeps `network.xpoint.deep`; the physical Debug lane accepts only `network.xpoint.deep.e2e`. It rejects a personal/unattested device and any device where the production package is present, and never reads, cleans, or modifies that production package.

The preflight supports an explicit `-AndroidSerial` and configures `adb reverse` for local UAT ports when requested. The runner must bind its result to the source commit, both invocation IDs, APK SHA-256, package/version, signing certificate, selected serial, its own binary hash/version, JUnit hash, and dedicated-device cleanup attestations before and after execution. JUnit counters are independently parsed and cross-checked. Raw JUnit, logcat, screenshots, and runner output remain below `quarantine/raw` and are never uploaded. Only `android-device-summary.json`, containing allowlisted hashes, counters, and booleans (not a raw serial or path), is standard evidence. Missing runner/device remains `blocked`; building an APK is never counted as execution.

## Release evidence gate

`.github/workflows/strict-release-evidence.yml` derives exactly one just-built Windows executable and E2E APK after cleaning their relevant output roots, invokes all three wrappers with the same release invocation on the dedicated self-hosted Windows/device lab, and then runs:

```powershell
.\eng\Test-StrictClientEvidence.ps1 `
  -ReleaseInvocationId $releaseInvocationId `
  -RequireComplete
```

The validator is the only release gate and sets `productionReady` explicitly. Normal developer CI only publishes `NOT-RUN`; direct `dotnet test`, a copied result, or skipped tests cannot satisfy this gate.

Rollback is forward-only: stop promotion, preserve rejected evidence locally for analysis, and ship a reviewed corrective commit. Do not restore the weaker validator, production package testing, raw artifact upload, or pass-by-return behavior. Mr. X owns the final release decision.
