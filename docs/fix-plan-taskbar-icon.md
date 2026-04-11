# Fix Plan: Taskbar Icon Feature

## Branch
`agent/taskbar-icon-feature`

---

## 1. Rewrite `SystemTrayIcon.cs` using P/Invoke (~150 lines)

Replace the entire WinForms-based implementation with native Win32:

- **P/Invoke structs**: `NOTIFYICONDATA`, menu-related types
- **P/Invoke methods**: `Shell_NotifyIcon`, `CreatePopupMenu`, `AppendMenu`, `TrackPopupMenu`, `SetForegroundWindow`, `DestroyMenu`
- **Hidden message window**: Use `HwndSource` (WPF) with `HWND_MESSAGE` parent to receive `WM_USER+0` tray callbacks
- **Icon loading**: Keep `System.Drawing.Icon` for resource loading, extract `.Handle` (`HICON`) for `NOTIFYICONDATA`
- **Context menu**: Native `CreatePopupMenu` → `AppendMenu` → `TrackPopupMenu` with "Restore" and "Exit" items
- **Same public API**: `Show()`, `Hide()`, `UpdateToolTip()`, `OnIconClicked`, `Dispose()` — no changes to callers needed

## 2. Remove WinForms from `WpfAppTest.csproj`

- Remove `<UseWindowsForms>true</UseWindowsForms>`
- Add `<PackageReference Include="System.Drawing.Common" Version="8.0.0" />` (needed for `Bitmap`, `Rectangle`, `ImageFormat` used in `MainWindow.xaml.cs`)

## 3. Revert namespace qualifications (4 files)

With WinForms global usings gone, these resolve unambiguously:

| File | Revert |
|---|---|
| `App.xaml.cs:16,65,144` | `System.Windows.Application` → `Application`, `System.Windows.Point` → `Point` |
| `DapploExtension.cs:8,13,14` | `System.Windows.Point` → `Point` |
| `Utilities.cs:8,12,51` | `System.Windows.Point` → `Point` |
| `WPFExtensionMethods.cs:8,11,29` | `System.Windows.Point` → `Point` + add trailing newline |

## 4. Revert namespace qualifications in `MainWindow.xaml.cs`

These were only ambiguous with WinForms types:

- `System.Windows.Controls.TextBox` → `TextBox` (lines 24, 344, 374)
- `System.Windows.Input.KeyEventArgs` → `KeyEventArgs` (lines 71, 431, 698)
- `System.Windows.Input.MouseEventArgs` → `MouseEventArgs` (lines 108, 113, 213)
- `System.Windows.Input.Cursors` → `Cursors` (lines 666, 675)
- `System.Windows.Application` → `Application` (lines 511, 547, 551, 565)

**Note**: `System.Windows.Point` stays qualified in this file because `using System.Drawing;` (needed for `Rectangle`, `Bitmap`) introduces a `Point` conflict.

## 5. Fix double disposal (`MainWindow.xaml.cs`)

- Keep `_systemTrayIcon?.Dispose()` in `Window_Closing` (line 519) — natural cleanup point
- Remove from `Window_Unloaded` (line 621)

## 6. Save/restore window state (`MainWindow.xaml.cs`)

- Add field: `private WindowState _previousWindowState = WindowState.Maximized;`
- In `Window_StateChanged`: save `_previousWindowState = WindowState` before setting minimized
- In `RestoreFromTray`: use `WindowState = _previousWindowState` instead of hardcoded `Maximized`

## 7. `Console.WriteLine` → `Debug.WriteLine`

Replace debug/error logging in `MainWindow.xaml.cs` (lines 550, 583) and `SystemTrayIcon.cs`.

---

## Execution order

1. Rewrite `SystemTrayIcon.cs`
2. Update `WpfAppTest.csproj`
3. Revert all namespace qualifications
4. Fix double disposal + window state restore
5. `dotnet build` to verify
6. Commit
