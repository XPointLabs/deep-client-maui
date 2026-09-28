# DID2 HTTPS physical checkpoint — 2026-09-28

This records an incomplete real Windows diagnostic, not messaging or release
approval. Registry and seed1–seed3 are production infrastructure; Mr. X permits
testing there until he explicitly reports users exist. There is no remote UAT.

- MAUI code `2e8eae6bdd68bb7b297a5543be7d202706a49042` passed 21 clean-account
  tests, 119 smoke tests and the selected seven-file secret scan. Actual MSBuild
  admission guards accepted the complete DID2 build inputs and rejected retired
  mailbox/PMA inputs and a zero network ID. Script preflight passed before build.
- The supported DID2 Windows script built an unpackaged ARM64 Debug candidate.
  Apphost SHA-256: `808c9dffcfc8f3f2326c4a00d7eb80341aeb6792b49c8d79a515590106124a51`;
  assembly SHA-256: `26746613e04679e09d11d8ffafff042c08b4da5a57f79f2662be66d85194292b`.
  No installer, package deployment or Release signing was performed.
- The real native window displayed onboarding. A separate test account was
  created by filling the name and pressing Create; settings showed retained,
  encrypted recovery without revealing or copying it. Account and device state
  files exist in the dedicated per-network physical lane; normal application
  data and both older probes were untouched. The app was then closed through
  its UI and the next committed candidate reopened the same account with its
  recovery phrase still hidden and retained.
- The explicit HTTPS action failed with a request-send error and retained the
  account. The independent Registry floor advanced from generation 17/tree 6
  to 18/tree 7, consistent with accepted admission, but not independent client
  proof completion or pre-key publication. An identity-neutral external HTTPS
  closure query returned 200 and 11,300 bytes; it is untrusted distribution,
  not device authority or a verified two-replica receipt pair.
- Diagnostic HTTP failures now carry only fixed operation stage and exception
  classification, preserving the inner exception without exposing its private
  message, URL or request bytes. This does not add a retry, fallback or bypass.
- Candidate `03bb8404d049236bea61fa8a72ace587bb8063e9` rebuilt successfully
  and reopened the same physical account. Apphost SHA-256:
  `2c5152b0d6d487abb981ccbf33eb792c8bf88e532ba30bf737f7cec6a4b20ec2`;
  assembly SHA-256:
  `b9ef87ab8a460d576b65fd63cc5cd6f694f73057991b9a84b0b909a6fdd1d511`.
  The actual window reported a bounded HTTP timeout on the next network
  action. The account remained visible; neither complete proof nor publication
  is claimed. Timeout failures now receive the same fixed-stage classification
  as request failures; HTTP deadlines and TLS checks remain unchanged.

Next: locate the exact failing stage, finish independently verified two-replica
publication, then compose DID2 contacts/DPH2/receive and verify Windows↔Android
text, file/image integrity and groups. None of those gates is closed here.

## Android diagnostic preparation

Code `915d05ca0fd3ee9aa7adef10edb7aa2defe790d3` compiled the dedicated ARM64
HTTPS package with zero warnings/errors. Its new signing step initially failed:
two explicit file-based password requests cannot consume the original
single-line password source. The existing `Invoke-ExplicitApkSigning` SDK helper
successfully signed the zip-aligned disposable APK using the PKCS12 store
password alone. Independent signature and 16-KiB-page alignment checks passed.
The actual production-custody signer is
`bf8abed56e852d0902796f9e0131789f188688784a07ad516760f127684204c2`,
not the older debug probe signer. No signer-mismatch check was bypassed.

The guarded installer passed preflight and installed/launched only
`network.xpoint.deep.did2https`; APK SHA-256:
`9829d57d070023bc4506b4d0edb00b50410fd078408510e6646aca57185c4c6d`.
Before/after path and stable metadata hashes matched for production, older
E2E and account-probe packages. No account was reset. This is installation
evidence only, not Android account/proof/message evidence.

The first guarded UI inspection rejected foreground ownership because the
Samsung `dumpsys window windows` subsection omits `mCurrentFocus`. Independent
read-only full-window and resumed-activity checks both identified the exact
HTTPS package in front, with no keyguard or captured runtime exception.
The harness now queries full `dumpsys window`; the signer now mirrors the
existing working helper and uses an aligned input. Clean tests passed 22/22,
smoke tests 119/119, and the revised seven composition guards passed. Actual
UI phases on the corrected harness remain to be completed.
