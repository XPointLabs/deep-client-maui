# Windows release

## Current release target: portable ZIP

The first public Windows release targets an unpackaged self-contained ZIP,
not MSIX. The application project carries both .NET and Windows App SDK for
`WindowsPackageType=None`, with Deployment Manager auto-initialization disabled.
SDK-owned registration-free WinRT initialization remains enabled. A framework-
dependent publish directory is not a portable release payload: it can fail
before the MAUI entry point with `REGDB_E_CLASSNOTREG`.

This deployment choice follows Microsoft's
[self-contained deployment guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
and [initializer requirements](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/project-properties).
Package/Main/Singleton-dependent WNS is not a capability of the portable profile;
no-push foreground/resume polling must pass device E2E before release. A single
EXE is only a later packaging optimization, not permission to omit runtime,
crypto, carrier or resource files from the ZIP.

Build a separate native x64/ARM64 artifact; verify startup, signing, embedded
authority, manifest digests and physical messaging against that exact artifact.
The current project settings fix runtime custody, but do not establish a signed
release bundle, live transport readiness or device messaging evidence. The
supported portable release authoring/evidence lane still needs completion.

## Deferred MSIX profile

The following existing MSIX authoring lane is not the current release target
and must not be used as evidence for the portable ZIP.

The optional MSIX lane creates a signed, framework-dependent MSIX for x64 or ARM64. The release
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

For a local QA sideload certificate only, add
`-AllowUntrustedSelfSignedCertificate`. The switch is rejected as a bypass for
any other signature failure: the certificate must be self-signed, the package
signer thumbprint must exactly match `-CertificateThumbprint`, and the only
accepted validation error is an untrusted root. Permanent production and Store
builds must omit this switch and use the trusted publisher certificate.

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
