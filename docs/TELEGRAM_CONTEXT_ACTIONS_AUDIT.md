# Telegram context actions audit

Last updated: 2026-07-08.

Scope: Deep MAUI Android/Windows client. Visual reference is the Telegram UI Kit file `MFu7je1PD6DMO9NpDvKpE6`, especially `Screens`, node `506:92242`, chat/media frame `506:98558`, and the discovered `Menu - iPhone` / `Quick Actions` / `Menu Items` layers. Behavior reference is Telegram Android `ChatActivity.java` in `source/Telegram`, notably `fillMessageMenu(...)`, `processSelectedOption(...)`, and `openPhotoViewerForMessage(...)`.

## Reference model

Telegram does not use one identical menu for all messages. Actions are computed by message type and permissions:

- Text/caption messages: reply, copy, forward, pin where allowed, edit for own editable messages, delete.
- Photo/video media: open on tap, contextual actions on long press or menu: reply, save to gallery, share file, forward, delete.
- Documents/music/files: open on tap, contextual actions: reply, save to downloads/music, share file, forward, delete.
- Grouped media: save/share can apply to the selected media group.
- Destructive actions are visually separated and colored as danger.
- Media viewer keeps photo viewing separate from message action selection.

The Telegram UI Kit reinforces the same pattern: message media has an inline quick action row (`reply/reactions`, `forward/share`, `more`) and the context menu is a compact surface with icons, 15-16 px labels, and no heavy gray blocks.

## Deep gaps before this pass

| Surface | Current Deep behavior | Telegram parity gap | This pass |
| --- | --- | --- | --- |
| Message bubble | Simple tap opens Reply/Copy/Delete menu | Context actions should not replace normal tap. Long press should open actions. | Add pointer-based long press and keep media tap for opening. |
| Text message menu | Reply/Copy/Delete only | Missing share/forward/pin/edit states. Forward/pin/edit need product flows. | Keep implemented actions; visually align and hide Copy for attachment-only messages. |
| Photo in chat | Tap opens inline viewer | No save/share/delete/reply actions from photo context or viewer. | Add media-aware rows: open photo, save, share, copy file name, reply, delete. |
| Photo viewer | Close + title only | Telegram viewer has contextual media actions through more menu. | Add top-right more action and reuse media action sheet. |
| File/document attachments | Open/Share/Copy filename only | Missing save to Downloads, reply, delete. | Add save, reply, delete and bind to parent message. |
| Group chats | Same reduced menu as 1:1 | Same gaps as direct chats. | Apply same behavior to group chats. |
| Chat list context | Some conversation options exist elsewhere, not unified with Telegram swipe/context actions | Pin/archive/mute/read state are separate feature work. | Document as deferred. |
| Forwarding | No Telegram-like forwarding picker wired to message menu | Requires route + conversation picker + send-as-forward metadata. | Deferred; avoid adding fake action. |
| Pin/edit captions/report/translate | Not product-complete yet | Telegram exposes these conditionally. | Deferred until backing model supports them. |

## Implementation notes

- Color palette stays Deep/XPoint: primary cyan/blue and existing danger color.
- Menus use the existing Telegram-inspired sheet styles, but rows must be conditional by message type.
- Save-to-device should copy files out of the app cache. Android uses MediaStore where available; desktop falls back to the user's Downloads directory.
- The audit should be updated whenever context actions change.
