# Google Play Release

The Android release profile produces an Android App Bundle and targets API 36.
Keep the upload keystore and its password outside Git.

## One-time setup

Create a Play upload key and register its certificate in Play Console. Set the
following variables for the build shell:

```powershell
$env:XPOINT_ANDROID_KEYSTORE = "C:\secure\xpoint-play-upload.keystore"
$env:XPOINT_ANDROID_SIGNING_PASSWORD_FILE = "C:\secure\xpoint-play-upload.pass"
$env:XPOINT_ANDROID_KEY_ALIAS = "xpoint-upload"
$env:XPOINT_GOOGLE_SERVICES_JSON = "C:\secure\google-services.json"
```

The password file must contain only the keystore password. The keystore and
key password must be identical because the build uses a PKCS#12 keystore.
The Firebase configuration must contain the Android package
`network.xpoint.deep`; the build script takes an exclusive bounded snapshot,
rejects reparse-point paths, and passes the external file to MSBuild through
`DeepGoogleServicesJson`. It never copies the secret into the repository.

## Build

```powershell
.\eng\build-android-play.ps1 `
  -TrustFloorBundle C:\secure\deep-production-trust.json `
  -AndroidCodeTransparencyManifest C:\secure\deep-android.act1 `
  -BundletoolJar C:\tools\bundletool-all.jar
```

The script fails when signing material is missing and prints the resulting
signed `.aab` and `.apk` paths only after both Release builds succeed and the
complete default bundletool APK set matches the pinned Mr. X-signed ACT1
code-transparency manifest. Both preparation and publish require bundletool 1.18.3 with SHA-256
`a099cfa1543f55593bc2ed16a70a7c67fe54b1747bb7301f37fdfd6d91028e29`. The Mr. X private signing key is never present on
the build host. Final
artifacts are named `network.xpoint.deep-<display-version>-v<version-code>.aab`
and `network.xpoint.deep-<display-version>-v<version-code>.apk` under
`artifacts/android-release`. Before compilation it validates the bundled Xray
AAR, including arm64/x86_64 ELF metadata and Google Play 16 KB alignment.

Before every upload, increment `ApplicationVersion` in
`src/Deep.Client.Maui/Deep.Client.Maui.csproj`. Update
`ApplicationDisplayVersion` when the user-facing version changes.

## Play Console

Enable Play App Signing, upload the generated bundle, complete the Data safety
and content declarations, provide the XPoint privacy-policy URL, and run the
bundle through an internal testing track before production rollout.

Before Beta promotion, the closed-track/Internal App Sharing lane must retrieve actual
Play-delivered base/splits and run the ACT1 `verify` command over at least nine representative
API/ABI/density/language configurations. This confirms the installed Play app-signing lineage and
blocks promotion if Play-delivered semantic content differs from ACT1.
