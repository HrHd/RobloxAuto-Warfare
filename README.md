# Roblox Auto - Warfare

A single-window automation tool for the Roblox **Warfare** game loop. It reads the
screen with the built-in Windows OCR and sends ordinary input (no injection, no
memory access), so it survives map and resolution changes.

## What it does

- **REJOIN** the last server you were on (deep link with `gameInstanceId`, so it
  pins the *same* server), with an optional auto-reconnect.
- **AUTO RUN**: wait for load -> pick team -> switch drone -> DEPLOY -> find the
  Base -> select the warhead -> **Deploy As Drone**.
- Reads the drone HUD for **HOME** distance and shows a fake **RF feed** overlay.
- On **NO SIGNAL** (drone crash) it auto-rejoins the same server.
- A terminal-style "connecting" cover (also available as an OBS browser source),
  and a flashing **LAND NOW** alert.
- Controller binds + hotkeys, all rebindable, plus a UI language dropdown.

## Files

| file | purpose |
|---|---|
| `RobloxAuto.cs` | the whole application (single file, C# 5) |
| `app.manifest` | `requireAdministrator` (the game ignores non-elevated input) |
| `ocr.ps1` | resident Windows OCR helper (must sit next to the exe) |
| `overlay.html` | OBS browser-source overlay page |
| `dist/` | ready-to-run package (exe + ocr.ps1 + overlay.html + README) |
| `RobloxAuto-Warfare.zip` | zipped `dist/` |

## Build

```bat
csc /nologo /target:winexe /out:RobloxAuto.exe /win32manifest:app.manifest ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll RobloxAuto.cs
```

Requires the .NET Framework 4 `csc` (ships with Windows). C# 5 only.

## Run

Run `RobloxAuto.exe` as Administrator. Default controller binds: `BACK` rejoin,
`LB` auto, `Y` stop, `B` land now, `START` straight reconnect.

## OBS overlay

Add a Browser Source -> `http://localhost:8730/` at 1920x1080 (or load
`overlay.html` as a local file).
