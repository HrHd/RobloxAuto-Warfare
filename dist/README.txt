Roblox Auto - Warfare
=====================

Keep these three files together in the same folder:
  RobloxAuto.exe
  ocr.ps1
  overlay.html

SETUP
-----
1. Run RobloxAuto.exe (it asks for Administrator - required so it can send
   input to the game; the game ignores input from a non-elevated app).
2. Launch Roblox / the game. The tool reads the last server from your Roblox
   logs automatically; press Refresh if needed.

CONTROLS (defaults, all rebindable in the panel)
------------------------------------------------
  Keybind       : set your own REJOIN key in the panel
  F8            : rejoin
  F5            : AUTO RUN
  Controller    : BACK  = rejoin
                  LB    = AUTO RUN
                  Y     = STOP
                  B     = LAND NOW
                  START = straight reconnect (no overlay)

OBS OVERLAY (optional)
----------------------
Add a Browser Source:
  URL   : http://localhost:8730/
  size  : 1920 x 1080
 or tick "Local file" and browse to overlay.html in this folder.
Shows the connection CLI + progress, and HOME distance while flying.

NOTES
-----
- Works by reading the screen (Windows OCR) and sending input - no injection.
- NO SIGNAL (crash) auto-reconnects to the same server.
- Leaving a server manually is detected and will NOT auto-reconnect.
- Settings are saved to settings.ini next to the exe.
