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

### Stable selector contract

Android and Windows E2E must select only fixed `AutomationId` values. IDs never
contain session IDs, message content, filenames, attachment keys, or other user
data; repeated message/list templates deliberately reuse a role selector.

| Surface | Selectors |
| --- | --- |
| Inbox | `Conversations.ProfileSettings`, `Conversations.ConversationRow`, `Conversations.Refresh`; desktop equivalents are `DesktopWorkspace.ProfileSettings`, `DesktopWorkspace.ConversationRow`, and `DesktopWorkspace.ConversationList` |
| Direct chat | `Chat.MessageBubble`, `Chat.MessageBody`, `Chat.DeliveryStatus`, `Chat.AttachmentCard`, `Chat.AttachmentFilename`, `Chat.StagedAttachmentRow`, `Chat.StagedAttachmentFilename`, `Chat.RemoveStagedAttachment`, `Chat.AttachmentOpen`, `Chat.AttachmentSave`, `Chat.MessageAttachmentOpen`, `Chat.MessageAttachmentSave` |
| Group chat | `GroupChat.MessageBubble`, `GroupChat.MessageBody`, `GroupChat.DeliveryStatus`, `GroupChat.AttachmentCard`, `GroupChat.AttachmentFilename`, `GroupChat.StagedAttachmentRow`, `GroupChat.StagedAttachmentFilename`, `GroupChat.RemoveStagedAttachment`, `GroupChat.AttachmentOpen`, `GroupChat.AttachmentSave`, `GroupChat.MessageAttachmentOpen`, `GroupChat.MessageAttachmentSave` |
| Desktop detail | `DesktopWorkspace.DirectMessageBubble`, `DesktopWorkspace.DirectMessageBody`, `DesktopWorkspace.DirectDeliveryStatus`, `DesktopWorkspace.GroupMessageBubble`, `DesktopWorkspace.GroupMessageBody`, `DesktopWorkspace.GroupDeliveryStatus`, `DesktopWorkspace.AttachmentOpen`, `DesktopWorkspace.AttachmentSave` |

`MessageBubble` exposes the fixed semantic role `Входящее сообщение` or
`Исходящее сообщение`; delivery status retains its accessible status description.
Attachment open/save selectors are emitted only by app-owned action sheets.
Every plain-text and rich-text template exposes bubble/body/status selectors.
Image and voice templates additionally expose `AttachmentCard`; document
attachment templates also expose `AttachmentFilename`.

Build the Debug Windows app first, then invoke:

```powershell
.\eng\Invoke-StrictClientLane.ps1 `
  -Lane WindowsUi `
  -ReleaseInvocationId $releaseInvocationId `
  -AppPath .\src\Deep.Client.Maui\bin\Debug\net10.0-windows10.0.19041.0\win-arm64\Deep.Client.Maui.exe `
  -Bootstrap stub
```

The lane takes a canonical, repository-contained, no-reparse snapshot of the complete just-built payload before launch. On Windows it opens every payload file with `FileShare.Read`, hashes those leased handles, and keeps every lease through process launch, UI automation, and post-run verification. Concurrent write, replace, delete, new-file insertion, duplicate/case-colliding paths, alternate separators, substitutions, and payload changes fail the lane. A host that cannot provide these Windows leases is blocked; it cannot downgrade to hash-only verification. The lane waits for a nonzero top-level window, fills the display name, invokes Create, and proves that Welcome is replaced by the enabled `Conversations.NewConversation` control on the authenticated Conversations/Desktop Workspace surface. The lane uses a real interactive control because WinUI does not consistently publish layout-only `ContentPage` and `Grid` automation IDs in its UIA tree. Each run uses a mandatory unique Debug-only application-data directory for both `stub` and `live` bootstrap.

The preflight also requires the current Windows session to be unlocked. UI Automation can still enumerate controls while Windows Hello or the lock screen owns input, but that is not rendered-interaction evidence; the lane therefore blocks before launch instead of misreporting a rejected input event as an application navigation defect.

Raw screenshots are disabled by default. `-CaptureStubWelcomeFailure` permits a raw bitmap only for an explicit stub Welcome failure; it is written below `windows-ui/quarantine/raw` and is never standard or uploadable evidence. The standard tree contains only allowlisted automation IDs and control types.

`stub` is an explicit UI-render bootstrap only. It is compiled behind `DEBUG`; Release configuration continues to reject stub transport and non-loopback cleartext URLs. This spike does not create accounts, self-chat, or claim message E2E.

Use `-Bootstrap live` only when all live endpoint variables are present.

## Live client acceptance

Set `XNODE_URLS` to between three and sixteen distinct
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
indices exactly `0,1,2`, three distinct identities from the pinned set, and three unique signed
relay RPC endpoints. Both routed store and authenticated retrieve must cross the
router API. A forced router-API outage must fail closed without any request to a
direct storage endpoint. The wrapper always executes this xUnit acceptance in
`Release`; the rendered Windows and physical Android UI lanes remain explicitly
Debug-only and do not substitute for that Release transport contract. The
self-hosted workflow explicitly restores and builds the ViewModel test project
in Release before invoking the wrapper's `--no-restore` live test command, so a
clean runner cannot fail merely because Release test assets are absent.

For a development-only physical survival compose that exposes canonical local
IPv4 HTTP endpoints (for example `192.168.1.44`), set
`DEEP_STRICT_LIVE_PHYSICAL_E2E=1` in addition to `DEEP_STRICT_LIVE=1`. This
exact-value opt-in passes `RoutedRuntimeEndpointPolicy.PhysicalE2eDevelopment`
through the live harness's pinned-router parser, file/push/call service URL
checks, and routed composition factory. When absent (or set to any value other
than exactly `1`), the harness preserves the Production policy and rejects LAN
HTTP. This changes no application production or Release default.

## Android device lane

Build the repository-owned runner as one self-contained executable for the
approved Windows lab architecture:

```powershell
.\eng\Build-AndroidRunner.ps1 -RuntimeIdentifier win-arm64
```

The publish directory contains exactly `deep-android-runner.exe`. Version 3
uses the policy-pinned sibling `adb.exe` directly (never a shell), enforces
bounded command/output/UI waits, revalidates the selected physical device and
installed APK, and rejects any non-E2E third-party package on the dedicated
device. Its mandatory rendered flow clears only `network.xpoint.deep.e2e`,
creates a fresh account through exact UIAutomator resource IDs, proves the
authenticated Conversations surface, cold-restarts the app, proves that state
again, and finally clears and removes the E2E package. It never queries, clears,
installs, or removes the production package except for the wrapper and runner's
read-only exact-name absence checks.

Copying this executable into the protected source is an explicit lab inventory
operation performed only after the final APK, Windows payload, commit, device,
and toolchain are fixed. The repository build script never writes the protected
source or the provisioned `.secrets/android-lab` destination.

Build the Debug-only E2E APK. Mr. X must provision and approve a commit-bound lab policy plus all referenced files below the ignored `.secrets/android-lab/` directory. Start from `eng/policies/android-lab-policy.template.json`, but do not edit that checked-in blocking template into a credential: copy it to the protected directory and fill exact values there. The policy binds:

- the current 40-character Git commit and a nonzero policy ID;
- an approval receipt signed off by `Mr. X`, including its exact SHA-256;
- repository-relative paths, exact SHA-256 values, and privacy-safe exact versions for `deep-android-runner.exe`, `adb.exe`, the selected `aapt.exe` or `aapt2.exe`, and `apksigner.bat`;
- the approved inventory serial, build fingerprint, product, hardware, model, SDK, dedicated flag, Mr. X inventory approval, `ro.kernel.qemu=0`, and `physical-managed-dedicated` class;
- the E2E package, version, APK SHA-256, and signing-certificate SHA-256.

For the verified development membership route lane, the embedded survival
environment must include both of the following or neither:

```text
DEEP_DEV_LOCAL_MEMBERSHIP_TRUST_URL=http://<literal-local-ipv4>:<port>/api/network/membership-route-catalog
DEEP_DEV_LOCAL_MEMBERSHIP_TRUST_SHA256=<64-lowercase-hex>
```

This opt-in is accepted only by a non-Release `DeepPhysicalE2E=true` build with
`SURVIVAL_ENV=Development`. One missing value, a remote/hostname/HTTPS URL, a
different path, or a pin mismatch fails closed. The pin binds the exact
downloaded artifact; there is no remote trust root or TOFU fallback. Omitting
both values preserves the existing pinned-router path.

That same explicit build/profile combination may use canonical literal local
IPv4 HTTP addresses for `XNODE_URLS` and configured service endpoints:
loopback, RFC1918, or `169.254.0.0/16`. The policy is passed explicitly through
both parser and runtime composition; it is not inferred from arbitrary
environment reads inside URL validation. Hostnames, public/unspecified/
multicast addresses, alternate IPv4 spellings, credentials, query, and
fragment forms fail closed. The identical LAN values remain invalid in ordinary
Debug and Release composition.

CI does not trust a pre-existing checkout directory. `eng/Provision-AndroidLabPolicy.ps1` materializes an exact allowlisted bundle from `DEEP_ANDROID_LAB_PROTECTED_SOURCE` after checkout, rejects reparse points in every existing source/destination ancestor, requires a pinned owner, and permits write access only to that owner, Local System, and Builtin Administrators by resolved SID. Every source file is regular/read-only, and both source and destination are re-enumerated against the exact signed file set with no extras. The script verifies all receipt/tool hashes and an Ed25519 signature over the complete semantic policy projection. The Mr. X public-key SHA-256 enters the protected build invocation as `DEEP_MR_X_PUBLIC_KEY_SHA256`, is passed explicitly as `DeepMrXPublicKeySha256`, and is compiled into the physical-lab binary; application runtime configuration never supplies or replaces this trust root. Release and non-physical builds reject the property. The repository contains no invented real key. The verify-only helper uses a locked dependency graph, is built before provisioning, and runs without restore/build at the trust gate. An `if: always()` step removes the destination even after a failed lane.

Then attach that exact dedicated managed physical test device and invoke:

```powershell
dotnet build .\src\Deep.Client.Maui\Deep.Client.Maui.csproj `
  -f net10.0-android -c Debug `
  -p:DeepPhysicalE2E=true `
  -p:DeepMrXPublicKeySha256=$env:DEEP_MR_X_PUBLIC_KEY_SHA256

.\eng\Invoke-StrictClientLane.ps1 `
  -Lane AndroidDevice `
  -ReleaseInvocationId $releaseInvocationId `
  -ApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk `
  -AndroidLabPolicyPath .\.secrets\android-lab\approved-policy.json `
  -ConfigureAdbReverse
```

Release always keeps `network.xpoint.deep`; the physical Debug lane accepts only `network.xpoint.deep.e2e`. The wrapper, not the caller or runner, opens and hashes the policy-selected tools and APK, checks their exact versions, queries the attached device identity with the trusted `adb`, requires `ro.kernel.qemu=0`, rejects emulator hardware/model/product/characteristic patterns, validates the APK archive/metadata/certificate, and rejects any production package presence. Caller-selected tool paths or serials are optional cross-checks only and cannot establish trust. It never reads, cleans, or modifies the production package.

### Two-phase DEV-local mailbox bootstrap

Authenticated MAU2 credentials are holder-bound, so the app must create or
restore its Session identity before the host can issue the Android/Windows
pair. The physical Debug runtime therefore publishes only `sessionId` and the
Ed25519 public holder key to its app-private
`files/mailbox-holder-bootstrap-v1/android.holder.v1.json` record before it
attempts to load signed mailbox material. It never exports the recovery phrase
or a private key. Missing signed material still fails the first MAU2 operation
closed; it does not select routed storage or a raw transport fallback.

After the account exists, retrieve and validate that public request:

```powershell
.\eng\Invoke-AndroidMailboxBootstrap.ps1 `
  -Action ExportHolder `
  -AndroidSerial 192.168.1.45:43337
```

Provision the pair with the Android and Windows public holder keys, assemble
the platform-specific signed `mailbox-runtime-v1` root outside the repository,
and, before protected runtime paths enter the process, build the locked
signature verifier once:

```powershell
dotnet build .\eng\Deep.AndroidLab.PolicyVerifier\Deep.AndroidLab.PolicyVerifier.csproj `
  -c Release --locked-mode
```

Then stage the Android root without packaging it in the APK. `StageRuntime`
uses that prebuilt verifier with `--no-build --no-restore`, so dependency
resolution cannot occur while approval material is in scope:

```powershell
.\eng\Invoke-AndroidMailboxBootstrap.ps1 `
  -Action StageRuntime `
  -AndroidSerial 192.168.1.45:43337 `
  -RuntimeRoot C:\protected\deep-mailbox\android\mailbox-runtime-v1 `
  -MrXPublicKeySha256 $env:DEEP_MR_X_PUBLIC_KEY_SHA256
```

The staging command force-stops only `network.xpoint.deep.e2e`, streams the
exact allowlisted files through `run-as`, checks every remote SHA-256, applies
`0700/0600`, and atomically renames the complete staging directory. It never
touches `network.xpoint.deep`. Relaunch only after staging completes.

### Windows DEV-local mailbox staging

The Windows peer uses the same signed pair but is not permitted to receive it
through an environment variable, package asset, or a shared working folder.
After the Windows Debug client has created its identity, export only its public
holder request and protect the output file with the exact current-user, Local
System, and Builtin Administrators DACL:

```powershell
.\eng\Invoke-WindowsMailboxBootstrap.ps1 `
  -Action ExportHolder `
  -WindowsAppDataRoot C:\protected\deep-e2e\windows-appdata `
  -OutputPath C:\protected\deep-mailbox\holders\windows.holder.v1.json
```

After pair issuance, stage the Windows-specific runtime into that same isolated
app-data root:

```powershell
.\eng\Invoke-WindowsMailboxBootstrap.ps1 `
  -Action StageRuntime `
  -WindowsAppDataRoot C:\protected\deep-e2e\windows-appdata `
  -RuntimeRoot C:\protected\deep-mailbox\windows\mailbox-runtime-v1 `
  -MrXPublicKeySha256 $env:DEEP_MR_X_PUBLIC_KEY_SHA256
```

The command uses the prebuilt locked verifier (`--no-build --no-restore`),
checks the pin, signature, exact ten-file/three-directory inventory, pair
generation, activation hashes, and minimized authority before copying. It
creates a same-volume sibling staging directory with a GUID, applies and
revalidates the exact non-inherited three-principal DACL to every entry, then
performs one `Directory.Move`. Existing `mailbox-runtime-v1` is a hard failure:
the command never replaces or deletes a live runtime. The app repeats the ACL
and reparse validation before loading the runtime. This is DEV-local only;
production approval and provisioning remain a separate release composition.

The preflight supports an explicit `-AndroidSerial` and configures `adb reverse` for local UAT ports when requested. The runner must bind its v3 result to the policy ID/hash, source commit, both invocation IDs, APK SHA-256, package/version, signing certificate, selected serial, fingerprint/product hashes, SDK/class, its own binary hash/exact version, JUnit hash, and cleanup attestations before and after execution. JUnit counters are independently parsed and cross-checked. Any DTD/entity, system output/error, attachment, absolute Windows/Unix path, absolute URI, sensitive property/value, or non-whitespace text blocks sanitization. Raw JUnit, logcat, screenshots, runner result, and runner output remain below `quarantine/raw` and are never uploaded. Only `android-device-summary.json`, containing allowlisted hashes, counters, safe versions, and booleans (not a raw serial or path), is standard evidence.

`-AllowSyntheticLabPolicyForContractTests` exists only for the repository's compiled synthetic security fixture. It must be explicit, emits `synthetic=true`, and is always rejected by the release validator. The checked-in template, missing Mr. X receipt, missing exact tool/APK/device bindings, personal devices, or runner self-attestation remain `blocked`; building an APK is never counted as execution.

## Physical Android ↔ Windows rendered flow (opt-in)

The non-destructive physical runner `eng/Invoke-PhysicalMau2CrossPlatform.ps1`
requires one explicit phase: `Attach`, `HappyPath`, `RestartDurability`, or
`NegativeRuntime`. `NegativeRuntime` does not clear or reinstall either Android
package and does not mutate Android app data. It materializes three disposable,
exact-DACL Windows app-data roots below the protected
`secrets/mailbox-bootstrap/e2e-runs/<run-id>` tree: a tampered Mr. X signature,
a missing authority file, and a valid Android runtime presented to Windows.
Each must expose a non-empty `Startup.Error` and must not reach Welcome or an
authenticated surface. The runner removes the disposable runtime bytes and
requires the canonical live Windows runtime hash and production Android package
snapshot to remain unchanged. Expiry/revocation and a second valid wrong-holder
bundle require issuer-backed signed fixtures and are not simulated by editing
JSON; Android platform/holder binding remains covered by shared loader tests
until a second disposable Android package is available.

`DEEP_STRICT_CROSS_PLATFORM_UI=1` enables the separate Debug/live rendered
acceptance in `Deep.Client.Maui.UiTests`. It uses FlaUI UIA3 only against the
PID returned by the Windows process it starts, and `adb -s <exact-serial>` plus
`uiautomator dump` for Android. It does not use Appium, WinAppDriver, an
emulator, a mock transport, a text selector, or a coordinate script. A tap
centre is derived only from the bounds of one exact resource-id in a fresh
dumped tree; every Windows action begins with one exact AutomationId.

The lane is `NOT-RUN` at xUnit discovery unless an unlocked Windows desktop and approved physical
device have all of these inputs: `DEEP_E2E_BOOTSTRAP=live`,
`DEEP_MAUI_EXE`, `DEEP_E2E_APPDATA_ROOT`, `DEEP_E2E_ARTIFACTS`,
`DEEP_E2E_ANDROID_SERIAL`, absolute `DEEP_E2E_ADB`, `DEEP_E2E_ANDROID_APK`,
absolute `DEEP_E2E_AAPT`, absolute `DEEP_E2E_APKSIGNER`, and
`DEEP_E2E_ATTACHMENT_FIXTURE`, one common `DEEP_RELEASE_INVOCATION_ID`, and an
exact provisioned `DEEP_E2E_ANDROID_POLICY` plus the external
`DEEP_MR_X_PUBLIC_KEY_SHA256` pin. This is the same protected policy, approval receipt,
signed payload, and Ed25519 verifier used by the release Android lane; a parallel
caller-hash policy is not accepted. Its signed inventory binds exact adb/aapt/apksigner
paths, hashes, version arguments and complete version output; source commit; Windows
executable path/hash; APK path/byte size; and physical dedicated device serial,
fingerprint, model, product, hardware, SDK, and build characteristics. The supplied APK
must be the installed `network.xpoint.deep.e2e` package at exact `aapt` package,
versionCode/versionName, SHA-256, and signing-certificate digest. It also requires
`DEEP_E2E_ANDROID_SELECTORS_JSON`, a role-to-exact-resource-id map for every
app control used by the test; and exact system-picker resource IDs in
`DEEP_E2E_ANDROID_PICKER_DOWNLOADS_ID` and
`DEEP_E2E_ANDROID_PICKER_FILE_ID`.
`DEEP_E2E_ANDROID_PICKER_CONFIRM_ID` is optional. Set it only for a
DocumentsUI implementation that requires a separate confirmation action after
selecting the exact filename; single-selection pickers that immediately return
to Deep must leave it unset.

It creates separate identities, records only identity hashes, rejects a syntactically
invalid Session ID (wrong prefix) before a contact or conversation can open, makes
reciprocal contacts, and verifies
unique text in both directions. A unique fixture is pushed through the Android
system picker, then opened/saved directly through the production
`Windows.Storage.DownloadsFolder\Deep` path and SHA-256 checked. Both clients cold restart;
Windows saves it again with the production collision name and both new files are checked.
Windows
must have a distinct PID while retaining the same per-run isolated app-data
root, and marked messages must render again.

System picker resource IDs are explicitly configured because they vary by OEM/version.
The lane taps only one exact configured resource ID and asserts the exact fixture filename
on that node; it never uses an unscoped text selector. Production Windows Save has no dialog:
the harness snapshots Downloads before each Save and owns only the exact new correlated file,
never a preexisting collision. Cleanup independently attempts both run-created Downloads
files, the attempted Android fixture path, attempted E2E package data, and isolated
Windows app-data. Cleanup eligibility is registered before ColdStart/identity/push
attempts, aggregates every independent failure, and writes `passed` only after all
cleanup succeeds.

A valid unknown 66-character Session ID with prefix `05`, `15`, or `25` is intentionally
accepted by the product and must never be used as the negative validation gate. The offline
contract suite mutation-tests that distinction.

Only `cross-platform-ui-result.json` is standard evidence: status, safe
package/version values, hashes, and booleans. It never contains serials,
identities, messages, paths, raw XML, screenshots, picker content, or ADB
output. Missing prerequisites/locked desktop are `NOT-RUN`; invalid configured
selectors, emulator/metadata mismatch, missing UI state, or SHA mismatch fail.
There is no selector, coordinate, or deterministic-pass fallback.

## Release evidence gate

`.github/workflows/strict-release-evidence.yml` derives exactly one just-built Windows executable and E2E APK after cleaning their relevant output roots, invokes all three wrappers with the same release invocation on the dedicated self-hosted Windows/device lab, and then runs:

```powershell
.\eng\Test-StrictClientEvidence.ps1 `
  -ReleaseInvocationId $releaseInvocationId `
  -AndroidLabPolicyPath .\.secrets\android-lab\approved-policy.json `
  -AndroidApkPath .\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep.e2e-Signed.apk `
  -RequireComplete
```

The validator is the only release gate and sets `productionReady` explicitly. It revalidates the protected real policy, receipt, tools, exact APK, safe summary, and all three fresh lane identities against a clean current commit. Normal developer CI and synthetic contracts only publish `NOT-RUN`/blocked evidence; direct `dotnet test`, a copied result, a synthetic policy, or skipped tests cannot satisfy this gate. `productionReady` remains false until the real Windows rendered lane, approved physical Android lane, and routed live lane (whose selected route has three nodes) all pass in one invocation.

Rollback is forward-only: stop promotion, preserve rejected raw evidence only in the local quarantine, and ship a reviewed corrective commit. Do not restore hash-only Windows checks, caller self-attestation, synthetic release evidence, weaker JUnit filtering, production package testing, raw artifact upload, or pass-by-return behavior. Mr. X owns policy approval and the final release decision.
