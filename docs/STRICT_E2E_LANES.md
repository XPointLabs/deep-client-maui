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

The lane takes a canonical, repository-contained, no-reparse snapshot of the complete just-built payload before launch. On Windows it opens every payload file with `FileShare.Read`, hashes those leased handles, and keeps every lease through process launch, UI automation, and post-run verification. Concurrent write, replace, delete, new-file insertion, duplicate/case-colliding paths, alternate separators, substitutions, and payload changes fail the lane. A host that cannot provide these Windows leases is blocked; it cannot downgrade to hash-only verification. The lane waits for a nonzero top-level window, fills the display name, invokes Create, and proves that an authenticated Conversations/Desktop Workspace root replaces Welcome. Each run uses a mandatory unique Debug-only application-data directory for both `stub` and `live` bootstrap.

Raw screenshots are disabled by default. `-CaptureStubWelcomeFailure` permits a raw bitmap only for an explicit stub Welcome failure; it is written below `windows-ui/quarantine/raw` and is never standard or uploadable evidence. The standard tree contains only allowlisted automation IDs and control types.

`stub` is an explicit UI-render bootstrap only. It is compiled behind `DEBUG`; Release configuration continues to reject stub transport and non-loopback cleartext URLs. This spike does not create accounts, self-chat, or claim message E2E.

Use `-Bootstrap live` only when all live endpoint variables are present.

## Live client acceptance

Set `XNODE_URLS` to exactly three distinct
`<64-lowerhex-routerId>|<https-or-loopback-http-url>` entries, plus valid HTTPS
or explicit-loopback HTTP `DEEP_FILE_URL`, `DEEP_PUSH_URL`, and
`DEEP_CALL_SIGNALING_BASE_URL`. Router bases must use the root path and omit
userinfo, query, and fragment; loopback HTTP must use a literal IP rather than a
hostname. `DEEP_STORAGE_URL` must be absent. Then run:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane LiveInfrastructure `
  -ReleaseInvocationId $releaseInvocationId
```

Without `DEEP_STRICT_LIVE=1`, the live xUnit acceptance test is explicitly reported as `NOT-RUN` instead of silently passing.
`DEEP_STORAGE_URL` cannot satisfy this release lane. Direct storage has a separate opt-in diagnostic contract (`DEEP_STRICT_DIRECT_STORAGE=1`) and is never routed evidence.
The acceptance verifies the actual `CurrentRoute`: mode `onion-storage`, node
indices exactly `0,1,2`, the exact pinned identity set, and three unique signed
relay RPC endpoints. Both routed store and authenticated retrieve must cross the
router API. A forced router-API outage must fail closed without any request to a
direct storage endpoint.

## Android device lane

Build the Debug-only E2E APK. Mr. X must provision and approve a commit-bound lab policy plus all referenced files below the ignored `.secrets/android-lab/` directory. Start from `eng/policies/android-lab-policy.template.json`, but do not edit that checked-in blocking template into a credential: copy it to the protected directory and fill exact values there. The policy binds:

- the current 40-character Git commit and a nonzero policy ID;
- an approval receipt signed off by `Mr. X`, including its exact SHA-256;
- repository-relative paths, exact SHA-256 values, and privacy-safe exact versions for `deep-android-runner.exe`, `adb.exe`, the selected `aapt.exe` or `aapt2.exe`, and `apksigner.bat`;
- the approved inventory serial, build fingerprint, product, hardware, model, SDK, dedicated flag, Mr. X inventory approval, `ro.kernel.qemu=0`, and `physical-managed-dedicated` class;
- the E2E package, version, APK SHA-256, and signing-certificate SHA-256.

CI does not trust a pre-existing checkout directory. `eng/Provision-AndroidLabPolicy.ps1` materializes an exact allowlisted bundle from `DEEP_ANDROID_LAB_PROTECTED_SOURCE` after checkout, rejects reparse points in every existing source/destination ancestor, requires a pinned owner, and permits write access only to that owner, Local System, and Builtin Administrators by resolved SID. Every source file is regular/read-only, and both source and destination are re-enumerated against the exact signed file set with no extras. The script verifies all receipt/tool hashes and an Ed25519 signature over the complete semantic policy projection. The Mr. X public-key SHA-256 is supplied as the protected deployment pin `DEEP_MR_X_PUBLIC_KEY_SHA256`; the repository contains no invented real key. The verify-only helper uses a locked dependency graph, is built before provisioning, and runs without restore/build at the trust gate. An `if: always()` step removes the destination even after a failed lane.

Then attach that exact dedicated managed physical test device and invoke:

```powershell
dotnet build .\src\Deep.Client.Maui\Deep.Client.Maui.csproj `
  -f net10.0-android -c Debug -p:DeepPhysicalE2E=true

.\eng\Invoke-StrictClientLane.ps1 `
  -Lane AndroidDevice `
  -ReleaseInvocationId $releaseInvocationId `
  -ApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk `
  -AndroidLabPolicyPath .\.secrets\android-lab\approved-policy.json `
  -ConfigureAdbReverse
```

Release always keeps `network.xpoint.deep`; the physical Debug lane accepts only `network.xpoint.deep.e2e`. The wrapper, not the caller or runner, opens and hashes the policy-selected tools and APK, checks their exact versions, queries the attached device identity with the trusted `adb`, requires `ro.kernel.qemu=0`, rejects emulator hardware/model/product/characteristic patterns, validates the APK archive/metadata/certificate, and rejects any production package presence. Caller-selected tool paths or serials are optional cross-checks only and cannot establish trust. It never reads, cleans, or modifies the production package.

The preflight supports an explicit `-AndroidSerial` and configures `adb reverse` for local UAT ports when requested. The runner must bind its v3 result to the policy ID/hash, source commit, both invocation IDs, APK SHA-256, package/version, signing certificate, selected serial, fingerprint/product hashes, SDK/class, its own binary hash/exact version, JUnit hash, and cleanup attestations before and after execution. JUnit counters are independently parsed and cross-checked. Any DTD/entity, system output/error, attachment, absolute Windows/Unix path, absolute URI, sensitive property/value, or non-whitespace text blocks sanitization. Raw JUnit, logcat, screenshots, runner result, and runner output remain below `quarantine/raw` and are never uploaded. Only `android-device-summary.json`, containing allowlisted hashes, counters, safe versions, and booleans (not a raw serial or path), is standard evidence.

`-AllowSyntheticLabPolicyForContractTests` exists only for the repository's compiled synthetic security fixture. It must be explicit, emits `synthetic=true`, and is always rejected by the release validator. The checked-in template, missing Mr. X receipt, missing exact tool/APK/device bindings, personal devices, or runner self-attestation remain `blocked`; building an APK is never counted as execution.

## Release evidence gate

`.github/workflows/strict-release-evidence.yml` derives exactly one just-built Windows executable and E2E APK after cleaning their relevant output roots, invokes all three wrappers with the same release invocation on the dedicated self-hosted Windows/device lab, and then runs:

```powershell
.\eng\Test-StrictClientEvidence.ps1 `
  -ReleaseInvocationId $releaseInvocationId `
  -AndroidLabPolicyPath .\.secrets\android-lab\approved-policy.json `
  -AndroidApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk `
  -RequireComplete
```

The validator is the only release gate and sets `productionReady` explicitly. It revalidates the protected real policy, receipt, tools, exact APK, safe summary, and all three fresh lane identities against a clean current commit. Normal developer CI and synthetic contracts only publish `NOT-RUN`/blocked evidence; direct `dotnet test`, a copied result, a synthetic policy, or skipped tests cannot satisfy this gate. `productionReady` remains false until the real Windows rendered lane, approved physical Android lane, and routed live three-node lane all pass in one invocation.

Rollback is forward-only: stop promotion, preserve rejected raw evidence only in the local quarantine, and ship a reviewed corrective commit. Do not restore hash-only Windows checks, caller self-attestation, synthetic release evidence, weaker JUnit filtering, production package testing, raw artifact upload, or pass-by-return behavior. Mr. X owns policy approval and the final release decision.
