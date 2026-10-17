# Screenline UX contract

Source brief: user requested a Windows recreation of https://github.com/alejandrobujan/tendedero. Upstream reference commit and platform differences are in docs/MIGRATION.md. Explicit Windows adaptation decisions below preserve the user's source images.

## Business context

| Concern | Source | Observable consequence |
|---|---|---|
| Platform | User request: Windows version | Native C# WPF, Windows 10/11 x64, no browser runtime |
| Reference behavior | Upstream README + sources at reviewed commit | Top-edge reveal, hanging images, copy/open/hold/drag, tray |
| Brand | Upstream LICENSE, preserved in THIRD-PARTY-NOTICES.txt | Independent Screenline name, code-drawn independent icon |
| Privacy | Upstream local-only behavior, conservative Windows adaptation | No application networking; clipboard monitoring starts disabled; private/mixed formats skipped |
| Image lifecycle | Windows adaptation | Import makes managed copy; remove is reversible and never deletes source; export/drag copy only |

## Canonical UI Map

| Capability | Canonical owner | Source of truth | Allowed variants | Verification |
|---|---|---|---|---|
| Form | SettingsWindow | SettingsStore + AppSettings | Native CheckBox, ListBox, file/folder dialogs | ServiceVerification settings persistence; rendered settings |
| Scrollbar | Native WPF ScrollViewer / ListBox | Windows system controls | Horizontal shelf, vertical settings | Shelf/render checks |
| Toast | ShelfWindow.SetStatus | This contract | Informational or failure text; no position change | Real shelf render |
| CRUD | LibraryStore | This contract + docs/MIGRATION.md | Import, reversible remove, undo, export copy | ServiceVerification recovery/rollback/source retention |

## Flow ledger

| Action | Result | Failure and recovery |
|---|---|---|
| Single click or Enter/Space on image | Copy full bitmap and managed file reference; keep item | Shelf status says retry if clipboard is busy |
| Double click | Open internal preview | Source remains; error text on shelf |
| Hold 500ms or context-menu 标注图片 | Open drawing editor | Original remains; save creates new image |
| Drag to application/folder | Offer bitmap/file COPY | Never delete files or remove item based on destination |
| 另存图片 | Windows save picker, export copy, keep item | Do not report success if IO fails |
| 移出晾台 / Delete | Persist retained internal removal; show Ctrl+Z hint | Atomic index failure rolls list back; error remains visible |
| Ctrl+Z / tray 撤销移出 | Restore last removal, including after restart | No removal means no action |
| Import / drop | Validate supported image, copy into managed library, deduplicate bytes | Report invalid/unreadable input and keep existing items |
| New monitored file | Wait for stable readable file, then import | Bounded retry, no imports of old files at startup |
| Save settings | Persist first, then apply settings | Failed persistence leaves active settings unchanged |
| Clipboard setting | Explicit checkbox opt-in | Recovery from stale backup forces opt-in off |

The shelf is intentionally non-modal. Automatic reveal must not activate the window. Manual hotkey/tray access supports keyboard focus. Escape conceals the shelf; keyboard access is the alternative to hover. Menus, dragging, press and pending click prevent automatic hiding. Moving away for 650ms hides after an initial 3-second grace period.

No irreversible file deletion is exposed. Managed files and retained removals keep occupying disk space until the user manages that local folder. There is no retention-based eviction.
