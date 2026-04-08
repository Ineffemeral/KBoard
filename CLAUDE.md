# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Kboard-language-layout-keybind** is a Windows system tray utility (v3.0) written in C# (.NET/WinForms) that remaps CapsLock to a language layout switcher.

### Key behavior
- **Short CapsLock press** (< hold duration): sends `Win+Space` to cycle Windows input language/layout
- **Long CapsLock press** (≥ hold duration): sends a real CapsLock toggle
- **Block native Shift/Ctrl layout toggle** (optional): intercepts lone Shift or Ctrl key-up and injects VK `0x87` to neutralize the default Windows IME toggle

## Build

This is a single-file C# project with no `.csproj` or solution file. Compile with the .NET Framework `csc` compiler:

```
csc /target:winexe /out:Kboard-language-layout-keybind.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Program.cs
```

Or open/import `Program.cs` into Visual Studio and build as a Windows Forms application targeting .NET Framework.

## Configuration

Settings are persisted in `config.txt` (same directory as the `.exe`), two lines:
1. Hold duration in milliseconds (default: `300`)
2. Block native toggle flag: `True` or `False` (default: `True`)

Users can also edit settings via the system tray icon → Settings dialog.

## Architecture

Everything lives in `Program.cs` (single file, ~445 lines), in namespace `CapsLockRemapper`:

- **`Program`** (static) — entry point, mutex (single-instance guard), low-level keyboard hook (`WH_KEYBOARD_LL`), tray icon, config load/save, and `HookCallback` containing all key interception logic.
- **`SettingsForm`** — borderless WinForms dialog for editing hold duration and block-toggle setting. Drag-to-move via `WM_NCLBUTTONDOWN`.
- **`PremiumButton`** — custom `Button` subclass with rounded corners and hover state, painted via `OnPaint`.

### Hook logic (`HookCallback`)
- Injected key events (`flags & 0x10`) are passed through without processing to avoid re-intercepting synthetic events.
- `_modifierPressedAlone` / `_currentModifier` track whether a Shift/Ctrl was pressed and released without any other key in between (to detect the "lone modifier" IME toggle gesture).
