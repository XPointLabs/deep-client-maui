# MAUI engineering and physical-lane rules

The workspace and repository `AGENTS.md` files apply.

## Safety boundary

- Always pass an explicit Android serial; do not rely on the script default.
- The production package and its data are audit-only: snapshot before/after and never alter them.
- UAT reset is allowed only through `ProvisionIdentity -ResetWindowsUatLocalState`.
- Do not delete the whole Windows `securestorage.dat`; reset only the physical-lane state/slot.
- Keep APK, policy, holder, authority and evidence bound to the current committed source revision.
- Read certificates/keys only through existing scripts. Never print or copy secret material into
  repository artifacts; evidence may contain only explicitly sanitized hashes and states.
- Physical evidence requires a real device and production-grade authenticated HTTPS transport.
  Compile-only, mock and simulated results cannot satisfy a physical or release claim.

## Workflow

1. Run the focused smoke/UI contract tests for the changed harness behavior.
2. Run the script without `-Execute` when a preflight/dry-run path is available.
3. Execute one bounded phase and inspect its machine-readable result before advancing.
4. Restore fault injection/baseline state even after a failed chaos phase.

```powershell
./eng/Invoke-PhysicalMau2CrossPlatform.ps1 `
  -Phase <phase> `
  -AndroidSerial <explicit-serial> `
  -MrXPublicKeySha256 <sha256> `
  -Execute
```

Valid phases are defined by the script's `ValidateSet`; do not bypass its provenance, process,
deadline, package-audit or evidence-containment checks.
