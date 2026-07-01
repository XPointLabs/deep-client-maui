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
`network.xpoint.deep`; the build script validates it and copies it into the
ignored platform path for compilation.

## Build

```powershell
.\eng\build-android-play.ps1
```

The script fails when signing material is missing and prints the resulting
signed `.aab` and `.apk` paths only after both Release builds succeed. Final
artifacts are named `network.xpoint.deep.aab` and `network.xpoint.deep.apk`
under `artifacts/android-release`. Before compilation it validates the bundled
Xray AAR, including arm64/x86_64 ELF metadata and Google Play 16 KB alignment.

Before every upload, increment `ApplicationVersion` in
`src/Deep.Client.Maui/Deep.Client.Maui.csproj`. Update
`ApplicationDisplayVersion` when the user-facing version changes.

## Play Console

Enable Play App Signing, upload the generated bundle, complete the Data safety
and content declarations, provide the XPoint privacy-policy URL, and run the
bundle through an internal testing track before production rollout.
