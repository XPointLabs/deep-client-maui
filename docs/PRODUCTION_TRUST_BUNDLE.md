# Production trust-floor bundle

Android and Windows Release builds require an external Mr. X-approved JSON bundle. The file is
never copied into the application. Its validated values are passed as MSBuild properties and
compiled into assembly metadata; runtime environment variables and downloaded files are not
trust roots.

```json
{
  "schemaVersion": 1,
  "trustFloor": {
    "mrXPublicKeySha256": "<64 lowercase hex>",
    "networkId": "<32 lowercase hex>",
    "authorityGeneration": "1",
    "authorityHash": "<64 lowercase hex>",
    "revocationGeneration": "1",
    "revocationHeadHash": "<64 lowercase hex>",
    "revocationSnapshotHash": "<64 lowercase hex>",
    "topologyGeneration": "1",
    "topologyHash": "<64 lowercase hex>"
  },
  "android": {
    "buildIdSha256": "<64 lowercase hex>",
    "applicationId": "network.xpoint.deep",
    "versionCode": "15",
    "playAppSigningLineageSha256": ["<64 lowercase hex>"]
  }
}
```

The schema is closed: unknown, missing, duplicate, escaped-property, and oversized input is
rejected. `android` is a required root property; Windows-only bundles set it to `null`, while an
Android build requires the exact Android object shown above.

The build script accepts only a local regular file whose path and ancestors contain no Windows
reparse points. It takes an exclusive, bounded single-handle snapshot and repeats the ancestor
check after reading. The operator must keep this Mr. X-approved input in a protected local secrets
directory; remote shares and concurrently managed paths are outside this build-time trust model.

`buildIdSha256` is the SHA-256 of the exact canonical ACT1 Android code-transparency manifest.
ACT1 is signed by the pinned Mr. X Ed25519 key in the separate
`Deep/AndroidCodeTransparency/ACT1/v1` domain. It binds the application ID, versionCode, ordered
Play app-signing lineage, and the semantic SHA-256 of every APK that bundletool can generate from
the approved AAB. Semantic APK hashes cover the ordered ZIP entry names, expanded lengths, and
contents; APK signature metadata and ACT1 itself are excluded to avoid the signing/content cycle.
All other entries, including application-readable `META-INF` resources, remain covered.

Runtime reads ACT1 from the immutable base APK, verifies its canonical encoding, hash, Mr. X
signature, package/version/lineage tuple, and requires the installed base plus every installed
split identity/content digest to occur in ACT1. It repeats fresh `PackageInfo`, signer-lineage,
artifact-inventory, and semantic-content verification before returning the PMA approval identity.
Thus copying a public build ID into different code with the same package/version/Play key fails.
The ordinary split-set digest remains a second completeness/TOCTOU control, not the content
authorization root.

`playAppSigningLineageSha256` pins the certificate lineage returned for the installed application
by Android/Google Play. It must contain the Play **app-signing** certificate lineage in platform
order. The Play upload certificate is a different key: `build-android-play.ps1` continues to
verify it independently for the uploaded APK/AAB, and it must not be substituted into the
installed app-signing lineage.

Production Android attestation requires the API 28 signing-lineage API, so the application minimum
Android version is Android 9 (API 28). No API 26/27 single-signer downgrade exists.

Usage:

```powershell
eng/build-android-play.ps1 `
  -TrustFloorBundle C:\secure\deep-production-trust.json `
  -AndroidCodeTransparencyManifest C:\secure\deep-android.act1 `
  -BundletoolJar C:\tools\bundletool-all.jar
eng/build-windows-msix.ps1 -TrustFloorBundle C:\secure\deep-production-trust.json <other args>
```

The Android approval lane is two-pass. `prepare-android-code-transparency.ps1` builds a Release
candidate with the explicit preparation-only MSBuild property, verifies the exact pinned
bundletool 1.18.3 binary (SHA-256
`a099cfa1543f55593bc2ed16a70a7c67fe54b1747bb7301f37fdfd6d91028e29`), expands it in default
mode, and writes an unsigned ACT1 plus the exact detached signing bytes outside the release
artifact directory:

```powershell
eng/prepare-android-code-transparency.ps1 `
  -TrustFloorBundle C:\secure\deep-production-trust-preparation.json `
  -MrXEd25519PublicKey <64-lowercase-hex> `
  -PlaySignerLineageSha256 <hash-or-pipe-separated-lineage> `
  -BundletoolJar C:\tools\bundletool-all.jar `
  -OutputDirectory C:\secure\deep-act1-request
```

Mr. X signs `android-code-transparency.signing.bin` offline and returns a raw 64-byte detached
signature. The no-private-key helper assembles and verifies the signed manifest:

```powershell
dotnet run --project eng/tools/Deep.AndroidTransparency.Tool -c Release -- assemble `
  --unsigned-manifest C:\secure\deep-act1-request\android-code-transparency.unsigned.act1 `
  --signature C:\secure\deep-act1.sig `
  --output C:\secure\deep-android.act1
```

The preparation command cannot satisfy the normal Release ACT1 target and never copies a candidate
to `artifacts/android-release`. The publish
pass injects that external ACT1 file, rebuilds the AAB, expands the final AAB again with bundletool,
and cryptographically verifies the complete generated set against ACT1 before copying any AAB/APK
to `artifacts/android-release`. The build host never receives the Mr. X signing key. Because ACT1
and APK signing metadata are excluded from semantic hashing, injection and Play signing do not
create a hash cycle. Android buildId is deliberately not compiled into assembly metadata; runtime
derives it only as SHA-256 of the canonically decoded, pinned-Mr. X-signed ACT1. Any other
pass-to-pass code/resource change fails verification.

Source and local packaging verification do not replace verification of artifacts transformed and
delivered by Google Play. Before Beta promotion, upload the build to a closed track, retrieve the
Play-delivered base/splits through Internal App Sharing or an equivalent controlled device lane,
build the exact split-identity inventory, and run the ACT1 tool `verify` command against that
inventory. The gate must cover representative API/ABI/density/language configurations (at least
nine configurations) and the installed Play app-signing lineage; any unapproved semantic split
digest blocks promotion.
