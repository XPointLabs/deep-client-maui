# P12A C14 acceptance evidence

Generated at `2026-07-19T22:11:01Z`.

## Decision

`GO` for the dormant P12A policy-shell corrective slice at the exact source
below. Severity counts from both independent reviews are
`P0/P1/P2/P3 = 0/0/0/0`.

This is source, compile, and policy-shell evidence only. It is explicitly
`RADIO-EXCHANGE-DISABLED` and `PHYSICAL-E2E-BLOCKED`. It is not evidence of
nearby delivery, a production Android package, a signed Windows MSIX, rendered
UI acceptance, battery performance, or physical-device interoperability.

## Exact source

| Component | Commit | Tree | State before builds |
|---|---|---|---|
| `deep-client-maui` | `2a7bd95e3d7ffbd89e950e1f216b334a20ed8066` | `7e692da57654c1de24004e1e19ffd1cb07623b84` | clean |
| sibling `deep-client-shared` build dependency | `343f770800ad6247a5a0ae63a418b229c9388cde` | `4b0ca437a7aa9f74dbe9e34ce79081447bf92675` | clean |

The evidence carrier is a later evidence-only commit. Artifact provenance stays
bound to the `deep-client-maui` source commit and tree above.

## C14 RED/GREEN chain

| Stage | Commit | Result |
|---|---|---|
| accepted C13 base | `3513fc1f4a2a720a64b923fc16ea8f243a84b7f0` | baseline before C14 |
| C14 RED | `70382e2df632cf9775266dfadde2b655abcf193e` | added regression coverage for retry-only failed disposal and atomic event admission/publication; failed against the pre-fix behavior as expected |
| C14 GREEN | `2a7bd95e3d7ffbd89e950e1f216b334a20ed8066` | implements `DisposeFailed`, keeps normal operations closed after successful unsubscribe, permits disposal retry, and linearizes event admission with queue publication |

## Independent reviews

### Review A — dependency and concurrency audit

- Exact source: `2a7bd95e3d7ffbd89e950e1f216b334a20ed8066`.
- Verdict: `GO`; `P0/P1/P2/P3 = 0/0/0/0`.
- Focused C14: `2 passed / 0 failed / 0 skipped`.
- Full ViewModels Release: `343 passed / 0 failed / 2 configured-only skipped`.
- C13/C14 stress: `40` repeated passes.
- Core Release build: `0 warnings / 0 errors`.
- Smoke: `119 passed / 0 failed / 0 skipped`.
- Targeted Core format/static and `git diff --check`: passed.
- Conclusion: `DisposeFailed` is retry-only; successful unsubscribe never
  returns the coordinator to `Running`; admission and queue publication are
  atomic under `sync -> eventSync`; scheduling remains outside locks; an
  admitted event is visible to drain; no reverse nested lock edge was found.

### Review B — architecture and lead-development audit

- Exact source: `2a7bd95e3d7ffbd89e950e1f216b334a20ed8066`.
- Verdict: `GO`; `P0/P1/P2/P3 = 0/0/0/0`.
- Focused C13+C14 Release: `10 passed / 0 failed / 0 skipped`.
- Focused C13+C14 stress: `50` repeated passes.
- All Nearby policy Release tests: `147 passed / 0 failed / 0 skipped`.
- Full ViewModels Release: `343 passed / 0 failed / 2 configured-only skipped`.
- Core Release build: `0 warnings / 0 errors`.
- Windows ARM64 Release compile: `0 warnings / 0 errors`.
- A local smoke rerun encountered only the repository's intentional external
  Android-lab-material guard; protected material was not read or changed. This
  does not replace Review A's clean `119/119` smoke result and is not treated as
  product acceptance.
- Conclusion: retry after durability failure is bounded to disposal cleanup;
  `Start`, `Stop`, `Refresh`, event admission, and drain remain closed; the
  subscription closes before final drain; post-close event floods do not append
  work; lock ordering is consistently `sync -> eventSync -> intentSync`.

## Carrier build and static gates

All commands ran in the clean exact-source worktree and used the repository's
documented fail-closed target/configuration guards.

| Gate | Result |
|---|---|
| Windows `net10.0-windows10.0.19041.0`, Release, `RuntimeIdentifierOverride=win-arm64`, clean then build, no restore during build | passed, `0 warnings / 0 errors` |
| Android `net10.0-android`, Debug, clean then build, no restore during build | passed, `0 warnings / 0 errors` |
| Android Debug APK signature verification | passed; one signer, APK Signature Schemes v2 and v3 verified |
| Focused C13+C14 Release rerun | `10 passed / 0 failed / 0 skipped` |
| Full ViewModels Release rerun | `343 passed / 0 failed / 2 configured-only skipped` |
| Production MAUI registrations for the dormant Nearby coordinator/adapters | `0` matches |
| Android/Windows manifest BLE, nearby Wi-Fi, location, and foreground-service permission scan | `0` matches |
| Pre-carrier worktree status | clean |
| `git diff --check` | passed |

No Firebase configuration, UAT material, production keystore, signing password,
seed phrase, token, or endpoint credential was supplied to these builds.

## Exact-source artifacts

These files were created by the clean/build flows above. Paths are absolute for
this local evidence carrier; hashes are SHA-256.

| Kind | Absolute path | Bytes | SHA-256 |
|---|---|---:|---|
| Windows ARM64 Release managed entry assembly | `C:\W\deep-survival\wave04\deep-client-maui\src\Deep.Client.Maui\bin\Release\net10.0-windows10.0.19041.0\win-arm64\Deep.Client.Maui.dll` | `2732032` | `ad9694bb983eaeed0086fc980ebc417fa3858c7ba5d2014b95fd010f7292103a` |
| Windows ARM64 Release launcher | `C:\W\deep-survival\wave04\deep-client-maui\src\Deep.Client.Maui\bin\Release\net10.0-windows10.0.19041.0\win-arm64\Deep.Client.Maui.exe` | `281088` | `38093f68cf91f5a2259592c70b1cccb658adb03f7fea810128ca977faf82a8ff` |
| Android Debug signed APK | `C:\W\deep-survival\wave04\deep-client-maui\src\Deep.Client.Maui\bin\Debug\net10.0-android\network.xpoint.deep-Signed.apk` | `156712074` | `5e23e8fba9b39ce5cc4903436ea96bfd870dbfabbf4504d5364b2e0b92008a9c` |

The Android APK is a build-only Debug artifact. It is not production release or
physical-device acceptance evidence.

## Fail-closed release and activation status

- Android Release was invoked only to reproduce the protected repository guard
  with explicitly empty protected inputs. It stopped before publish because
  the mandatory Play upload keystore was absent. No Release APK/AAB is claimed.
- Windows output above is an unpackaged compile result. Production MSIX signing,
  publisher/WNS binding, installation, and rendered Windows acceptance were not
  attempted without their protected release inputs.
- `RADIO-EXCHANGE-DISABLED`: P12A remains a dormant policy shell. Production DI
  has no Nearby registration, platform manifests request no radio/location
  permissions, and this slice exchanges no payload.
- `PHYSICAL-E2E-BLOCKED`: no approved commit-bound Android lab policy and
  authorized physical-device run is included, and no rendered Windows UI lane
  is included.
- Battery, thermal, BLE/Wi-Fi Direct behavior, discovery, pairing, routing,
  store-and-forward delivery, and interoperability remain future
  activation-gated evidence.

No repository push was performed.
