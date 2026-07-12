# Windows desktop workspace

Windows replaces the `conversations` Shell root at runtime with `DesktopWorkspacePage`. The XAML root remains `ConversationsPage`, so Android and the other mobile targets keep their existing templates and navigation behavior.

## Layout contract

- Split mode starts at 680 DIP.
- The conversation column starts at 320 DIP and can be resized from 280 to 420 DIP.
- Narrow mode shows one column. Selecting a conversation opens the detail surface and the back button returns to the list without clearing the selection.
- `DesktopWorkspaceViewModel` keeps isolated direct/group view models per conversation, with a bounded 16-entry cache per kind. Drafts, replies, attachments, reactions, and in-flight sends therefore cannot leak into another dialog.
- Authentication transitions synchronously clear the list, selected detail, cached view models, and composer state before another account is rendered. Resizing only changes visibility and grid columns.

The dimensions follow the same proportions as Telegram Desktop's `window.style` and `dialogs.style`: a constrained left column, a 380 px-class minimum main column, 46 px avatars, and 62 px dialog rows.

## Ingress

`IConversationActivationTarget` lets notification activation select a desktop detail pane without pushing `ChatPage` or `GroupChatPage`. `IActiveComposerProvider` exposes the selected desktop composer to share ingress. `AppShell` retains route and binding-context fallbacks for mobile pages.

The foreground sync pump coalesces repeated push requests. It acknowledges the background marker only after a successful inbox and active-detail reconciliation. Push-driven clients retain a 15-minute recovery poll; clients without a working push registration use a 30-second fallback.

## Message input

Message bubbles attach the existing `MessageContextGestureBehavior`, so Windows uses native `ContextRequested` input, including right-click and Shift+F10. The desktop menu is a compact child of the detail pane and does not add a full-window scrim.

The context menu supports reply, reactions, copy, delete, open, save, share, and copy-filename actions. Direct and group composers support attachment-only messages and hold-to-record voice messages; voice playback stays inline with play/pause state and progress.
