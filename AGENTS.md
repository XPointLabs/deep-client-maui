# Deep Client MAUI agent rules

The workspace rules in `../AGENTS.md` apply. This file contains only MAUI-specific deltas.

## Owns

- `src/Deep.Client.Maui`: XAML, app startup, navigation, platform adapters and packaging.
- `src/Deep.Client.Maui.Core`: testable ViewModels, routes and UI-facing abstractions.
- `tests/Deep.Client.Maui.*`: UI, composition, outbox, platform and physical-lane contracts.
- `eng/`: signed build, strict-lane and physical Android/Windows automation.

Portable message, attachment, call, persistence and transport semantics belong in
`deep-client-shared`; protocol bytes and crypto belong in `deep-protocol`.

## Repository rules

- Keep testable behavior in ViewModels; code-behind is for lifecycle, navigation and platform UI.
- Keep `AutomationId` values stable unless the matching automation changes in the same commit.
- Release composition must fail closed when real transport, trust or authority inputs are absent.
- Use compile-time physical-lane namespaces for UAT SecureStorage/SQLCipher keys. Never wipe or
  mutate the production package/data while preparing physical evidence.
- A visible control must work, be explicitly unavailable, or be absent.
- Update `docs/ARCHITECTURE.md` or release docs when composition, config or supported UI changes.

## Verify

```powershell
dotnet test tests/Deep.Client.Maui.ViewModels.Tests/Deep.Client.Maui.ViewModels.Tests.csproj
dotnet test tests/Deep.Client.Maui.SmokeTests/Deep.Client.Maui.SmokeTests.csproj
dotnet test tests/Deep.Client.Maui.UiTests/Deep.Client.Maui.UiTests.csproj
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-windows10.0.19041.0
```

Run the affected Android build/physical phase for Android runtime claims. Release composition,
signing or device evidence must use the scripts under `eng/`; see `eng/AGENTS.md`.
