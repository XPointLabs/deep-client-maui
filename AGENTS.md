# Agent Specification - Deep Client MAUI

Last updated: 2026-06-10.

## Mission

`deep-client-maui` is the production client shell for Deep across Android, iOS, Windows, and Mac Catalyst. It owns MAUI pages, platform adapters, app startup, navigation, device-bound services, and the MVVM layer that makes `deep-client-shared` usable as a real messenger.

The repository must deliver a new Deep client experience while preserving launch-critical Session behavior. Do not create a separate native Android/iOS/Desktop client track unless the program governance docs explicitly approve it.

## Source Of Truth

- Workspace entry point: `../prompts/00_Agent_Entry_Point.md`.
- Shared runtime contract: `../deep-client-shared/AGENTS.md`.
- Current MAUI architecture: `docs/ARCHITECTURE.md`.
- Session porting rules for this repo: `docs/SESSION_PORTING.md`.
- UI smoke baseline: `tests/Deep.Client.Maui.UiTests/Baselines/session-ui-baseline.json`.

When documents conflict, prefer the entry point and current code/tests, then update the stale doc in the same change.

## Ownership Boundaries

Owned here:

- `src/Deep.Client.Maui`: MAUI app, `MauiProgram`, platform folders, XAML pages, styles, and platform service implementations.
- `src/Deep.Client.Maui.Core`: ViewModels, commands, route catalog, auth navigation state, and abstractions needed to test UI behavior without MAUI.
- `tests/Deep.Client.Maui.*`: ViewModel, route smoke, device, and Windows UI automation tests.

Not owned here:

- Message, conversation, group, attachment, call-signaling, persistence, or transport domain semantics. Put those in `deep-client-shared`.
- Protocol wire formats or crypto. Put those in `deep-protocol`.
- Backend service behavior. Put that in service repos or `deep-devops`.

## Product Direction

Build a new Deep UX, not a skin over Session. The UX can be cleaner and MAUI-native, but it must keep the user-visible Session contract for launch-critical flows:

- create account and restore account,
- see Session ID and profile state,
- start a one-to-one chat from Session ID,
- search/open conversations,
- send/receive messages,
- stage/send attachments,
- create/open/manage groups,
- register/unregister push where supported,
- start/respond/end calls where call features are enabled,
- sign out and optionally wipe local state.

Any button rendered in a release path must either work, be hidden behind an unavailable feature state, or show a clear error path. Avoid decorative controls that look interactive.

## Architecture Rules

- Keep logic in ViewModels when it can be tested without MAUI. Use code-behind only for shell navigation, platform pickers, alerts, and page lifecycle.
- Keep all platform side effects behind interfaces in `Maui.Core` or `deep-client-shared/Platform`.
- Release builds must fail fast if required real transport endpoints are missing. Stub transport is debug/test only.
- XAML should follow the existing `SessionTheme.xaml` resource system. Do not introduce a separate visual design system without updating this spec and architecture docs.
- Prefer responsive layouts with stable dimensions for toolbars, buttons, chat rows, and composer controls.
- Keep `AutomationId` values stable for launch-critical controls and update the UI baseline when controls change intentionally.

## Required Verification

For ViewModel/runtime-facing changes:

```powershell
dotnet test tests/Deep.Client.Maui.ViewModels.Tests/Deep.Client.Maui.ViewModels.Tests.csproj --no-restore
dotnet test tests/Deep.Client.Maui.SmokeTests/Deep.Client.Maui.SmokeTests.csproj --no-restore
```

For XAML, shell, platform adapter, or release wiring changes:

```powershell
dotnet test tests/Deep.Client.Maui.UiTests/Deep.Client.Maui.UiTests.csproj --no-restore
dotnet build src/Deep.Client.Maui/Deep.Client.Maui.csproj -f net10.0-windows10.0.19041.0 --no-restore
```

When workloads are available, also build Android and Mac Catalyst/iOS targets. If a platform build cannot run locally, say exactly why and rely on GitHub Actions evidence.

## Acceptance Gates

A client change is not complete until:

- UI controls in the touched flow are reachable and command-backed.
- ViewModel tests cover command enablement and success/failure behavior.
- UI smoke baseline remains accurate for onboarding, conversations, chat, and group chat.
- Any Session parity delta is documented in `docs/SESSION_PORTING.md` or `docs/ARCHITECTURE.md`.
- Release profile still has no stub transport dependency.

## Stop-The-Line Conditions

Stop and open/fix a blocker before moving on if:

- a launch-critical flow depends on `StubSessionBackend` in non-Debug builds,
- a visible release button has no working action or no explicit disabled/unavailable state,
- Session ID, recovery phrase, or attachment data is logged or exposed in diagnostics,
- MAUI and shared runtime behavior diverge silently across Android/iOS/Desktop,
- UI tests need to be weakened instead of reflecting intended behavior.

## Agent Workflow

1. Read this file, `docs/SESSION_PORTING.md`, and the relevant ViewModel/page tests.
2. Inspect the equivalent Session upstream screen or behavior before claiming parity.
3. Implement the smallest coherent slice across XAML, code-behind, ViewModel, and tests.
4. Run the required local checks.
5. Update README/docs when behavior, env vars, commands, or acceptance criteria change.
6. Commit only this repository's changes. Do not mix client and backend commits unless the task explicitly requires it.
