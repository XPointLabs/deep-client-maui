# Windows release

Deep ships as a signed, framework-dependent MSIX for x64 or ARM64. The release
artifact is a complete sideload ZIP containing the application MSIX, signing
certificate, install scripts, and the architecture-specific Windows App Runtime
dependency packages. Packaging binds the executable and bundled Xray binary to
the XPoint Labs publisher and lets WNS activate the app for background raw
notifications.

## One-time identity setup

1. Reserve the permanent package identity and publisher subject used for Deep.
2. Create a multi-tenant Microsoft Entra app registration for Windows App SDK
   push. Record the application/client ID, tenant ID, and service-principal
   object ID. Store the client secret only on the push server.
3. Send the package family name, Entra application ID, and service-principal
   object ID to Microsoft using the Windows App SDK WNS PFN mapping process.
4. Install the code-signing certificate, including its private key, in
   `Cert:\CurrentUser\My` on the trusted release machine. Its subject must
   exactly equal the package publisher.

The WNS application/client ID is used by the manifest COM activator. The
service-principal object ID is embedded as `DEEP_WINDOWS_PUSH_REMOTE_ID` and is
not a secret. The tenant ID, client ID, and client secret are server-side only.

## Build

```powershell
.\eng\build-windows-msix.ps1 `
  -PackageIdentityName '<permanent package identity>' `
  -Publisher '<exact certificate subject>' `
  -PublisherDisplayName 'XPoint Labs' `
  -WnsAppId '<Entra application/client ID>' `
  -WnsRemoteId '<service-principal object ID>' `
  -CertificateThumbprint '<SHA-1 certificate thumbprint>' `
  -RuntimeIdentifier win-x64
```

Repeat with `win-arm64` for native ARM64 distribution. The script fails closed
when identity data, WNS IDs, certificate validity, manifest generation, package
signing, or signature verification is invalid. It also verifies that no
manifest tokens remain, every activation entry names the executable actually
stored in the MSIX, and every declared package dependency has a compatible
signed package in the sideload payload.

## Release payload

Artifacts are written to `artifacts/windows-release`:

- `Deep-<display-version>-v<version-code>-<rid>-sideload.zip` is the artifact to
  distribute.
- The matching `-sideload` directory is the expanded copy for local inspection.
- `SHA256SUMS.txt` inside the payload records every included file hash. The build
  also prints the ZIP SHA-256 digest.

Do not distribute the application MSIX by itself. A clean machine may not have
the required Windows App Runtime framework package, while the complete payload
contains every dependency selected by the Windows App SDK packaging targets.

To install, extract the ZIP without changing its directory structure and run
the generated installer from the extracted directory:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\Install.ps1
```

The generated installer selects and installs the applicable dependency packages
before registering Deep. Keep `Dependencies`, the `.cer` file, and both install
scripts beside the application MSIX.

Release builds read endpoints and trust pins only from embedded resources.
Environment variables and loose `deep.release.env` files are Debug-only, so an
operator or local process cannot silently replace production routers or TLS
pins after the package is built.
