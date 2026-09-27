// RobloxAuto.cs - one window for the Warfare loop.
//
//   REJOIN      rejoin the last server (deep link), auto-reconnect, OCR loading watch
//   AUTO RUN    wait for load -> team -> drone -> DEPLOY -> Base -> Deploy As Drone
//
// Everything is found at run time by OCR, so it survives different maps and
// resolutions. Input is external only (SendInput) - no injection, no memory access.
//
// Build:  csc /target:winexe /out:RobloxAuto.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll RobloxAuto.cs
// Needs:  ocr.ps1 next to the exe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

class RobloxAuto : Form
{
    // ================= config =================
    string _appDir, _cfgPath;
    string _placeId = "", _serverId = "";

    string _team = "Blue";       // "Blue" or "Red" - the team NUMBER changes every match
    string _drone = "MAVIC";
    string _bomb = "(none)";     // warhead chosen on the Team Base panel
    bool _asDrone = true;

    uint _hkRejoinKey = 0x77;   // F8
    uint _hkAutoKey = 0x74;     // F5
    uint _hkNightKey = 0x76;    // F7 - toggles night vision
    bool _capturingNight = false;

    string _padRejoin = "BACK";
    string _padAuto = "LB";
    string _padStop = "Y";
    string _padLand = "B";              // flashes the LAND NOW alert
    string _padReconnect = "START";     // straight reconnect, no overlay at all
    bool _padRejoinOn = true, _padAutoOn = true, _padStopOn = true, _padLandOn = true, _padReconnectOn = true;

    bool _autoRecon = true;
    bool _ocrWatch = true;
    string _watchWords = "Joining server";
    int _watchStuckSec = 45;

    // which parts of the AUTO flow to run (untick to skip)
    bool _stepTeam = true;
    bool _stepDrone = true;
    bool _stepDeploy = true;
    bool _stepBase = true;
    bool _stepBomb = true;

    // ================= runtime =================
    volatile bool _running = false;
    volatile int _autoGen = 0;      // bumped to cancel/restart a run
    DateTime _lastRejoin = DateTime.MinValue;
    string _lastRejoinServerId = "";
    DateTime _lastRejoinServerIdTime = DateTime.MinValue;
    Dictionary<string, long> _logPos = new Dictionary<string, long>();
    DateTime _watchSince = DateTime.MinValue;
    bool _watchOn = false;
    bool _padRejoinWasDown = false;
    bool _padAutoWasDown = false;
    bool _padStopWasDown = false;
    bool _padLandWasDown = false;
    bool _padReconnectWasDown = false;
    ushort _lastButtons = 0;

    // ================= ui =================
    TextBox txtServer, txtLog;
    Button btnRejoin, btnReconnect, btnRefresh, btnAuto, btnSetKey, btnStop, btnOpenLog, btnHighPrio, btnSetNight;
    Label lblKeyVal, lblNightVal;
    bool _capturingKey = false;
    ComboBox cmbTeam, cmbDrone, cmbBomb, cmbPadRejoin, cmbPadAuto, cmbPadStop, cmbPadLand, cmbPadReconnect;
    CheckBox chkAsDrone, chkAutoRecon, chkOcrWatch, chkPadRejoin, chkPadAuto, chkPadStop, chkPadLand, chkPadReconnect, chkTop, chkNoAct, chkClickKey, chkHudAuto;
    CheckBox chkStepTeam, chkStepDrone, chkStepDeploy, chkStepBase, chkStepBomb;
    CheckBox chkAutoAfterRejoin;
    NumericUpDown numAutoDelay;
    NumericUpDown numPreDelay;
    bool _autoAfterRejoin = false;      // after a rejoin, wait for the game UI then run AUTO
    int _autoAfterRejoinMs = 5000;      // small settle so the reconnect begins before AUTO
    int _preRejoinMs = 0;               // hold this long (LAND NOW showing) before relaunching
    bool _watchHome = false;            // after Deploy As Drone, count down the distance to HOME
    bool _hudAutoDetect = false;        // scan the screen for a drone view (only needed if you pick a drone BY HAND)
    CheckBox chkRfWatch;
    RFOverlayForm _rfForm = null;
    System.Windows.Forms.Timer _rfBlink = null;
    volatile bool _rfRun = false;
    string _rfHomeText = "----";
    // live telemetry for the FPV/UAV HUD (read from the drone OSD)
    string _hudHome = "----", _hudSpd = "", _hudAlt = "", _hudAgl = "", _hudHdg = "";
    string _lastHudDbg = "";
    string _lastDroneDbg = "";
    bool _ulwLogged = false;
    string _homeLastText = null;              // last accepted HOME, for the jump filter
    float _homeLastM = -1f;
    long _homeLastAt = 0;
    float _hudRoll = 0f, _hudRollTarget = 0f;     // horizon bank, degrees (eased)
    float _hudPitch = 0f, _hudPitchTarget = 0f;   // horizon vertical offset, px (eased)
    float _hudDetRoll = 0f, _hudDetPitch = 0f;    // what DetectHorizon last measured (image-based)
    float _hudSmRoll = 0f, _hudSmPitch = 0f;      // smoothed across measurements
    bool _hudSmSeeded = false;
    float _padLx = 0f, _padLy = 0f, _padRx = 0f, _padRy = 0f;   // sticks, -1..1, dead-zoned
    float _hudLockRoll = 0f, _hudLockPitch = 0f;  // base horizon (image-derived, corrected over time)
    float _hudCtrlRoll = 0f, _hudCtrlPitch = 0f;  // stick rotation integrated on top of the base
    float _hudFRoll = 0f, _hudFPitch = 0f;        // fused horizon (complementary filter, 50fps)
    volatile bool _hudShown = false;              // RF loop tells HudTick the feed is up
    int _hudSecs = 0;
    bool _hudLocked = false;
    bool _hudNeedLock = false;                    // true from Deploy As Drone until we lock
    bool _hudDetValid = false;                    // DetectHorizon found a confident line
    long _hudDetAt = 0;                           // when it did (so a stale fix is not trusted)
    long _hudLogAt = 0;
    long _hudPrevTick = 0;
    bool _hudStyleUav = false;                     // MAVIC gets the UAV-style layout
    FpvHudForm _hudForm = null;
    CheckBox chkHud, chkNight;
    // ---- HUD tuning dials (live-adjustable, hotkeys below) ----
    float _hudPitchRate = 660f;                    // px/s the horizon moves per unit of stick pitch
    float _hudRollRate = 220f;                     // deg/s per unit of stick roll
    float _hudLockTau = 0.25f;                     // s - camera lock time constant when centred (small = snappy)
    float _hudFlyTau = 0.9f;                       // s - camera correction while flying
    float _hudBias = 6f;                           // px - constant downward offset of the detected line
    float _hudLeftPx = 50f;                        // px - max horizon offset from the LEFT stick (bounded)
    NumericUpDown numPitch, numRoll, numLock, numFly, numBias;
    bool _hudOn = true;                            // draw the FPV/UAV HUD while flying
    bool _nightVision = false;                     // invert the whole display (Magnifier color effect)
    DateTime _flightStart = DateTime.MinValue;   // when Deploy As Drone happened
    int _flightOcrBase = -1;                      // last FLIGHT mm:ss read from the OSD (seconds)
    long _flightOcrAt = 0;                        // when that read was taken
    long _lastInDroneAt = 0;                      // last time the drone OSD was actually on screen
    DateTime _leftAt = DateTime.MinValue;        // when the player voluntarily left a server
        ConsoleForm _black = null;          // terminal-style cover shown while reconnecting
    volatile bool _blackWatch = false;
    float _flowPct = 0f;                // monotonic CLI progress across the whole flow
    bool _blackScreen = true;           // show the black cover while reconnecting
    CheckBox chkBlack;
    OverlayForm _land = null;           // the LAND NOW alert, tracked so STOP can close it
    volatile int _stopGen = 0;          // bumped by STOP to cancel pending delayed actions
    volatile bool _halted = false;      // STOP pressed: suppress ALL auto-reconnect until next AUTO
    TextBox txtClickKey;
    string _clickKeyText = "0";
    bool _topMost = true;
    bool _noActivate = true;    // panel never steals focus from the game (see WS_EX_NOACTIVATE)
    bool _filling = false;      // suppresses the bomb handler while the list is rebuilt
    TextBox txtWatch;
    NumericUpDown numStuck, numMatch;
    Label lblPadStatus;
    System.Windows.Forms.Timer timerPad, timerLog, timerOcr, timerDrone, timerHud;

    // ================= win32 =================
    const int WM_HOTKEY = 0x0312;
    const int HK_REJOIN = 0x5A01, HK_AUTO = 0x5A02, HK_NIGHT = 0x5A03, HK_STOP = 0x5A04;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLT, bRT; public short lx, ly, rx, ry; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint pkt; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int XInput14(int i, ref XINPUT_STATE s);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int XInput910(int i, ref XINPUT_STATE s);

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int val);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    // Hide a window from screen capture (CopyFromScreen / Grab) while it stays visible on the
    // monitor. Lets our overlays cover the screen without blinding the OCR.
    static void ExcludeFromCapture(IntPtr h)
    {
        try { SetWindowDisplayAffinity(h, 0x00000011); } catch { }   // WDA_EXCLUDEFROMCAPTURE
    }

    // ---- night vision ----
    // There is no way for an overlay window to recolour the game pixels behind it, so a true
    // invert is done with the Windows Magnification API: a colour matrix applied to the whole
    // display. It also shows up in screen capture, so OBS records the inverted feed.
    [StructLayout(LayoutKind.Sequential)]
    struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] transform;
    }
    [DllImport("Magnification.dll")] static extern bool MagInitialize();
    [DllImport("Magnification.dll")] static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT effect);

    void ApplyNightVision(bool on)
    {
        try
        {
            if (!MagInitialize()) { Log("night vision: the Magnification API is unavailable"); return; }
            MAGCOLOREFFECT e = new MAGCOLOREFFECT();
            if (on)
                // The Magnification colour matrix is applied as a ROW vector (v * M), so the
                // translation lives in the LAST ROW - exactly like Direct2D/WPF ColorMatrix.
                // Putting the "+1" in the last column instead makes every channel (1 - v) come
                // out negative, clamps to 0, and blacks the whole screen out. This is that bug.
                e.transform = new float[] {
                    -1, 0, 0, 0, 0,
                     0,-1, 0, 0, 0,
                     0, 0,-1, 0, 0,
                     0, 0, 0, 1, 0,
                     1, 1, 1, 0, 1 };
            else
                e.transform = new float[] {
                     1, 0, 0, 0, 0,
                     0, 1, 0, 0, 0,
                     0, 0, 1, 0, 0,
                     0, 0, 0, 1, 0,
                     0, 0, 0, 0, 1 };
            bool ok = MagSetFullscreenColorEffect(ref e);
            Log("night vision " + (on ? "ON" : "OFF") + (ok ? "" : " - the call was refused"));
        }
        catch (Exception ex) { Log("night vision failed: " + ex.Message); }
    }

    // Hotkey + button entry point: flips the flag and drives the checkbox so both stay in sync.
    void ToggleNightVision()
    {
        _nightVision = !_nightVision;
        if (chkNight != null) chkNight.Checked = _nightVision;   // fires the handler, applies it
        else ApplyNightVision(_nightVision);
    }

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const int SW_RESTORE = 9;

    // The panel is a HUD that sits over the game. If Windows ever treats it as the active
    // window, the game drops to the background ("tabbing out") and injected input goes to
    // the wrong place. WS_EX_NOACTIVATE makes the panel a window that can never become
    // active, so clicking its buttons cannot pull focus away from the game. It is our own
    // window only - nothing is done to Roblox.
    //
    // WS_EX_TOOLWINDOW is deliberately NOT used: it would hide the app from the taskbar
    // and alt-tab, which makes it look like the app is not running.
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;

    // Injected clicks go to whatever window is in the FOREGROUND. This panel is
    // always-on-top, so if it holds focus every click lands on the panel and the game
    // never sees it. Steer focus to the Roblox window before clicking.
    static IntPtr _robloxWnd = IntPtr.Zero;
    static DateTime _robloxScan = DateTime.MinValue;

    static IntPtr RobloxWindow()
    {
        if (_robloxWnd != IntPtr.Zero && IsWindow(_robloxWnd)) return _robloxWnd;
        _robloxWnd = IntPtr.Zero;
        if ((DateTime.Now - _robloxScan).TotalMilliseconds < 2000) return IntPtr.Zero;
        _robloxScan = DateTime.Now;
        try
        {
            foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                if (p.MainWindowHandle != IntPtr.Zero) { _robloxWnd = p.MainWindowHandle; break; }
        }
        catch { }
        return _robloxWnd;
    }

    static void FocusRoblox()
    {
        IntPtr rw = RobloxWindow();
        if (rw == IntPtr.Zero) return;
        if (GetForegroundWindow() == rw) return;

        // Windows refuses SetForegroundWindow from a background process. Tapping ALT first
        // releases the foreground lock, and attaching to the current foreground thread earns
        // the right too. Retry a few times: this is the difference between a click that lands
        // in the game and one that lands in whatever terminal is in front of it.
        for (int i = 0; i < 8 && GetForegroundWindow() != rw; i++)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);          // ALT down
            keybd_event(0x12, 0, 2, UIntPtr.Zero);          // ALT up
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint them = GetWindowThreadProcessId(fg, out pid);
            uint us = GetCurrentThreadId();
            try
            {
                if (them != 0 && them != us) AttachThreadInput(us, them, true);
                ShowWindow(rw, SW_RESTORE);
                BringWindowToTop(rw);
                SetForegroundWindow(rw);
            }
            catch { }
            finally
            {
                try { if (them != 0 && them != us) AttachThreadInput(us, them, false); } catch { }
            }
            Thread.Sleep(120);
        }
        Thread.Sleep(80);
    }

    const uint IN_MOUSE = 0, IN_KEY = 1;
    const uint MV_MOVE = 0x0001, MV_ABS = 0x8000, MV_LDOWN = 0x0002, MV_LUP = 0x0004;
    const uint MV_WHEEL = 0x0800;
    const uint KEYUP = 0x0002;

    static readonly Dictionary<string, ushort> PAD = new Dictionary<string, ushort>
    {
        { "off", 0 },
        { "A", 0x1000 }, { "B", 0x2000 }, { "X", 0x4000 }, { "Y", 0x8000 },
        { "LB", 0x0100 }, { "RB", 0x0200 },
        { "START", 0x0010 }, { "BACK", 0x0020 },
        { "L3", 0x0040 }, { "R3", 0x0080 },
        { "D-Up", 0x0001 }, { "D-Down", 0x0002 }, { "D-Left", 0x0004 }, { "D-Right", 0x0008 }
    };

    // ---- language ----
    string _lang = "English";
    ComboBox cmbLang;
    static readonly Dictionary<Control, string> _enText = new Dictionary<Control, string>();

    // English key -> translation. Anything not listed stays in English, so a language can be
    // filled in gradually without breaking the layout.
    static readonly Dictionary<string, Dictionary<string, string>> LANG =
        new Dictionary<string, Dictionary<string, string>>
    {
        { "English",  new Dictionary<string, string>() },
        { "EspaÃ±ol",  new Dictionary<string, string> {
            {"REJOIN NOW","RECONECTAR"},{"Refresh","Actualizar"},{"AUTO RUN","AUTO"},{"Open log","Abrir registro"},
            {"Keybind:","Tecla:"},{"Set key...","Fijar tecla..."},{"controller:","mando:"},{"land now:","aterrizar:"},
            {"reconnect:","reconectar:"},{"stuck","atascado"},{"sec -> reconnect","s -> reconectar"},
            {"Team:","Equipo:"},{"Drone:","Dron:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO tras reconectar"},
            {"RF watch (HOME)","RF (CASA)"},{"Black screen while reconnecting","Pantalla negra al reconectar"},
            {"s delay","s retraso"},{"Show LAND NOW, hold","Mostrar LAND NOW, esperar"},{"s before reconnect","s antes de reconectar"} } },
        { "Deutsch",  new Dictionary<string, string> {
            {"REJOIN NOW","NEU VERBINDEN"},{"Refresh","Aktualisieren"},{"AUTO RUN","AUTO"},{"Open log","Log Ã¶ffnen"},
            {"Keybind:","Taste:"},{"Set key...","Taste setzen..."},{"controller:","Controller:"},{"land now:","landen:"},
            {"reconnect:","verbinden:"},{"stuck","hÃ¤ngt"},{"sec -> reconnect","s -> neu verbinden"},
            {"Team:","Team:"},{"Drone:","Drohne:"},{"Bomb:","Bombe:"},{"AUTO after rejoin","AUTO nach Neuverb."},
            {"RF watch (HOME)","RF (BASIS)"},{"Black screen while reconnecting","Schwarzer Bildschirm"},
            {"s delay","s VerzÃ¶gerung"},{"Show LAND NOW, hold","LAND NOW zeigen, warten"},{"s before reconnect","s vor Neuverb."} } },
        { "FranÃ§ais", new Dictionary<string, string> {
            {"REJOIN NOW","RECONNECTER"},{"Refresh","Actualiser"},{"AUTO RUN","AUTO"},{"Open log","Ouvrir log"},
            {"Keybind:","Touche:"},{"Set key...","DÃ©finir touche..."},{"controller:","manette:"},{"land now:","atterrir:"},
            {"reconnect:","reconnecter:"},{"stuck","bloquÃ©"},{"sec -> reconnect","s -> reconnecter"},
            {"Team:","Ã‰quipe:"},{"Drone:","Drone:"},{"Bomb:","Bombe:"},{"AUTO after rejoin","AUTO aprÃ¨s reconnexion"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Ã‰cran noir en reconnexion"},
            {"s delay","s dÃ©lai"},{"Show LAND NOW, hold","Afficher LAND NOW, attendre"},{"s before reconnect","s avant reconnexion"} } },
        { "PortuguÃªs", new Dictionary<string, string> {
            {"REJOIN NOW","RECONECTAR"},{"Refresh","Atualizar"},{"AUTO RUN","AUTO"},{"Open log","Abrir log"},
            {"Keybind:","Tecla:"},{"Set key...","Definir tecla..."},{"controller:","controle:"},{"land now:","aterrissar:"},
            {"reconnect:","reconectar:"},{"stuck","travado"},{"sec -> reconnect","s -> reconectar"},
            {"Team:","Equipe:"},{"Drone:","Drone:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO apÃ³s reconectar"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Tela preta ao reconectar"},
            {"s delay","s atraso"},{"Show LAND NOW, hold","Mostrar LAND NOW, esperar"},{"s before reconnect","s antes de reconectar"} } },
        { "Ð ÑƒÑÑÐºÐ¸Ð¹",  new Dictionary<string, string> {
            {"REJOIN NOW","ÐŸÐ•Ð Ð•ÐŸÐžÐ”ÐšÐ›Ð®Ð§Ð˜Ð¢Ð¬Ð¡Ð¯"},{"Refresh","ÐžÐ±Ð½Ð¾Ð²Ð¸Ñ‚ÑŒ"},{"AUTO RUN","ÐÐ’Ð¢Ðž"},{"Open log","ÐžÑ‚ÐºÑ€Ñ‹Ñ‚ÑŒ Ð»Ð¾Ð³"},
            {"Keybind:","ÐšÐ»Ð°Ð²Ð¸ÑˆÐ°:"},{"Set key...","Ð—Ð°Ð´Ð°Ñ‚ÑŒ ÐºÐ»Ð°Ð²Ð¸ÑˆÑƒ..."},{"controller:","Ð³ÐµÐ¹Ð¼Ð¿Ð°Ð´:"},{"land now:","Ð¿Ð¾ÑÐ°Ð´ÐºÐ°:"},
            {"reconnect:","Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ:"},{"stuck","Ð·Ð°Ð²Ð¸Ñ"},{"sec -> reconnect","Ñ -> Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ðµ"},
            {"Team:","ÐšÐ¾Ð¼Ð°Ð½Ð´Ð°:"},{"Drone:","Ð”Ñ€Ð¾Ð½:"},{"Bomb:","Ð‘Ð¾Ð¼Ð±Ð°:"},{"AUTO after rejoin","ÐÐ’Ð¢Ðž Ð¿Ð¾ÑÐ»Ðµ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ñ"},
            {"RF watch (HOME)","RF (Ð‘ÐÐ—Ð)"},{"Black screen while reconnecting","Ð§Ñ‘Ñ€Ð½Ñ‹Ð¹ ÑÐºÑ€Ð°Ð½ Ð¿Ñ€Ð¸ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ð¸"},
            {"s delay","Ñ Ð·Ð°Ð´ÐµÑ€Ð¶ÐºÐ°"},{"Show LAND NOW, hold","ÐŸÐ¾ÐºÐ°Ð·Ð°Ñ‚ÑŒ LAND NOW, Ð¿Ð°ÑƒÐ·Ð°"},{"s before reconnect","Ñ Ð´Ð¾ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ñ"} } },
        { "Italiano", new Dictionary<string, string> {
            {"REJOIN NOW","RICONNETTI"},{"Refresh","Aggiorna"},{"AUTO RUN","AUTO"},{"Open log","Apri log"},
            {"Keybind:","Tasto:"},{"Set key...","Imposta tasto..."},{"controller:","controller:"},{"land now:","atterra:"},
            {"reconnect:","riconnetti:"},{"stuck","bloccato"},{"sec -> reconnect","s -> riconnetti"},
            {"Team:","Squadra:"},{"Drone:","Drone:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO dopo riconnessione"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Schermo nero in riconnessione"},
            {"s delay","s ritardo"},{"Show LAND NOW, hold","Mostra LAND NOW, attendi"},{"s before reconnect","s prima di riconnettere"} } }
    };

    // language picker, pinned to the top-right of the panel
    void AddLangCombo()
    {
        cmbLang = new ComboBox();
        cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbLang.SetBounds(ClientSize.Width - 118, 8, 108, 24);
        foreach (string k in LANG.Keys) cmbLang.Items.Add(k);
        cmbLang.SelectedItem = LANG.ContainsKey(_lang) ? _lang : "English";
        cmbLang.SelectedIndexChanged += delegate
        {
            if (cmbLang.SelectedItem != null) { _lang = cmbLang.SelectedItem.ToString(); SaveCfg(); ApplyLanguage(); }
        };
        Controls.Add(cmbLang);
        cmbLang.BringToFront();
    }

    void CaptureEnglish(Control c)
    {
        if (!string.IsNullOrEmpty(c.Text)) _enText[c] = c.Text;
        foreach (Control ch in c.Controls) CaptureEnglish(ch);
    }

    void ApplyLanguage()
    {
        Dictionary<string, string> d;
        LANG.TryGetValue(_lang, out d);
        foreach (KeyValuePair<Control, string> kv in _enText)
        {
            string t = kv.Value, tr;
            if (d != null && d.TryGetValue(t, out tr)) t = tr;
            try { kv.Key.Text = t; } catch { }
        }
    }

    // ================= ctor =================
    public RobloxAuto()
    {
        _appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        _cfgPath = Path.Combine(_appDir, "settings.ini");

        Text = "Roblox Auto - Warfare";
        ClientSize = new Size(500, 800);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(22, 24, 28);
        ForeColor = Color.Gainsboro;
        KeyPreview = true;   // the form sees the key first - needed to capture a keybind

        LoadCfg();
        BuildUi();
        AddLangCombo();
        CaptureEnglish(this);
        ApplyLanguage();
        FindServer();
        TopMost = _topMost;

        timerPad = new System.Windows.Forms.Timer(); timerPad.Interval = 60; timerPad.Tick += PadTick; timerPad.Start();
        timerLog = new System.Windows.Forms.Timer(); timerLog.Interval = 2000; timerLog.Tick += LogTick; timerLog.Start();
        timerOcr = new System.Windows.Forms.Timer(); timerOcr.Interval = 4000; timerOcr.Tick += OcrTick; timerOcr.Start();
        timerDrone = new System.Windows.Forms.Timer(); timerDrone.Interval = 2000; timerDrone.Tick += DroneTick; timerDrone.Start();
        timerHud = new System.Windows.Forms.Timer(); timerHud.Interval = 20; timerHud.Tick += HudTick; timerHud.Start();
    }

    // Keep the panel out of the activation race entirely. ShowWithoutActivation stops the
    // panel taking focus when it opens; the extended styles keep it from ever becoming the
    // active window or showing up in alt-tab. Turned off with the "never steal focus" box
    // so the settings fields can still be typed into.
    protected override bool ShowWithoutActivation { get { return _noActivate; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (_noActivate) cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    // Start listening for a keybind. The panel is WS_EX_NOACTIVATE so the game keeps focus and
    // never gets tabbed out of - but that means Windows never delivers keyboard input to us
    // either, so the "press..." button would wait forever. We drop the flag only for as long as
    // it takes to read the key, then restore it.
    void BeginCaptureKey(bool night)
    {
        _capturingKey = !night;
        _capturingNight = night;
        Button b = night ? btnSetNight : btnSetKey;
        if (b != null) b.Text = "press...";
        try
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
            Activate();
            Focus();
        }
        catch { }
        Log("press the key for " + (night ? "NIGHT VISION" : "REJOIN") + "  (Esc cancels)");
    }

    void EndCaptureKey()
    {
        _capturingKey = false;
        _capturingNight = false;
        if (btnSetKey != null) btnSetKey.Text = "Set key...";
        if (btnSetNight != null) btnSetNight.Text = "Set key...";
        ApplyNoActivate();
    }

    void ApplyNoActivate()
    {
        if (Handle == IntPtr.Zero) return;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        ex &= ~WS_EX_TOOLWINDOW;                    // always keep a taskbar button
        if (_noActivate) ex |= WS_EX_NOACTIVATE;
        else ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
    }

    // ================= ui =================
    void BuildUi()
    {
        int x = 14, y = 12, w = ClientSize.Width - 28;

        var title = new Label();
        title.Text = "ROBLOX AUTO";
        title.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(120, 200, 255);
        title.SetBounds(x, y, w, 26);
        Controls.Add(title);
        y += 34;

        // ---- HUD tuning dials, at the very top so they are easy to reach and see ----
        var lblTune = new Label();
        lblTune.Text = "HUD TUNING";
        lblTune.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        lblTune.ForeColor = Color.FromArgb(120, 200, 255);
        lblTune.SetBounds(x, y, w, 18);
        Controls.Add(lblTune);
        y += 22;

        numPitch = MkTune(x, y, "pitch px/s", (decimal)_hudPitchRate, 80m, 4000m, 40m, 0);
        numRoll = MkTune(x + 168, y, "roll deg/s", (decimal)_hudRollRate, 20m, 900m, 10m, 0);
        numPitch.ValueChanged += delegate { _hudPitchRate = (float)numPitch.Value; SaveCfg(); };
        numRoll.ValueChanged += delegate { _hudRollRate = (float)numRoll.Value; SaveCfg(); };
        y += 28;

        numLock = MkTune(x, y, "lock s", (decimal)_hudLockTau, 0.05m, 2.0m, 0.05m, 2);
        numFly = MkTune(x + 168, y, "drift s", (decimal)_hudFlyTau, 0.10m, 3.0m, 0.1m, 2);
        numLock.ValueChanged += delegate { _hudLockTau = (float)numLock.Value; SaveCfg(); };
        numFly.ValueChanged += delegate { _hudFlyTau = (float)numFly.Value; SaveCfg(); };
        y += 28;

        numBias = MkTune(x, y, "bias px", (decimal)_hudBias, -120m, 120m, 2m, 0);
        numBias.ValueChanged += delegate { _hudBias = (float)numBias.Value; SaveCfg(); };
        y += 32;

        var l1 = new Label();
        l1.Text = "Server (from the Roblox logs):";
        l1.SetBounds(x, y, w, 18);
        Controls.Add(l1);
        y += 20;

        txtServer = new TextBox();
        txtServer.ReadOnly = true;
        txtServer.BackColor = Color.FromArgb(14, 15, 18);
        txtServer.ForeColor = Color.LightGreen;
        txtServer.BorderStyle = BorderStyle.FixedSingle;
        txtServer.SetBounds(x, y, w - 92, 24);
        Controls.Add(txtServer);

        btnRefresh = new Button();
        btnRefresh.Text = "Refresh";
        btnRefresh.SetBounds(x + w - 86, y - 1, 86, 26);
        btnRefresh.Click += delegate { FindServer(); Log("server: place " + _placeId + " server " + _serverId); };
        Controls.Add(btnRefresh);
        y += 34;

        btnRejoin = new Button();
        btnRejoin.Text = "REJOIN NOW";
        btnRejoin.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        btnRejoin.BackColor = Color.FromArgb(32, 140, 72);
        btnRejoin.ForeColor = Color.White;
        btnRejoin.FlatStyle = FlatStyle.Flat;
        btnRejoin.SetBounds(x, y, w - 132, 48);
        btnRejoin.Click += delegate { Rejoin("button"); };
        Controls.Add(btnRejoin);

        // straight reconnect: relaunch into the same server immediately, no LAND NOW / no hold -
        // this is the "I am done with the MAVIC, take me back" button
        btnReconnect = new Button();
        btnReconnect.Text = "RECONNECT";
        btnReconnect.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        btnReconnect.BackColor = Color.FromArgb(40, 92, 168);
        btnReconnect.ForeColor = Color.White;
        btnReconnect.FlatStyle = FlatStyle.Flat;
        btnReconnect.SetBounds(x + w - 126, y, 126, 48);
        btnReconnect.Click += delegate { Rejoin("panel RECONNECT", false); };
        Controls.Add(btnReconnect);
        y += 54;

        // run the whole AUTO macro automatically a while after a rejoin - useful when the
        // reconnect drops you back at the menu and you always want the same sequence next
        // after Deploy As Drone, poll the screen for the HOME distance and show a fake RF feed
        chkRfWatch = new CheckBox();
        chkRfWatch.Text = "RF watch (HOME)";
        chkRfWatch.SetBounds(x, y, 160, 22);
        chkRfWatch.Checked = _watchHome;
        chkRfWatch.CheckedChanged += delegate
        {
            _watchHome = chkRfWatch.Checked; SaveCfg();
            if (!_watchHome) StopRfWatch();
        };
        Controls.Add(chkRfWatch);
        y += 26;

        chkBlack = new CheckBox();
        chkBlack.Text = "Black screen while reconnecting";
        chkBlack.SetBounds(x, y, 240, 22);
        chkBlack.Checked = _blackScreen;
        chkBlack.CheckedChanged += delegate
        {
            _blackScreen = chkBlack.Checked; SaveCfg();
            if (!_blackScreen) HideBlack();
        };
        Controls.Add(chkBlack);
        y += 26;

        chkHud = new CheckBox();
        chkHud.Text = "FPV HUD overlay";
        chkHud.SetBounds(x, y, 150, 22);
        chkHud.Checked = _hudOn;
        chkHud.CheckedChanged += delegate
        {
            _hudOn = chkHud.Checked; SaveCfg();
            if (!_hudOn) StopHud();
        };
        Controls.Add(chkHud);

        var lblHudStyle = new Label();
        lblHudStyle.Text = "OSD style follows the drone";
        lblHudStyle.ForeColor = Color.Silver;
        lblHudStyle.SetBounds(x + 156, y + 3, 200, 20);
        Controls.Add(lblHudStyle);
        y += 26;

        chkHudAuto = new CheckBox();
        chkHudAuto.Text = "auto-detect drone view (only if you pick a drone by hand)";
        chkHudAuto.SetBounds(x, y, 380, 22);
        chkHudAuto.Checked = _hudAutoDetect;
        chkHudAuto.CheckedChanged += delegate { _hudAutoDetect = chkHudAuto.Checked; SaveCfg(); };
        Controls.Add(chkHudAuto);
        y += 24;

        chkNight = new CheckBox();
        chkNight.Text = "Night vision (invert display)";
        chkNight.SetBounds(x, y, 260, 22);
        chkNight.Checked = _nightVision;
        chkNight.CheckedChanged += delegate
        {
            _nightVision = chkNight.Checked; SaveCfg();
            ApplyNightVision(_nightVision);
        };
        Controls.Add(chkNight);
        y += 26;

        chkAutoAfterRejoin = new CheckBox();
        chkAutoAfterRejoin.Text = "AUTO after rejoin";
        chkAutoAfterRejoin.SetBounds(x, y, 140, 22);
        chkAutoAfterRejoin.Checked = _autoAfterRejoin;
        chkAutoAfterRejoin.CheckedChanged += delegate { _autoAfterRejoin = chkAutoAfterRejoin.Checked; SaveCfg(); };
        Controls.Add(chkAutoAfterRejoin);

        numAutoDelay = new NumericUpDown();
        numAutoDelay.Minimum = 1; numAutoDelay.Maximum = 600;
        numAutoDelay.Value = Math.Max(numAutoDelay.Minimum, Math.Min(numAutoDelay.Maximum, _autoAfterRejoinMs / 1000));
        numAutoDelay.SetBounds(x + 146, y, 58, 22);
        numAutoDelay.ValueChanged += delegate { _autoAfterRejoinMs = (int)numAutoDelay.Value * 1000; SaveCfg(); };
        Controls.Add(numAutoDelay);

        var lblDelay = new Label();
        lblDelay.Text = "s delay";
        lblDelay.SetBounds(x + 208, y + 3, 70, 20);
        Controls.Add(lblDelay);
        y += 30;

        // press reconnect -> LAND NOW pops -> hold this long -> only then relaunch. Lets you
        // record the alert before the reconnect actually starts.
        var lblPre = new Label();
        lblPre.Text = "Show LAND NOW, hold";
        lblPre.SetBounds(x, y + 3, 150, 20);
        Controls.Add(lblPre);

        numPreDelay = new NumericUpDown();
        numPreDelay.Minimum = 0; numPreDelay.Maximum = 120;
        numPreDelay.Value = Math.Max(numPreDelay.Minimum, Math.Min(numPreDelay.Maximum, _preRejoinMs / 1000));
        numPreDelay.SetBounds(x + 154, y, 58, 22);
        numPreDelay.ValueChanged += delegate { _preRejoinMs = (int)numPreDelay.Value * 1000; SaveCfg(); };
        Controls.Add(numPreDelay);

        var lblPreS = new Label();
        lblPreS.Text = "s before reconnect";
        lblPreS.SetBounds(x + 216, y + 3, 140, 20);
        Controls.Add(lblPreS);
        y += 30;

        var lblKey = new Label();
        lblKey.Text = "Keybind:";
        lblKey.SetBounds(x, y + 4, 60, 20);
        Controls.Add(lblKey);

        lblKeyVal = new Label();
        lblKeyVal.SetBounds(x + 62, y + 3, 100, 20);
        lblKeyVal.ForeColor = Color.Khaki;
        lblKeyVal.Font = new Font("Consolas", 10F, FontStyle.Bold);
        Controls.Add(lblKeyVal);

        btnSetKey = new Button();
        btnSetKey.Text = "Set key...";
        btnSetKey.SetBounds(x + 168, y, 92, 26);
        btnSetKey.Click += delegate { BeginCaptureKey(false); };
        Controls.Add(btnSetKey);

        var lblKeyHint = new Label();
        lblKeyHint.Text = "F5 is AUTO RUN";
        lblKeyHint.SetBounds(x + 268, y + 4, 140, 20);
        lblKeyHint.ForeColor = Color.Silver;
        Controls.Add(lblKeyHint);
        y += 34;

        var lblNightKey = new Label();
        lblNightKey.Text = "Night vision:";
        lblNightKey.SetBounds(x, y + 4, 86, 20);
        Controls.Add(lblNightKey);

        lblNightVal = new Label();
        lblNightVal.SetBounds(x + 88, y + 3, 76, 20);
        lblNightVal.ForeColor = Color.Khaki;
        lblNightVal.Font = new Font("Consolas", 10F, FontStyle.Bold);
        Controls.Add(lblNightVal);

        btnSetNight = new Button();
        btnSetNight.Text = "Set key...";
        btnSetNight.SetBounds(x + 168, y, 92, 26);
        btnSetNight.Click += delegate { BeginCaptureKey(true); };
        Controls.Add(btnSetNight);

        var lblNightHint = new Label();
        lblNightHint.Text = "toggles the invert";
        lblNightHint.SetBounds(x + 268, y + 4, 160, 20);
        lblNightHint.ForeColor = Color.Silver;
        Controls.Add(lblNightHint);
        y += 34;

        // ---- reconnect options ----
        var gRe = new GroupBox();
        gRe.Text = "Reconnect";
        gRe.ForeColor = Color.Gainsboro;
        gRe.SetBounds(x, y, w, 178);
        Controls.Add(gRe);

        chkAutoRecon = new CheckBox();
        chkAutoRecon.Text = "reconnect automatically when disconnected";
        chkAutoRecon.Checked = _autoRecon;
        chkAutoRecon.CheckedChanged += delegate { _autoRecon = chkAutoRecon.Checked; SaveCfg(); };
        chkAutoRecon.SetBounds(12, 24, 300, 22);
        gRe.Controls.Add(chkAutoRecon);

        chkOcrWatch = new CheckBox();
        chkOcrWatch.Text = "watch the loading screen by OCR";
        chkOcrWatch.Checked = _ocrWatch;
        chkOcrWatch.CheckedChanged += delegate { _ocrWatch = chkOcrWatch.Checked; SaveCfg(); };
        chkOcrWatch.SetBounds(12, 50, 240, 22);
        gRe.Controls.Add(chkOcrWatch);

        txtWatch = new TextBox();
        txtWatch.Text = _watchWords;
        txtWatch.SetBounds(256, 49, 130, 24);
        txtWatch.BackColor = Color.FromArgb(14, 15, 18);
        txtWatch.ForeColor = Color.Gainsboro;
        txtWatch.TextChanged += delegate { _watchWords = txtWatch.Text; SaveCfg(); };
        gRe.Controls.Add(txtWatch);

        var lStuck = new Label();
        lStuck.Text = "stuck";
        lStuck.SetBounds(12, 82, 40, 20);
        gRe.Controls.Add(lStuck);

        numStuck = new NumericUpDown();
        numStuck.Minimum = 10; numStuck.Maximum = 600; numStuck.Increment = 5; numStuck.Value = _watchStuckSec;
        numStuck.SetBounds(54, 80, 56, 24);
        numStuck.ValueChanged += delegate { _watchStuckSec = (int)numStuck.Value; SaveCfg(); };
        gRe.Controls.Add(numStuck);

        var lSec = new Label();
        lSec.Text = "sec -> reconnect";
        lSec.SetBounds(116, 82, 130, 20);
        lSec.ForeColor = Color.Silver;
        gRe.Controls.Add(lSec);

        chkPadRejoin = new CheckBox();
        chkPadRejoin.Text = "controller:";
        chkPadRejoin.Checked = _padRejoinOn;
        chkPadRejoin.CheckedChanged += delegate { _padRejoinOn = chkPadRejoin.Checked; SaveCfg(); };
        chkPadRejoin.SetBounds(256, 80, 84, 22);
        gRe.Controls.Add(chkPadRejoin);

        cmbPadRejoin = new ComboBox();
        cmbPadRejoin.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadRejoin.SetBounds(342, 79, 110, 24);
        foreach (string k in PAD.Keys) cmbPadRejoin.Items.Add(k);
        cmbPadRejoin.SelectedItem = PAD.ContainsKey(_padRejoin) ? _padRejoin : "BACK";
        cmbPadRejoin.SelectedIndexChanged += delegate { if (cmbPadRejoin.SelectedItem != null) { _padRejoin = cmbPadRejoin.SelectedItem.ToString(); SaveCfg(); } };
        gRe.Controls.Add(cmbPadRejoin);

        chkPadLand = new CheckBox();
        chkPadLand.Text = "land now:";
        chkPadLand.Checked = _padLandOn;
        chkPadLand.CheckedChanged += delegate { _padLandOn = chkPadLand.Checked; SaveCfg(); };
        chkPadLand.SetBounds(256, 108, 84, 22);
        gRe.Controls.Add(chkPadLand);

        cmbPadLand = new ComboBox();
        cmbPadLand.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadLand.SetBounds(342, 107, 110, 24);
        foreach (string k in PAD.Keys) cmbPadLand.Items.Add(k);
        cmbPadLand.SelectedItem = PAD.ContainsKey(_padLand) ? _padLand : "B";
        cmbPadLand.SelectedIndexChanged += delegate { if (cmbPadLand.SelectedItem != null) { _padLand = cmbPadLand.SelectedItem.ToString(); SaveCfg(); } };
        gRe.Controls.Add(cmbPadLand);

        chkPadReconnect = new CheckBox();
        chkPadReconnect.Text = "reconnect:";
        chkPadReconnect.Checked = _padReconnectOn;
        chkPadReconnect.CheckedChanged += delegate { _padReconnectOn = chkPadReconnect.Checked; SaveCfg(); };
        chkPadReconnect.SetBounds(256, 136, 84, 22);
        gRe.Controls.Add(chkPadReconnect);

        cmbPadReconnect = new ComboBox();
        cmbPadReconnect.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadReconnect.SetBounds(342, 135, 110, 24);
        foreach (string k in PAD.Keys) cmbPadReconnect.Items.Add(k);
        cmbPadReconnect.SelectedItem = PAD.ContainsKey(_padReconnect) ? _padReconnect : "START";
        cmbPadReconnect.SelectedIndexChanged += delegate { if (cmbPadReconnect.SelectedItem != null) { _padReconnect = cmbPadReconnect.SelectedItem.ToString(); SaveCfg(); } };
        gRe.Controls.Add(cmbPadReconnect);
        y += 186;

        // ---- auto sequence ----
        var gAu = new GroupBox();
        gAu.Text = "Auto sequence   (load > team > drone > deploy > base > bomb > as drone)";
        gAu.ForeColor = Color.Gainsboro;
        gAu.SetBounds(x, y, w, 150);
        Controls.Add(gAu);

        var lT = new Label();
        lT.Text = "Team:";
        lT.SetBounds(12, 26, 38, 20);
        gAu.Controls.Add(lT);

        cmbTeam = new ComboBox();
        cmbTeam.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbTeam.SetBounds(52, 24, 62, 24);
        cmbTeam.Items.Add("Blue"); cmbTeam.Items.Add("Red");
        cmbTeam.SelectedItem = (_team == "Red") ? "Red" : "Blue";
        cmbTeam.SelectedIndexChanged += delegate { if (cmbTeam.SelectedItem != null) { _team = cmbTeam.SelectedItem.ToString(); SaveCfg(); } };
        gAu.Controls.Add(cmbTeam);

        var lD = new Label();
        lD.Text = "Drone:";
        lD.SetBounds(122, 26, 44, 20);
        gAu.Controls.Add(lD);

        cmbDrone = new ComboBox();
        cmbDrone.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDrone.SetBounds(168, 24, 84, 24);
        cmbDrone.Items.Add("(default)");
        cmbDrone.Items.Add("MAVIC");
        cmbDrone.Items.Add("FPV");
        cmbDrone.SelectedItem = cmbDrone.Items.Contains(_drone) ? _drone : "(default)";
        cmbDrone.SelectedIndexChanged += delegate
        {
            if (cmbDrone.SelectedItem != null)
            {
                _drone = cmbDrone.SelectedItem.ToString();
                _hudStyleUav = _drone == "MAVIC";   // MAVIC gets the UAV-style OSD, FPV the FPV one
                if (_hudForm != null) _hudForm.Uav = _hudStyleUav;
                FillBombs();          // MAVIC and FPV carry different warheads
                SaveCfg();
            }
        };
        gAu.Controls.Add(cmbDrone);

        var lB = new Label();
        lB.Text = "Bomb:";
        lB.SetBounds(258, 26, 42, 20);
        gAu.Controls.Add(lB);

        cmbBomb = new ComboBox();
        cmbBomb.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbBomb.SetBounds(302, 24, 124, 24);
        cmbBomb.SelectedIndexChanged += delegate
        {
            if (_filling) return;
            if (cmbBomb.SelectedItem != null) { _bomb = cmbBomb.SelectedItem.ToString(); SaveCfg(); }
        };
        gAu.Controls.Add(cmbBomb);
        FillBombs();                 // populate for whichever drone is selected

        chkAsDrone = new CheckBox();
        chkAsDrone.Text = "deploy as drone";
        chkAsDrone.Checked = _asDrone;
        chkAsDrone.CheckedChanged += delegate { _asDrone = chkAsDrone.Checked; SaveCfg(); };
        chkAsDrone.SetBounds(8, 58, 118, 22);
        gAu.Controls.Add(chkAsDrone);

        chkPadAuto = new CheckBox();
        chkPadAuto.Text = "AUTO:";
        chkPadAuto.Checked = _padAutoOn;
        chkPadAuto.CheckedChanged += delegate { _padAutoOn = chkPadAuto.Checked; SaveCfg(); };
        chkPadAuto.SetBounds(128, 58, 64, 22);
        gAu.Controls.Add(chkPadAuto);

        cmbPadAuto = new ComboBox();
        cmbPadAuto.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadAuto.SetBounds(194, 57, 74, 24);
        foreach (string k in PAD.Keys) cmbPadAuto.Items.Add(k);
        cmbPadAuto.SelectedItem = PAD.ContainsKey(_padAuto) ? _padAuto : "LB";
        cmbPadAuto.SelectedIndexChanged += delegate { if (cmbPadAuto.SelectedItem != null) { _padAuto = cmbPadAuto.SelectedItem.ToString(); SaveCfg(); } };
        gAu.Controls.Add(cmbPadAuto);

        chkPadStop = new CheckBox();
        chkPadStop.Text = "STOP:";
        chkPadStop.Checked = _padStopOn;
        chkPadStop.CheckedChanged += delegate { _padStopOn = chkPadStop.Checked; SaveCfg(); };
        chkPadStop.SetBounds(272, 58, 66, 22);
        gAu.Controls.Add(chkPadStop);

        cmbPadStop = new ComboBox();
        cmbPadStop.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadStop.SetBounds(340, 57, 74, 24);
        foreach (string k in PAD.Keys) cmbPadStop.Items.Add(k);
        cmbPadStop.SelectedItem = PAD.ContainsKey(_padStop) ? _padStop : "Y";
        cmbPadStop.SelectedIndexChanged += delegate { if (cmbPadStop.SelectedItem != null) { _padStop = cmbPadStop.SelectedItem.ToString(); SaveCfg(); } };
        gAu.Controls.Add(cmbPadStop);

        var lFlow = new Label();
        lFlow.Text = "Reads the screen to know where it is (team select / lobby / loadout / map / TEAM BASE).\nEach click is verified by the screen changing, and AUTO resumes from wherever the game actually is.";
        lFlow.SetBounds(12, 88, 424, 44);
        lFlow.ForeColor = Color.Silver;
        gAu.Controls.Add(lFlow);
        y += 158;

        // ---- per-step toggles ----
        var gSt = new GroupBox();
        gSt.Text = "Steps to run   (untick to skip)";
        gSt.ForeColor = Color.Gainsboro;
        gSt.SetBounds(x, y, w, 56);
        Controls.Add(gSt);

        chkStepTeam = MkChk(gSt, "team", 12, 22, _stepTeam);
        chkStepTeam.CheckedChanged += delegate { _stepTeam = chkStepTeam.Checked; SaveCfg(); };
        chkStepDrone = MkChk(gSt, "drone / loadout", 74, 22, _stepDrone);
        chkStepDrone.CheckedChanged += delegate { _stepDrone = chkStepDrone.Checked; SaveCfg(); };
        chkStepDeploy = MkChk(gSt, "deploy", 198, 22, _stepDeploy);
        chkStepDeploy.CheckedChanged += delegate { _stepDeploy = chkStepDeploy.Checked; SaveCfg(); };
        chkStepBase = MkChk(gSt, "base", 268, 22, _stepBase);
        chkStepBase.CheckedChanged += delegate { _stepBase = chkStepBase.Checked; SaveCfg(); };
        chkStepBomb = MkChk(gSt, "bomb", 326, 22, _stepBomb);
        chkStepBomb.CheckedChanged += delegate { _stepBomb = chkStepBomb.Checked; SaveCfg(); };

        // how close an OCR word has to be to count as a match
        var lMatch = new Label();
        lMatch.Text = "match %:";
        lMatch.SetBounds(370, 25, 58, 18);
        gSt.Controls.Add(lMatch);

        numMatch = new NumericUpDown();
        numMatch.Minimum = 50; numMatch.Maximum = 100; numMatch.Increment = 5; numMatch.Value = _matchPct;
        numMatch.SetBounds(428, 22, 46, 24);
        numMatch.ValueChanged += delegate { _matchPct = (int)numMatch.Value; SaveCfg(); };
        gSt.Controls.Add(numMatch);
        y += 64;

        btnAuto = new Button();
        btnAuto.Text = "AUTO RUN   (F5)      - press again to restart";
        btnAuto.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        btnAuto.BackColor = Color.FromArgb(38, 92, 168);
        btnAuto.ForeColor = Color.White;
        btnAuto.FlatStyle = FlatStyle.Flat;
        btnAuto.SetBounds(x, y, w - 96, 42);
        btnAuto.Click += delegate { AutoRun(); };
        Controls.Add(btnAuto);

        btnStop = new Button();
        btnStop.Text = "STOP";
        btnStop.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        btnStop.BackColor = Color.FromArgb(168, 48, 48);
        btnStop.ForeColor = Color.White;
        btnStop.FlatStyle = FlatStyle.Flat;
        btnStop.SetBounds(x + w - 90, y, 90, 42);
        btnStop.Click += delegate { StopAuto(); };
        Controls.Add(btnStop);
        y += 50;

        chkTop = new CheckBox();
        chkTop.Text = "always on top";
        chkTop.Checked = _topMost;
        chkTop.CheckedChanged += delegate { _topMost = chkTop.Checked; TopMost = _topMost; SaveCfg(); };
        chkTop.SetBounds(x, y, 130, 22);
        Controls.Add(chkTop);

        var lblTopHint = new Label();
        lblTopHint.Text = "turn this off if the game stutters (a topmost window forces windowed compositing)";
        lblTopHint.SetBounds(x + 136, y + 3, 320, 20);
        lblTopHint.ForeColor = Color.Silver;
        Controls.Add(lblTopHint);
        y += 26;

        chkNoAct = new CheckBox();
        chkNoAct.Text = "never steal focus (stops the game tabbing out)";
        chkNoAct.Checked = _noActivate;
        chkNoAct.CheckedChanged += delegate
        {
            _noActivate = chkNoAct.Checked;
            ApplyNoActivate();
            SaveCfg();
            Log(_noActivate ? "panel will not take focus from the game"
                            : "panel can take focus again (so the settings fields can be typed into)");
        };
        chkNoAct.SetBounds(x, y, 300, 22);
        Controls.Add(chkNoAct);
        y += 26;

        // Clicking through a Razer/keyboard macro is a real hardware click, which the game
        // accepts where an injected mouse click is sometimes ignored. Falls back to an
        // injected click when this is off.
        chkClickKey = new CheckBox();
        chkClickKey.Text = "click with macro key:";
        chkClickKey.Checked = _clickKeyOn;
        chkClickKey.CheckedChanged += delegate
        {
            _clickKeyOn = chkClickKey.Checked;
            SaveCfg();
            Log(_clickKeyOn ? "clicks will use the macro key " + _clickKeyText
                            : "clicks will use an injected mouse click");
        };
        chkClickKey.SetBounds(x, y, 150, 22);
        Controls.Add(chkClickKey);

        txtClickKey = new TextBox();
        txtClickKey.Text = _clickKeyText;
        txtClickKey.SetBounds(x + 154, y, 40, 22);
        txtClickKey.TextChanged += delegate
        {
            _clickKeyText = txtClickKey.Text.Trim();
            _clickVk = ParseVk(_clickKeyText);
            SaveCfg();
        };
        Controls.Add(txtClickKey);

        var lblClickHint = new Label();
        lblClickHint.Text = "(\"0\" = the Razer left-click macro, \"off\" = disabled)";
        lblClickHint.ForeColor = Color.Silver;
        lblClickHint.SetBounds(x + 200, y + 3, 280, 20);
        Controls.Add(lblClickHint);
        y += 26;

        lblPadStatus = new Label();
        lblPadStatus.SetBounds(x, y, w, 18);
        lblPadStatus.ForeColor = Color.Silver;
        Controls.Add(lblPadStatus);
        y += 22;

        // own row, taller, so the captions are not clipped
        btnHighPrio = new Button();
        btnHighPrio.Text = "roblox: HIGH priority";
        btnHighPrio.FlatStyle = FlatStyle.Flat;
        btnHighPrio.BackColor = Color.FromArgb(45, 48, 54);
        btnHighPrio.ForeColor = Color.Gainsboro;
        btnHighPrio.UseVisualStyleBackColor = false;
        btnHighPrio.SetBounds(x, y, 200, 26);
        btnHighPrio.Click += delegate { SetRobloxPriority(); };
        Controls.Add(btnHighPrio);

        btnOpenLog = new Button();
        btnOpenLog.Text = "open log";
        btnOpenLog.FlatStyle = FlatStyle.Flat;
        btnOpenLog.BackColor = Color.FromArgb(45, 48, 54);
        btnOpenLog.ForeColor = Color.Gainsboro;
        btnOpenLog.UseVisualStyleBackColor = false;
        btnOpenLog.SetBounds(x + 208, y, 120, 26);
        btnOpenLog.Click += delegate
        {
            try { Process.Start("notepad.exe", Path.Combine(_appDir, "auto.log")); }
            catch (Exception ex) { Log("could not open the log: " + ex.Message); }
        };
        Controls.Add(btnOpenLog);
        y += 32;

        var lblLogHead = new Label();
        lblLogHead.Text = "what it is doing";
        lblLogHead.ForeColor = Color.Gainsboro;
        lblLogHead.SetBounds(x, y, w, 16);
        Controls.Add(lblLogHead);
        y += 18;

        txtLog = new TextBox();
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(14, 15, 18);
        txtLog.ForeColor = Color.Silver;
        txtLog.Font = new Font("Consolas", 8.5F);
        txtLog.BorderStyle = BorderStyle.FixedSingle;
        txtLog.SetBounds(x, y, w, 132);
        Controls.Add(txtLog);
        y += 132 + 8;

        // grow the window to fit the content, but never taller than the screen - if it is,
        // clamp and let the form scroll, otherwise the log and buttons fall off the bottom
        int wantH = y;
        int maxH = Screen.PrimaryScreen.WorkingArea.Height - 40;
        if (wantH > maxH) { wantH = maxH; AutoScroll = true; }
        ClientSize = new Size(ClientSize.Width, wantH);

        WireEditables(this);   // so every text box can be typed into despite WS_EX_NOACTIVATE
    }

    // A WS_EX_NOACTIVATE window never becomes the active window, so Windows never delivers keys to
    // its children - which is why the settings text boxes could not be edited. Drop the flag and
    // activate only while a text box is focused, then restore it on leave.
    void WireEditables(Control root)
    {
        foreach (Control c in root.Controls)
        {
            TextBox tb = c as TextBox;
            if (tb != null && !tb.ReadOnly)
            {
                tb.Enter += delegate { BeginEdit(tb); };
                tb.MouseDown += delegate { BeginEdit(tb); };
                tb.Leave += delegate { EndEdit(); };
            }
            // NumericUpDown keeps its edit box as a child that is not always reachable, and its
            // spin arrows are not TextBoxes at all - wire the whole control so it can be typed in
            // and clicked while the panel is WS_EX_NOACTIVATE.
            NumericUpDown nud = c as NumericUpDown;
            if (nud != null)
            {
                nud.Enter += delegate { BeginEdit(nud); };
                nud.MouseDown += delegate { BeginEdit(nud); };
                nud.Leave += delegate { EndEdit(); };
            }
            if (c.Controls.Count > 0) WireEditables(c);
        }
    }

    bool _noActivateWas = false;
    void BeginEdit(Control c)
    {
        try
        {
            // Clear WS_EX_NOACTIVATE AND actually take the foreground. Activate() alone is refused
            // while the fullscreen game owns the foreground lock, so tap ALT first like FocusRoblox
            // does - otherwise the settings fields can never be typed into while the game is up.
            _noActivateWas = _noActivate;
            _noActivate = false;
            ApplyNoActivate();
            BringOurselvesToFront();
            if (c != null) c.Focus();
        }
        catch { }
    }

    void EndEdit()
    {
        _noActivate = _noActivateWas;
        ApplyNoActivate();
    }

    void BringOurselvesToFront()
    {
        for (int i = 0; i < 6 && GetForegroundWindow() != Handle; i++)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);   // ALT down
            keybd_event(0x12, 0, 2, UIntPtr.Zero);   // ALT up
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint them = GetWindowThreadProcessId(fg, out pid);
            uint us = GetCurrentThreadId();
            try
            {
                if (them != 0 && them != us) AttachThreadInput(us, them, true);
                SetForegroundWindow(Handle);
            }
            catch { }
            finally { try { if (them != 0 && them != us) AttachThreadInput(us, them, false); } catch { } }
            Thread.Sleep(50);
        }
    }

    static float ParseF(string s)
    {
        float f;
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out f);
        return f;
    }

    // caption + numeric spinner for a HUD tuning dial
    NumericUpDown MkTune(int cx, int cy, string caption, decimal val, decimal min, decimal max, decimal inc, int decimals)
    {
        var lbl = new Label();
        lbl.Text = caption;
        lbl.ForeColor = Color.Silver;
        lbl.SetBounds(cx, cy, 90, 20);
        Controls.Add(lbl);
        var nud = new NumericUpDown();
        nud.Minimum = min; nud.Maximum = max; nud.Increment = inc;
        nud.DecimalPlaces = decimals;
        nud.Value = val;
        nud.BackColor = Color.FromArgb(14, 15, 18);
        nud.ForeColor = Color.Gainsboro;
        nud.SetBounds(cx + 92, cy - 2, 70, 24);
        Controls.Add(nud);
        return nud;
    }

    CheckBox MkChk(Control parent, string text, int cx, int cy, bool val)
    {
        CheckBox c = new CheckBox();
        c.Text = text;
        c.Checked = val;
        c.AutoSize = true;
        c.SetBounds(cx, cy, 10, 22);
        parent.Controls.Add(c);
        return c;
    }

    // Log is called from the AUTO background thread as well as the UI thread, so any write
    // to the text box has to be marshalled - touching a control from another thread is not
    // legal even though WinForms often lets it slide.
    void Log(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
        try
        {
            if (txtLog.IsHandleCreated && txtLog.InvokeRequired)
                txtLog.BeginInvoke((MethodInvoker)delegate { AppendLog(line); });
            else
                AppendLog(line);
        }
        catch { }
        try
        {
            File.AppendAllText(Path.Combine(_appDir, "auto.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n");
        }
        catch { }
    }

    void AppendLog(string line)
    {
        try
        {
            // keep the box from growing forever during a long session
            if (txtLog.Lines.Length > 400)
            {
                string[] keep = new string[200];
                Array.Copy(txtLog.Lines, txtLog.Lines.Length - 200, keep, 0, 200);
                txtLog.Lines = keep;
            }
            txtLog.AppendText(line + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }
        catch { }
    }

    // ================= rejoin =================
    // Roblox keeps the log open for writing, so a plain File.ReadAllText (FileShare.Read)
    // fails with a sharing violation. Always open logs shared.
    static string ReadAllShared(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    void FindServer()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
            if (!Directory.Exists(dir)) { txtServer.Text = "(no Roblox logs found)"; return; }

            FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.log");
            Array.Sort(files, delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });

            int n = Math.Min(20, files.Length);
            for (int i = 0; i < n; i++)
            {
                string t = ReadAllShared(files[i].FullName);
                if (t == "") continue;

                // A single log can hold several joins. Roblox appends as it goes, so the
                // LAST "Joining game" line is the server we are on now - the FIRST one is a
                // stale server from earlier in the session, and rejoining it gives
                // "This experience has ended, or the server became unavailable".
                MatchCollection ms = Regex.Matches(t, @"Joining game '([0-9a-fA-F\-]{36})'\s+place\s+(\d+)");
                if (ms.Count == 0) continue;

                Match m = ms[ms.Count - 1];
                _serverId = m.Groups[1].Value; _placeId = m.Groups[2].Value;
                txtServer.Text = "place " + _placeId + "   server " + _serverId + "   (last join in " + files[i].Name + ")";
                return;
            }
            txtServer.Text = "(no recent server found)";
        }
        catch { }
    }

    readonly object _rejoinLock = new object();

    void Rejoin(string why) { Rejoin(why, true); }

    void Rejoin(string why, bool overlay)
    {
        // Guard against the controller/hotkey firing twice in one frame: claim the cooldown
        // slot BEFORE launching so a second call cannot start a parallel rejoin.
        lock (_rejoinLock)
        {
            if ((DateTime.Now - _lastRejoin).TotalSeconds < 5) { Log("rejoin ignored (" + why + ") - cooldown"); return; }
            _lastRejoin = DateTime.Now;
        }

        FindServer();   // always re-read, so we rejoin the server we are on right now
        if (_placeId == "" || _serverId == "") { Log("rejoin: no server found yet"); return; }

        // ALWAYS rejoin the exact same server. We used to drop the gameInstanceId when the same
        // server was retried within 25s, but that silently put the player into a random server -
        // the deep link with gameInstanceId is what pins the same server.
        string uri = "roblox://placeId=" + _placeId + "&gameInstanceId=" + _serverId;

        // silent reconnect: relaunch immediately, no LAND NOW and no black cover
        if (!overlay) { Log("   straight reconnect (no overlay)"); LaunchRejoin(uri, why); return; }

        // Show the LAND NOW alert up front, then (optionally) hold before the relaunch so the
        // alert is visible/recordable before the reconnect actually starts.
        ShowLandNow();
        if (_preRejoinMs > 0)
        {
            Log("   holding " + (_preRejoinMs / 1000) + "s before reconnecting (record delay)");
            string u = uri, w = why;
            int sg = _stopGen;
            Thread t = new Thread(delegate () { Thread.Sleep(_preRejoinMs); if (sg == _stopGen) LaunchRejoin(u, w); });
            t.IsBackground = true;
            t.Start();
            return;
        }
        LaunchRejoin(uri, why);
    }

    // the actual relaunch - split out so Rejoin can delay it for recording
    void LaunchRejoin(string uri, string why)
    {
        try
        {
            Log("   launching " + uri);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            _lastRejoinServerId = _serverId; _lastRejoinServerIdTime = DateTime.Now;
            Log("REJOIN (" + why + ") -> " + _serverId);

            if (_autoAfterRejoin)
            {
                // The relaunch sits on the OLD screen for ~10s before the game actually starts
                // reconnecting. If AUTO starts during that window it sees the live nav bar and
                // skips its "wait for the UI" step, then acts on the session we are leaving.
                // So wait here until the reconnect really begins (the loading screen appears, or
                // the old screen has been gone for a couple of reads), THEN run AUTO - whose step
                // 1 waits for team select / lobby / loadout to come back.
                Log("   AUTO after rejoin: waiting for the reconnect to begin...");
                int sg = _stopGen;
                Thread t = new Thread(delegate ()
                {
                    Thread.Sleep(_autoAfterRejoinMs);
                    if (sg != _stopGen) { Log("auto-after-rejoin cancelled by STOP"); return; }
                    long t0 = Environment.TickCount;
                    int gone = 0;
                    while ((Environment.TickCount - t0) < 90000)
                    {
                        if (sg != _stopGen) { Log("auto-after-rejoin cancelled by STOP"); return; }
                        List<string[]> ws = OcrWords();
                        string st = ScreenName(ws);
                        if (st == "loading") { Log("   reconnecting (loading screen)"); break; }
                        bool live = (st == "team select" || st == "lobby" || st == "loadout" ||
                                     st == "map" || st == "team base");
                        if (!live) { gone++; if (gone >= 2) { Log("   left the old screen - reconnecting"); break; } }
                        else gone = 0;
                        InvalidateOcr();
                        Thread.Sleep(400);
                    }
                    if (sg != _stopGen) { Log("auto-after-rejoin cancelled by STOP"); return; }
                    AutoRun();
                });
                t.IsBackground = true;
                t.Start();
            }
        }
        catch (Exception e) { Log("rejoin failed: " + e.Message); }
    }

    // ================= AUTO =================
    void AutoRun()
    {
        // hitting the trigger again RESTARTS the flow: bumping the generation cancels
        // whatever run is in progress and a fresh one takes over.
        int g = ++_autoGen;
        _running = true;
        _halted = false;                // starting again re-arms the auto-reconnect
        StopRfWatch();                  // clear any feed from the previous run
        _hoverMs = _hoverBase;          // start each run snappy; BumpHover raises it if needed
        // Restart mid-flow keeps the CLI progress and shows a milsim fault, so OBS viewers see a
        // recovery that resumes where it left off instead of the bar jumping back to zero.
        if (g > 1) AddBlackError("0x1B", "AUTO restart - re-syncing uplink");
        else if (_flowPct >= 0.99f) _flowPct = 0f;
        OverlayHub.I.SetMission(_team, _drone, _bomb);
        Log((g > 1 ? "=== AUTO RESTART" : "=== AUTO") + "   team " + _team + " | drone " + _drone +
            " | bomb " + _bomb + " | as drone " + _asDrone + " ===");

        Thread t = new Thread(delegate ()
        {
            try { AutoSteps(g); }
            catch (Exception ex) { Log("AUTO error: " + ex.Message); }
            if (g == _autoGen)
            {
                _running = false;
                try { BeginInvoke((MethodInvoker)delegate { Log("=== AUTO finished ==="); }); } catch { }
            }
            else
            {
                try { BeginInvoke((MethodInvoker)delegate { Log("(old AUTO run cancelled)"); }); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    void StopAuto()
    {
        _autoGen++;          // invalidates the running run
        _stopGen++;          // cancels any pending auto-after-rejoin launch too
        _halted = true;      // stop the log/OCR auto-reconnect too (it used to restart the flow)
        _running = false;
        StopRfWatch();
        HideBlack();
        HideLandNow();
        OverlayHub.I.SetHome("");
        // OBS CLI: hold the bar and show a milsim "paused" state until the operator re-establishes
        AddBlackLine("!! LINK PAUSED - operator halt", "BAD");
        AddBlackLine("   awaiting re-establishment...", "");
        OverlayHub.I.SetProgress(_flowPct, "paused - awaiting re-establishment");
        // STOP = "I'll take it from here". Turn the drone screen-detect ON, so if we are sitting
        // in a drone the user picked by hand the HUD/RF feed comes straight back up on its own.
        _hudAutoDetect = true;
        if (chkHudAuto != null) chkHudAuto.Checked = true;
        Log("STOP pressed - stopped AUTO, RF feed, covers and LAND NOW (drone detect on)");
    }

    bool Alive(int g) { return g == _autoGen; }

    // checked at every step boundary - stopping mid-run used to be ignored because the
    // step guards only looked at the per-step toggles, never the generation.
    bool GoOn(int g)
    {
        if (Alive(g)) return true;
        Log("AUTO stopped.");
        return false;
    }

    void AutoSteps(int g)
    {
        // RESUME-AWARE. Rather than always starting at step 1, work out where the game
        // actually is from its own on-screen text and pick up there. Re-triggering AUTO
        // halfway through therefore continues the flow instead of hanging on the first
        // step waiting for a screen that has already passed.
        //
        //   loading      "Joining server"
        //   team select  "PLAYERS IN TEAM"
        //   lobby nav    "CHANGE TEAM"
        //   loadout      "SELECT DRONE"
        //   map          "POINT" / "Base"
        //   team base    "TEAM BASE"
        List<string[]> ws0 = OcrWords();
        string state = ScreenName(ws0);
        Log("state on entry: " + state);

        bool mapUp = PhraseIn("POINT", ws0) || PhraseIn("Base", ws0);
        bool panelUp = PhraseIn("TEAM BASE", ws0);

        // 1 - get out of loading and into the game UI
        if (!GoOn(g)) return;
        if (state == "loading" || state == "unknown")
        {
            AddBlackProgress(0.08f, "loading");
            Log("1) waiting for the game UI...");
            int w = 0;
            while (Alive(g) && w < 120000
                   && !PhraseOnScreen("PLAYERS IN TEAM") && !PhraseOnScreen("CHANGE TEAM")
                   && !PhraseOnScreen("SELECT DRONE") && !PhraseOnScreen("DEPLOY"))
            { InvalidateOcr(); Thread.Sleep(150); w += 150; }
            List<string[]> ws1 = OcrWords();
            state = ScreenName(ws1);
            mapUp = PhraseIn("POINT", ws1) || PhraseIn("Base", ws1);
            panelUp = PhraseIn("TEAM BASE", ws1);
            Log("   now at \"" + state + "\" after " + (w / 1000.0).ToString("0.0") + "s");
        }
        else Log("1) skipped - already at \"" + state + "\"");
        AddBlackProgress(0.12f, "terrain db");

        // 2 - team. The number in "PLAYERS IN TEAM 17" is only the player COUNT and changes
        //     every match, so pick by COLOUR. Only runs if the team screen is really up.
        if (!GoOn(g)) return;
        AddBlackProgress(0.18f, "team select");
        if (_stepTeam && PhraseOnScreen("PLAYERS IN TEAM"))
        {
            Log("2) selecting the " + _team + " team by colour...");
            if (ClickBlobUntil("PLAYERS IN TEAM", _team != "Red", 4, g)) Log("   team selected - lobby is up");
            else Log("   could not confirm the team - continuing");
        }
        else Log("2) team step skipped (" + (state == "lobby" || state == "loadout" ? "already on a team" : "team screen not up") + ")");
        AddBlackProgress(0.30f, "team locked");

        // 3 - drone. FPV is the game's default, so ONLY a MAVIC run needs the LOADOUT switch.
        //     Never runs once the map or the base panel is up - that part already happened.
        if (!GoOn(g)) return;
        AddBlackProgress(0.40f, "loadout");
        // Applies to BOTH drones - it used to run only for MAVIC ("only MAVIC needs it"), so
        // choosing FPV left whatever was equipped (often MAVIC) and you deployed the wrong drone.
        if (_stepDrone && (_drone == "MAVIC" || _drone == "FPV") && !mapUp && !panelUp)
        {
            Log("3) switching drone to " + _drone + " (LOADOUT)...");
            ClickPhraseVerified("LOADOUT", g);
            if (WaitPhrase("SELECT DRONE", 6000, g))
            {
                InvalidateOcr();
                if (DroneIs(_drone))
                {
                    Log("   " + _drone + " already selected");
                }
                else if (DroneIs("MAVIC") || DroneIs("FPV"))
                {
                    // we can POSITIVELY see the OTHER drone, so flipping is safe
                    bool wantMavic = _drone == "MAVIC";
                    string cur = DroneIs("MAVIC") ? "MAVIC" : "FPV";
                    Log("   drone is " + cur + " -> clicking the " + (wantMavic ? "right" : "left") + " arrow");
                    ClickDroneArrow(wantMavic, g);       // right = MAVIC, left = FPV
                    Thread.Sleep(350);
                    InvalidateOcr();
                    Log(DroneIs(_drone) ? "   " + _drone + " selected" : "   could not confirm " + _drone);
                }
                else
                {
                    // We cannot read which drone is equipped. DO NOT click - this is exactly what
                    // used to turn a chosen FPV into a MAVIC (both chevrons just toggle, so a
                    // blind click flips it). Leave the loadout untouched.
                    Log("   could not read the current drone - leaving the loadout untouched");
                }
            }
            else Log("   SELECT DRONE panel did not open");
            // If the LOADOUT click dropped us into a weapon "Return" page instead, back out with
            // the red Return so the nav DEPLOY is clickable again for step 4.
            if (PhraseOnScreen("Return")) ClickRedReturn();
        }
        else Log("3) drone step skipped (" + (!_stepDrone ? "step off" : "map already open") + ")");
        AddBlackProgress(0.50f, "airframe set");

        // 4 - DEPLOY from the nav bar opens the map
        if (!GoOn(g)) return;
        if (_stepDeploy && !mapUp && !panelUp)
        {
            // If we are nested in a weapon / "Return" page the nav DEPLOY is still visible but the
            // map opens behind it - back out with the red Return first, then press DEPLOY again.
            for (int attempt = 1; attempt <= 2 && Alive(g); attempt++)
            {
                Log("4) pressing DEPLOY to open the map..." + (attempt > 1 ? " (retry)" : ""));
                ClickPhraseVerified("DEPLOY", "AS", g);
                if (WaitPhrase("POINT", 15000, g)) { Log("   map is up"); mapUp = true; break; }
                Log("   map did not appear");
                if (!ClickRedReturn()) break;      // nothing nested to come back from
                Thread.Sleep(500);
                InvalidateOcr();
            }
            AddBlackProgress(0.55f, "combat zone");
        }
        else Log("4) deploy step skipped (" + (mapUp || panelUp ? "map already open" : "step off") + ")");

        // 5 - Base. The map opens showing the POINT markers; the Base (the drone spawn) is
        //     usually off screen until you zoom out. Seeing points but no Base is the cue to
        //     zoom out. NEVER click a POINT: that is a control point, not where drones spawn.
        //     The "Base" label next to the tent icon is the only safe anchor - the tent is
        //     the same red as the POINT D/E/F pins, so colour alone cannot tell them apart.
        if (!GoOn(g)) return;
        if (_stepBase && !panelUp)
        {
            Log("5) looking for the Base (drone spawn)...");
            bool sawBase = false;
            int baseX = 0, baseY = 0;

            // Give the map time to finish opening after the DEPLOY click. Panning or zooming a
            // still-loading screen does nothing, so acting too early finds no Base and gives up.
            // Wait for the map to actually be up (POINT / Base on screen), capped.
            Thread.Sleep(300);
            long mapWait = Environment.TickCount;
            while ((Environment.TickCount - mapWait) < 10000 && Alive(g))
            {
                List<Hit> probe = FindPhraseAll("Base", null, OcrWordsWhiten());
                if (probe.Count == 0) probe = FindPhraseAll("Base", null, OcrWords());
                if (probe.Count > 0)
                {
                    // already visible - use it, do NOT start panning the map
                    sawBase = true; baseX = probe[0].X; baseY = probe[0].Y;
                    Log("   Base label is visible at (" + baseX + "," + baseY + ")");
                    break;
                }
                if (PhraseOnScreen("POINT")) break;
                Thread.Sleep(200);
                InvalidateOcr();
            }

            // Zoom out FIRST - the Base is usually just off the edge of the initial view, and the
            // wheel reveals it without moving the pointer around. Do NOT bail early on "no change":
            // the wheel redraw is often too subtle for Signature/WaitChange to see, so an old
            // max-zoom guess fired while the map still had plenty of zoom to give. Over-scrolling
            // is harmless - the map just clamps at its own max zoom.
            for (int z = 1; z <= 10 && !sawBase && Alive(g); z++)
            {
                List<Hit> bh = FindPhraseAll("Base", null, OcrWordsWhiten());
                if (bh.Count == 0) bh = FindPhraseAll("Base", null, OcrWords());
                if (bh.Count > 0)
                {
                    sawBase = true;
                    baseX = bh[0].X; baseY = bh[0].Y;
                    Log("   Base label is visible at (" + baseX + "," + baseY + ") (after " + (z - 1) + " zoom-outs)");
                    break;
                }
                Log("   Base not visible - zooming out (" + z + "/10)");
                // the wheel goes to the window under the cursor, and the game must be
                // in front or the wheel is swallowed - so focus, drift, then scroll
                FocusRoblox();
                MoveOverGameSoft(z);
                ScrollOut(2);
                Thread.Sleep(140);
            }

            // THEN pan, but only gently - the drag is clamped away from the screen edges so it can
            // never yank the pointer to the top of the window (which dragged the Roblox window
            // around). Panning is the fallback for a Base sitting off to one side.
            if (!sawBase)
            {
                int[,] pans = { { 520, 0 }, { -1040, 0 }, { 520, 0 }, { 0, 300 }, { 0, -600 }, { 0, 300 } };
                for (int p = 0; p < 6 && !sawBase && Alive(g); p++)
                {
                    Log("   panning the map to look for the Base (" + (p + 1) + "/6)");
                    FocusRoblox();
                    DragPan(pans[p, 0], pans[p, 1]);
                    Thread.Sleep(180);
                    InvalidateOcr();
                    List<Hit> bh = FindPhraseAll("Base", null, OcrWordsWhiten());
                    if (bh.Count == 0) bh = FindPhraseAll("Base", null, OcrWords());
                    if (bh.Count > 0)
                    {
                        sawBase = true; baseX = bh[0].X; baseY = bh[0].Y;
                        Log("   Base label is visible at (" + baseX + "," + baseY + ") after panning");
                    }
                }
            }

            if (!sawBase)
            {
                // The label is often unreadable when it is drawn over the tent icon. The icon
                // itself is still clickable, so click that instead of giving up.
                Log("   Base label not readable - trying the Base tent icon");
                int tx, ty;
                if (FindBaseTent(out tx, out ty)) { sawBase = true; baseX = tx; baseY = ty; }
                else
                {
                    // show near-misses so we can see whether the OCR is reading the label at all
                    Log("   no Base found - Close reads:");
                    foreach (string[] wd in OcrWords())
                    {
                        int sim = SimPct(wd[4], "Base");
                        if (sim >= 50) Log("      (" + wd[0] + "," + wd[1] + ") '" + wd[4] + "'  " + sim + "%");
                    }
                }
            }

            if (!sawBase)
            {
                // still on the map (the POINT markers are up) - do NOT fall through to the
                // warhead / deploy steps as if the base had been selected. Stop and let the
                // user retry instead of deploying blind.
                Log("5) could not find the Base - trying the red Return, then stopping");
                ClickRedReturn();
                return;
            }

            Log("   clicking the Base...");
            if (ClickBaseAt(baseX, baseY, g)) panelUp = true;
            else if (TeamBaseUp(false))
            {
                // the panel is actually up - the click verify just could not read it
                panelUp = true;
                Log("   TEAM BASE panel is up (read late) - continuing");
            }
            else
            {
                Log("5) could not open TEAM BASE - pressing the red Return, then stopping");
                ClickRedReturn();
                return;
            }
        }
        else Log("5) base step skipped (" + (panelUp ? "TEAM BASE already open" : "step off") + ")");

        // 6 - warhead. Click the fixed grid cell for the chosen warhead and confirm the
        //     green highlight, rather than trusting the tiny label text.
        if (!GoOn(g)) return;
        bool warheadOk = true;
        AddBlackProgress(0.62f, "payload");
        if (_stepBomb && _bomb != "(none)" && TeamBaseUp(panelUp))
        {
            Log("6) selecting warhead " + _bomb + "...");
            warheadOk = ClickWarhead(g);
            if (!warheadOk) Log("   " + _bomb + " is not equipped");
            AddBlackProgress(0.75f, "warhead armed");
        }
        else Log("6) bomb step skipped (" + (_bomb == "(none)" || !_stepBomb ? "step off" : "TEAM BASE not open") + ")");

        // 7 - final deploy, only from the Team Base panel, and only once the warhead box is
        //     green. If the requested warhead could not be equipped, stop here rather than
        //     deploying with the wrong loadout.
        if (!GoOn(g)) return;
        if (!warheadOk)
        {
            // Wrong bombs / wrong loadout - do NOT deploy. Back out to the LOADOUT screen so the
            // drone + warhead can be re-selected, then stop this run.
            Log("7) wrong loadout (" + _bomb + " not equipped) - returning to LOADOUT, not deploying");
            AddBlackError("0x13", "payload mismatch - aborting to loadout");
            ClickPhraseVerified("LOADOUT", g);
            HideBlack();
            return;
        }
        if (TeamBaseUp(panelUp))
        {
            AddBlackProgress(0.84f, "arm");
            if (_asDrone)
            {
                Log("7) clicking Deploy As Drone...");
                AddBlackLine("connecting to drone", "");
                AddBlackProgress(0.90f, "connecting to drone");
                bool ok = ClickPhrasePersistent("Deploy As Drone", 20000, g);
                if (!ok) Log("   Deploy As Drone did not react");
                AddBlackLine("  uplink established", ok ? "OK" : "BAD");
            }
            else
            {
                Log("7) clicking Deploy...");
                if (!ClickPhraseVerified("Deploy", "As", g)) Log("   Deploy did not react");
            }
        }
        else Log("7) deploy skipped - the TEAM BASE panel is not open");

        // drone deployed - the terminal cover has done its job
        AddBlackProgress(1.0f, "drone online");
        AddBlackLine("drone online", "OK");
        Thread.Sleep(500);
        HideBlack();

        // The payload we just selected tells us which HUD to use, so lock the style in the
        // moment we click Deploy As Drone (no need to wait to read it off the HUD).
        _hudStyleUav = MavicBomb(_bomb);
        Log("   HUD style from payload: " + (_hudStyleUav ? "UAV (DJI)" : "FPV"));

        // now that we are flying, keep reading the HOME distance / telemetry. The loop drives
        // the fake RF feed (if RF watch is on) and the FPV/UAV HUD (if the HUD is on), so it
        // starts when EITHER is wanted.
        if ((_watchHome || _hudOn) && _asDrone) StartRfWatch();

        Thread.Sleep(150);
        Log("AUTO sequence complete.");
    }

    bool ClickWord(string word, int timeoutMs, int g) { return ClickWordOffset(word, 0, 0, timeoutMs, g); }

    bool ClickWordOffset(string word, int dx, int dy, int timeoutMs, int g)
    {
        int spent = 0;
        int attempt = 0;
        while (Alive(g) && spent <= timeoutMs)
        {
            string[] hit = FindWord(word);
            if (hit != null)
            {
                int px = int.Parse(hit[0]) + int.Parse(hit[2]) / 2 + dx;
                int py = int.Parse(hit[1]) + int.Parse(hit[3]) / 2 + dy;
                Log("   OCR \"" + word + "\" -> (" + px + "," + py + ")");
                attempt++;

                if (attempt == 1)
                    ClickPrimaryLogged(px, py, "\"" + word + "\"");
                else if (attempt == 2 && _clickKeyOn && _clickVk != 0)
                    ClickLogged(px, py, "\"" + word + "\"");
                else
                {
                    int n = ClickHard(px, py);
                    InvalidateOcr();
                    Log("   hard click \"" + word + "\" (" + px + "," + py + ") accepted=" + n + "/3");
                }

                // click -> check -> click -> check. If the word is still up after a beat,
                // the click did not register (lag) so hit it again without restarting.
                if (!WordOnScreen(word)) return true;
                InvalidateOcr();
                Thread.Sleep(120); spent += 120;
                if (!WordOnScreen(word)) return true;
                Log("   \"" + word + "\" still up - clicking again");
                continue;
            }
            InvalidateOcr();
            Thread.Sleep(120); spent += 120;
        }
        Log("   \"" + word + "\" not found in " + (timeoutMs / 1000) + "s");
        return false;
    }

    // ---- tolerant token compare ----
    // The OCR is noisy: it reads "TBG-7B" as "T8G-7B" and drags punctuation into words
    // ("(untick", "skip)", "key..."). Normalise, then compare by similarity so a word that
    // matches the set percentage still counts (tunable in the UI, default 80%).
    static string Norm(string s)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static int _matchPct = 80;

    // Damerau-Levenshtein - single-character insert/delete/substitute edits, and a
    // transposition counts as ONE edit ("Standrad" -> "Standard" is a single slip)
    static int Lev(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (la == 0) return lb;
        if (lb == 0) return la;
        int[] prev2 = new int[lb + 1], prev = new int[lb + 1], cur = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;
        for (int i = 1; i <= la; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int m = prev[j] + 1;
                if (cur[j - 1] + 1 < m) m = cur[j - 1] + 1;
                if (prev[j - 1] + cost < m) m = prev[j - 1] + cost;
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    if (prev2[j - 2] + 1 < m) m = prev2[j - 2] + 1;
                cur[j] = m;
            }
            int[] t = prev2; prev2 = prev; prev = cur; cur = t;
        }
        return prev[lb];
    }

    // similarity as a percentage of the longer of the two words
    static int SimPct(string a, string b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 100;
        int max = Math.Max(x.Length, y.Length);
        return (int)Math.Round(100.0 * (max - Lev(x, y)) / max);
    }

    // true when the words match closely enough to be considered the same
    static bool TokEq(string a, string b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;

        // Player nameplates sit on top of map labels and bleed a character in: the OCR
        // read "Baseu" for "Base". Accept a short prefix/suffix extension regardless of
        // the percentage threshold so an obscured label still counts.
        string shorter = x.Length <= y.Length ? x : y;
        string longer = x.Length <= y.Length ? y : x;
        if (shorter.Length >= 3 && longer.Length - shorter.Length <= 2 && longer.StartsWith(shorter))
            return true;

        return SimPct(x, y) >= _matchPct;
    }

    string[] FindWord(string word)
    {
        List<string[]> ws = OcrWords();
        string[] best = null; int bestScore = -1;
        string wn = Norm(word);
        foreach (string[] wd in ws)
        {
            string t = wd[4];
            string tn = Norm(t);
            if (tn.Length == 0 || wn.Length == 0) continue;

            int score;
            if (tn == wn) score = 3000000;                          // perfect spelling wins
            else
            {
                int sim = SimPct(tn, wn);
                if (sim >= _matchPct) score = 2000000 + sim * 1000; // closest spelling wins
                else if (t.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    score = 1000000 - (t.Length - word.Length) * 100;
                else continue;
            }
            // on-screen size only breaks a tie between equally good spellings
            try
            {
                int area = int.Parse(wd[2]) * int.Parse(wd[3]);
                score += Math.Min(area / 100, 900);
            }
            catch { }
            if (score > bestScore) { bestScore = score; best = wd; }
        }
        return best;
    }

    bool WordOnScreen(string word) { return FindWord(word) != null; }

    // ---- phrase search ----
    // The OCR emits one entry per WORD, so multi-word UI text ("TEAM BASE", "PLAYERS IN
    // TEAM", "SELECT DRONE") can never match a single-word lookup. Group words into visual
    // lines by their y band, then look for the phrase as consecutive words on a line.
    struct Hit { public int X, Y, Area, Quality; public Hit(int x, int y, int a, int q) { X = x; Y = y; Area = a; Quality = q; } }

    List<Hit> FindPhraseAll(string phrase) { return FindPhraseAll(phrase, null, null); }

    // excludeNext: skip a match whose next word equals this, e.g. "Deploy" + "As" so the
    // Deploy button is not confused with "Deploy As Drone".
    List<Hit> FindPhraseAll(string phrase, string excludeNext) { return FindPhraseAll(phrase, excludeNext, null); }

    // source: optional pre-read word list (e.g. the whitened OCR pass). Null means the
    // normal screen OCR.
    List<Hit> FindPhraseAll(string phrase, string excludeNext, List<string[]> source)
    {
        List<Hit> hits = new List<Hit>();
        List<Hit> mergeHits = new List<Hit>();
        List<string[]> ws = source != null ? source : OcrWords();
        if (ws.Count == 0) return hits;

        string[] want = phrase.Trim().ToLowerInvariant()
            .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (want.Length == 0) return hits;

        // the same phrase with the spaces squeezed out, for when the OCR runs the words
        // together ("TEAM BASE" comes back as one token "TEAMBASE")
        string wantCat = "";
        foreach (string w in want) wantCat += Norm(w);

        // The OCR does NOT return reading order, and words on the same visual line have
        // slightly different y values. Sorting by (y,x) therefore scrambles the line -
        // "Steps to run" came back as "Steps (untick skip) to to run". Sort by y, group
        // into lines, then sort each line by x before matching.
        List<string[]> sorted = new List<string[]>(ws);
        sorted.Sort(delegate (string[] a, string[] b)
        {
            return int.Parse(a[1]).CompareTo(int.Parse(b[1]));
        });

        int i = 0;
        while (i < sorted.Count)
        {
            int ly = int.Parse(sorted[i][1]), lh = int.Parse(sorted[i][3]);
            int lineCentre = ly + lh / 2;
            int j = i; List<string[]> line = new List<string[]>();
            while (j < sorted.Count)
            {
                int wy = int.Parse(sorted[j][1]), wh = int.Parse(sorted[j][3]);
                if (Math.Abs(lineCentre - (wy + wh / 2)) > Math.Max(lh, wh) * 6 / 10) break;
                line.Add(sorted[j]); j++;
            }
            line.Sort(delegate (string[] a, string[] b)
            {
                return int.Parse(a[0]).CompareTo(int.Parse(b[0]));
            });
            for (int s = 0; s + want.Length <= line.Count; s++)
            {
                bool ok = true;
                for (int k = 0; k < want.Length; k++)
                    if (!TokEq(line[s + k][4], want[k])) { ok = false; break; }
                if (!ok) continue;
                if (excludeNext != null && s + want.Length < line.Count
                    && TokEq(line[s + want.Length][4], excludeNext)) continue;

                int x1 = int.MaxValue, y1 = int.MaxValue, x2 = 0, y2 = 0;
                int simSum = 0;
                for (int k = 0; k < want.Length; k++)
                {
                    string[] w = line[s + k];
                    int wx = int.Parse(w[0]), wy = int.Parse(w[1]), ww = int.Parse(w[2]), wh2 = int.Parse(w[3]);
                    if (wx < x1) x1 = wx;
                    if (wy < y1) y1 = wy;
                    if (wx + ww > x2) x2 = wx + ww;
                    if (wy + wh2 > y2) y2 = wy + wh2;
                    simSum += SimPct(w[4], want[k]);
                }
                int quality = simSum / want.Length;   // 0..100, how well the spelling matched
                hits.Add(new Hit((x1 + x2) / 2, (y1 + y2) / 2, Math.Max(1, (x2 - x1) * (y2 - y1)), quality));
            }

            // Runs of adjacent words with the spaces removed, compared to the phrase with
            // the spaces removed. This is what catches OCR that merges or splits a label:
            // "TEAM BASE" -> "TEAMBASE", "Deploy As Drone" -> "Deploy" + "Asprone".
            for (int s = 0; s < line.Count; s++)
            {
                string acc = "";
                for (int e = s; e < line.Count; e++)
                {
                    acc += Norm(line[e][4]);
                    if (acc.Length > wantCat.Length + 4) break;
                    int sim = SimPct(acc, wantCat);
                    if (sim < _matchPct) continue;
                    if (excludeNext != null && e + 1 < line.Count
                        && TokEq(line[e + 1][4], excludeNext)) continue;

                    int x1 = int.MaxValue, y1 = int.MaxValue, x2 = 0, y2 = 0;
                    for (int k = s; k <= e; k++)
                    {
                        int wx = int.Parse(line[k][0]), wy = int.Parse(line[k][1]);
                        int ww = int.Parse(line[k][2]), wh2 = int.Parse(line[k][3]);
                        if (wx < x1) x1 = wx;
                        if (wy < y1) y1 = wy;
                        if (wx + ww > x2) x2 = wx + ww;
                        if (wy + wh2 > y2) y2 = wy + wh2;
                    }
                    mergeHits.Add(new Hit((x1 + x2) / 2, (y1 + y2) / 2,
                        Math.Max(1, (x2 - x1) * (y2 - y1)), sim));
                }
            }
            i = j;
        }

        // Second pass: button labels are often printed on TWO lines ("TBG-7B" over
        // "Thermobaric", "PG-7VS" over "Shaped"). Find the first word, then the remaining
        // words stacked underneath it in the same column.
        if (want.Length >= 2)
        {
            foreach (string[] t0 in ws)
            {
                if (!TokEq(t0[4], want[0])) continue;

                int x1 = int.Parse(t0[0]), y1 = int.Parse(t0[1]);
                int x2 = x1 + int.Parse(t0[2]), y2 = y1 + int.Parse(t0[3]);
                int colLeft = x1, colRight = x2;
                int curBottom = y2, curH = int.Parse(t0[3]);
                int simSum = SimPct(t0[4], want[0]);
                bool ok = true;

                for (int k = 1; k < want.Length; k++)
                {
                    string[] f = null;
                    foreach (string[] c in ws)
                    {
                        if (!TokEq(c[4], want[k])) continue;
                        int cy = int.Parse(c[1]), chh = int.Parse(c[3]);
                        int cxx = int.Parse(c[0]), cww = int.Parse(c[2]);
                        if (cy < curBottom - curH / 2) continue;                   // must sit below
                        if (cy > curBottom + Math.Max(curH, chh) * 2) continue;    // but stay nearby
                        if (!(cxx < colRight && cxx + cww > colLeft)) continue;    // same column
                        if (f == null || cy < int.Parse(f[1])) f = c;              // topmost wins
                    }
                    if (f == null) { ok = false; break; }

                    int fx = int.Parse(f[0]), fy = int.Parse(f[1]);
                    int fw = int.Parse(f[2]), fh = int.Parse(f[3]);
                    if (fx < x1) x1 = fx;
                    if (fy < y1) y1 = fy;
                    if (fx + fw > x2) x2 = fx + fw;
                    if (fy + fh > y2) y2 = fy + fh;
                    curBottom = fy + fh; curH = fh;
                    simSum += SimPct(f[4], want[k]);
                }

                if (ok)
                    hits.Add(new Hit((x1 + x2) / 2, (y1 + y2) / 2,
                        Math.Max(1, (x2 - x1) * (y2 - y1)), simSum / want.Length));
            }
        }

        // the space-squeezed matches are a fallback only: use them when nothing matched
        // normally, so a normal spelling always wins over a merged-token guess
        if (hits.Count == 0 && mergeHits.Count > 0) hits.AddRange(mergeHits);

        // best spelling first; size only breaks a tie between equally good matches
        hits.Sort(delegate (Hit a, Hit b)
        {
            if (a.Quality != b.Quality) return b.Quality.CompareTo(a.Quality);
            return b.Area.CompareTo(a.Area);
        });
        return hits;
    }

    bool PhraseOnScreen(string phrase) { return FindPhraseAll(phrase).Count > 0; }

    // same as PhraseOnScreen but against a word list already read once, so several phrases
    // can be tested from a single OCR pass instead of re-scanning per phrase
    bool PhraseIn(string phrase, List<string[]> ws) { return FindPhraseAll(phrase, null, ws).Count > 0; }

    // the SELECT DRONE panel prints the current drone as "FPV Drone Customization" /
    // "MAVIC Drone Customization" - a white label the plain read sometimes misses, so try
    // the whitened OCR too
    bool DroneIs(string name) { return PhraseOnScreen(name) || PhraseOnScreenWhiten(name); }

    // The TEAM BASE header can be missed for a frame while the panel animates open, so a single
    // read right after clicking the Base is too flaky (the next step gets skipped and the run
    // stops). panelUp already means we saw it, so trust that and retry the read a few times.
    bool TeamBaseUp(bool knownUp)
    {
        if (knownUp) return true;
        for (int i = 0; i < 4; i++)
        {
            // the panel has several unique labels - accept ANY of them, because "TEAM BASE" is
            // white over bright green and the OCR often misses it, which made the flow think the
            // panel never opened (and bail) while it was actually right there.
            if (PhraseOnScreen("TEAM BASE") || PhraseOnScreenWhiten("TEAM BASE")) return true;
            if (PhraseOnScreen("DEPLOY AS DRONE") || PhraseOnScreenWhiten("DEPLOY AS DRONE")) return true;
            if (PhraseOnScreen("WARHEAD") || PhraseOnScreenWhiten("WARHEAD")) return true;
            InvalidateOcr();
            Thread.Sleep(200);
        }
        return false;
    }

    // The TEAM BASE / map panels have a red "Return" button. When the flow cannot find what it
    // expects, pressing it backs out of the panel instead of leaving the run stuck.
    bool ClickRedReturn()
    {
        try
        {
            List<Hit> h = FindPhraseAll("Return", null, OcrWords());
            if (h.Count == 0) h = FindPhraseAll("Return", null, OcrWordsWhiten());
            if (h.Count == 0) h = FindPhraseAll("Retum", null, OcrWords());
            if (h.Count == 0) return false;
            Log("   pressing the red Return to back out at (" + h[0].X + "," + h[0].Y + ")");
            ClickPrimaryLogged(h[0].X, h[0].Y, "Return");
            return true;
        }
        catch { return false; }
    }

    bool WaitPhrase(string phrase, int ms, int g)
    {
        int spent = 0;
        while (Alive(g) && spent < ms)
        {
            if (PhraseOnScreen(phrase)) return true;
            InvalidateOcr();                    // force a fresh read next loop, not a cached miss
            Thread.Sleep(120); spent += 120;
        }
        return PhraseOnScreen(phrase);
    }

    // What screen are we actually looking at? Every step uses this instead of guessing.
    string ScreenName() { return ScreenName(OcrWords()); }

    string ScreenName(List<string[]> ws)
    {
        // "CHANGE TEAM" is a permanent NAV-BAR button, so it is on screen in the lobby AND on
        // the team-select screen. It therefore cannot be used to tell them apart - the big
        // "PLAYERS IN TEAM" panel is the team-select anchor and has to be checked first.
        if (PhraseIn("TEAM BASE", ws)) return "team base";
        if (PhraseIn("SELECT DRONE", ws)) return "loadout";
        if (PhraseIn("POINT", ws) || PhraseIn("Base", ws)) return "map";
        if (PhraseIn("PLAYERS IN TEAM", ws)) return "team select";
        if (PhraseIn("CHANGE TEAM", ws)) return "lobby";
        if (PhraseIn("Joining server", ws)) return "loading";
        return "unknown";
    }

    // click the best-looking match; if the screen does not react, try the next match. This
    // is what makes the warhead row reliable - the description line contains the same words
    // as the button, and only the real button changes the screen.
    bool ClickPhraseVerified(string phrase, int g) { return ClickPhraseVerified(phrase, null, g); }

    bool ClickPhraseVerified(string phrase, string excludeNext, int g)
    {
        // Button labels wrap, so if the whole label is not found try just its first word -
        // "TBG-7B Thermobaric" may only have "TBG-7B" readable. Click-verify still has to
        // see the screen change before it accepts a candidate.
        bool haveMacro = _clickKeyOn && _clickVk != 0;
        bool sawOnce = false;

        // Re-read the screen on EVERY attempt. The label drifts as panels animate and cells
        // scale under the cursor, so a coordinate captured once goes stale and the next click
        // lands in empty space - which is what makes the flow stall on "Deploy As Drone".
        for (int attempt = 1; attempt <= 8 && Alive(g); attempt++)
        {
            List<Hit> hits = FindPhraseAll(phrase, excludeNext);
            string used = phrase;
            if (hits.Count == 0 && phrase.IndexOf(' ') > 0)
            {
                string first = phrase.Substring(0, phrase.IndexOf(' '));
                hits = FindPhraseAll(first, excludeNext);
                used = first;
                if (hits.Count > 0) Log("   \"" + phrase + "\" not found as a label - trying just \"" + first + "\"");
            }

            if (hits.Count == 0)
            {
                if (!sawOnce) Log("   \"" + phrase + "\" is not on screen");
                InvalidateOcr();          // it may just be mid-animation - look again
                Thread.Sleep(250);
                continue;
            }
            sawOnce = true;

            foreach (Hit h in hits)
            {
                if (!Alive(g)) return false;
                int[] before = Signature();
                Log("   clicking \"" + used + "\" at (" + h.X + "," + h.Y + ") (attempt " + attempt + ")");

                if (attempt <= 2)
                {
                    // hover then fire two quick clicks - the first is often swallowed by the
                    // focus/resync, and the second (a bare press, pointer already there) lands
                    // before the UI can change
                    ClickPrimaryLogged(h.X, h.Y, "\"" + used + "\"");
                    Thread.Sleep(45);
                    if (haveMacro) SendKey(_clickVk); else ClickAgain();
                }
                else if (attempt <= 4)
                {
                    ClickLogged(h.X, h.Y, "\"" + used + "\"");
                    Thread.Sleep(45);
                    ClickAgain();
                }
                else
                {
                    int n = ClickHard(h.X, h.Y);
                    Log("   hard click \"" + used + "\" (" + h.X + "," + h.Y + ") accepted=" + n + "/3");
                }

                if (WaitChange(before, 2.0, attempt <= 2 ? 700 : 1000, g)) return true;
                BumpHover();
                InvalidateOcr();
                Log("   \"" + used + "\" at (" + h.X + "," + h.Y + ") did nothing (attempt " + attempt + ")");
            }
            Thread.Sleep(120);
        }
        return false;
    }

    // Keep re-reading the screen about once a second and press the label the moment it is found.
    // Used for the last step (Deploy As Drone): the panel animates, the button drifts under the
    // cursor, and a single-shot lookup keeps missing it. Every round uses fresh OCR coordinates,
    // and the loop only stops when the screen actually reacts (panel closes) or the time runs out.
    bool ClickPhrasePersistent(string phrase, int timeoutMs, int g)
    {
        int waited = 0, round = 0;
        bool haveMacro = _clickKeyOn && _clickVk != 0;
        while (waited < timeoutMs && Alive(g))
        {
            round++;
            List<Hit> hits = FindPhraseAll(phrase);
            if (hits.Count > 0)
            {
                Hit h = hits[0];
                int[] before = Signature();
                Log("   \"" + phrase + "\" at (" + h.X + "," + h.Y + ") - clicking (round " + round + ")");
                ClickPrimaryLogged(h.X, h.Y, "\"" + phrase + "\"");
                Thread.Sleep(60);
                if (haveMacro) SendKey(_clickVk); else ClickAgain();
                if (WaitChange(before, 1.2, 400, g)) return true;
                Log("   \"" + phrase + "\" did not react - still looking");
                BumpHover();
            }
            InvalidateOcr();                 // force the next read to be a fresh capture
            Thread.Sleep(1000);              // look once a second - cheap, resident OCR
            waited += 1000;
        }
        return false;
    }

    // The SELECT DRONE panel has < and > chevrons that the OCR cannot read (it only sees the
    // header text). They are drawn in near-white at the two ends of the drone preview box, but
    // the row's vertical offset drifts between layouts, so find the glyphs directly instead of
    // anchoring to fixed pixels. Locate the header for a search band, then pick the two small
    // bright components that are furthest apart on the same line - those are the chevrons.
    void ClickDroneArrow(bool right, int g)
    {
        List<Hit> h = FindPhraseAll("SELECT DRONE");
        if (h.Count == 0) h = FindPhraseAll("DRONE");
        if (h.Count == 0) { Log("   SELECT DRONE header not found"); return; }

        FocusRoblox();
        int headerY = h[0].Y;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int lx, ly, rx, ry, x, y;
            if (FindChevrons(headerY, out lx, out ly, out rx, out ry))
            {
                x = right ? rx : lx;
                y = right ? ry : ly;
            }
            else
            {
                // last resort: the chevrons sit at the two ends of the preview box
                int w = Screen.PrimaryScreen.Bounds.Width;
                x = right ? (int)(w * 0.337) : (int)(w * 0.025);
                y = headerY + 25;
            }
            MoveTo(x, y);
            int[] before = Signature();
            ClickPrimaryLogged(x, y, right ? "drone > arrow" : "drone < arrow");
            if (WaitChange(before, 1.0, 500, g)) return;
            Log("   chevron click (" + x + "," + y + ") did nothing (try " + (attempt + 1) + ")");
            BumpHover();
            InvalidateOcr();
            Thread.Sleep(250);
        }
        Log("   drone arrow did not change the screen");
    }

    // Find the < and > chevrons as bright pixel components. The header text is also bright, so
    // return the two small components that share a line and are furthest apart (the chevrons),
    // ignoring the wider text blobs. Falls back to false if the pairing is unconvincing.
    bool FindChevrons(int headerY, out int leftX, out int leftY, out int rightX, out int rightY)
    {
        leftX = leftY = rightX = rightY = -1;
        try
        {
            InvalidateGrab();
            int W, H; int[] px = Grab(out W, out H);
            int y0 = Math.Max(0, headerY - 120), y1 = Math.Min(H - 1, headerY + 150);
            int bw = W / 2, bh = y1 - y0 + 1;      // right-side HUD text lives beyond the halfway mark
            bool[] m = new bool[bw * bh];
            for (int y = y0; y <= y1; y++)
            {
                int row = y * W;
                for (int x = 0; x < bw; x++)
                {
                    int c = px[row + x];
                    if (((c >> 16) & 0xFF) > 205 && ((c >> 8) & 0xFF) > 205 && (c & 0xFF) > 205)
                        m[(y - y0) * bw + x] = true;
                }
            }
            List<int[]> comps = new List<int[]>();   // {cx, cy, area}
            bool[] seen = new bool[m.Length];
            int[] stack = new int[m.Length];
            for (int i = 0; i < m.Length; i++)
            {
                if (!m[i] || seen[i]) continue;
                int sp = 0; stack[sp++] = i; seen[i] = true;
                int cnt = 0, sx = 0, sy = 0;
                while (sp > 0)
                {
                    int p = stack[--sp];
                    int cx = p % bw, cy = p / bw;
                    cnt++; sx += cx; sy += cy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                            int q = ny * bw + nx;
                            if (m[q] && !seen[q]) { seen[q] = true; stack[sp++] = q; }
                        }
                }
                if (cnt >= 12) comps.Add(new int[] { sx / cnt, y0 + sy / cnt, cnt });
            }
            int bestD = -1, bi = -1, bj = -1;
            for (int i = 0; i < comps.Count; i++)
                for (int j = i + 1; j < comps.Count; j++)
                {
                    if (comps[i][2] > 250 || comps[j][2] > 250) continue;   // too big to be a glyph
                    if (Math.Abs(comps[i][1] - comps[j][1]) > 16) continue; // must sit on one line
                    int d = Math.Abs(comps[i][0] - comps[j][0]);
                    if (d > bestD) { bestD = d; bi = i; bj = j; }
                }
            if (bi < 0 || bestD < 200) return false;
            int a = comps[bi][0], b = comps[bj][0];
            if (a < b) { leftX = comps[bi][0]; leftY = comps[bi][1]; rightX = comps[bj][0]; rightY = comps[bj][1]; }
            else { leftX = comps[bj][0]; leftY = comps[bj][1]; rightX = comps[bi][0]; rightY = comps[bi][1]; }
            Log("   chevrons < (" + leftX + "," + leftY + ")  > (" + rightX + "," + rightY + ")");
            return true;
        }
        catch { return false; }
    }

    // Click the Base marker. The label is read through the whitened OCR; the label sits
    // just under the tent icon, so the retries walk upward onto the tent in case the text
    // itself is not the clickable part. Success is the TEAM BASE panel opening.
    bool ClickBaseAt(int bx, int by, int g)
    {
        // the label sits under the tent, but the clickable marker is the tent itself, so aim
        // a little above the text first and step further up only if the panel does not open.
        // t==1 injected click, t==2 macro-key click (backup), t>=3 heavy press on the tent.
        int[] dys = new int[] { -30, -30, -55, -12 };
        for (int t = 1; t <= 4 && Alive(g); t++)
        {
            int dy = dys[Math.Min(t - 1, dys.Length - 1)];
            int rx = bx, ry = by + dy;
            Log("   clicking the Base at (" + rx + "," + ry + ") (" + t + "/4)");
            if (t == 1) ClickPrimaryLogged(rx, ry, "Base");
            else if (t == 2 && _clickKeyOn && _clickVk != 0) ClickLogged(rx, ry, "Base");
            else { ClickHard(rx, ry); InvalidateOcr(); }
            if (WaitPhrase("TEAM BASE", 8000, g))
            {
                // no settle needed here - the grid only shifts when the cursor hovers a cell,
                // so ClickWarhead measures, hovers, re-measures, then clicks
                InvalidateOcr();
                Log("   TEAM BASE panel is open");
                return true;
            }
            Log("   TEAM BASE panel not up - retrying (" + t + "/4)");
            Thread.Sleep(250);
        }
        return false;
    }

    // Last-resort Base finder. When the "Base" label is drawn over the tent icon the OCR can
    // miss it, but the icon is still clickable. The POINT pins are a fixed red teardrop
    // (53x77, aspect ~0.69); the Base tent is near-square (~73x77). Find red blobs, drop the
    // thin front-line strips and the small diamond specks, merge the tent's fragments, and
    // return the near-square cluster. A wrong pick is harmless: ClickBaseAt only succeeds if
    // the TEAM BASE panel actually opens, otherwise the base step stops the flow.
    bool FindBaseTent(out int bx, out int by)
    {
        bx = by = -1;
        try
        {
            InvalidateGrab();
            int W, H; int[] px = Grab(out W, out H);
            bool[] m = new bool[W * H];
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i];
                int b = c & 0xFF, g = (c >> 8) & 0xFF, r = (c >> 16) & 0xFF;
                m[i] = r > 150 && g < 95 && b < 95;
            }
            List<Rectangle> blobs = new List<Rectangle>();
            bool[] seen = new bool[m.Length];
            int[] stack = new int[m.Length];
            for (int i = 0; i < m.Length; i++)
            {
                if (!m[i] || seen[i]) continue;
                int sp = 0; stack[sp++] = i; seen[i] = true;
                int x0 = i % W, y0 = i / W, x1 = x0, y1 = y0, area = 0;
                while (sp > 0)
                {
                    int p = stack[--sp]; area++;
                    int x = p % W, y = p / W;
                    if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y;
                    if (x > 0 && m[p-1] && !seen[p-1]) { seen[p-1]=true; stack[sp++]=p-1; }
                    if (x < W-1 && m[p+1] && !seen[p+1]) { seen[p+1]=true; stack[sp++]=p+1; }
                    if (y > 0 && m[p-W] && !seen[p-W]) { seen[p-W]=true; stack[sp++]=p-W; }
                    if (y < H-1 && m[p+W] && !seen[p+W]) { seen[p+W]=true; stack[sp++]=p+W; }
                }
                int bw = x1-x0+1, bh = y1-y0+1;
                // drop the thin front-line strip (bh ~10) and the tiny diamond specks (~30x29)
                if (area < 500 || bw < 30 || bh < 35 || bw > 220 || bh > 220) continue;
                blobs.Add(new Rectangle(x0, y0, bw, bh));
            }

            // merge fragments of the same icon (the tent is split by the label/flag)
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < blobs.Count && !again; i++)
                    for (int j = i + 1; j < blobs.Count && !again; j++)
                    {
                        Rectangle a = blobs[i], b = blobs[j];
                        bool near = a.X - 45 <= b.Right && b.X - 45 <= a.Right &&
                                    a.Y - 45 <= b.Bottom && b.Y - 45 <= a.Bottom;
                        if (near) { blobs[i] = Rectangle.Union(a, b); blobs.RemoveAt(j); again = true; }
                    }
            }

            int best = -1; double bestErr = 1e9;
            for (int i = 0; i < blobs.Count; i++)
            {
                double ar = (double)blobs[i].Width / blobs[i].Height;
                if (ar < 0.85 || ar > 1.5) continue;          // pins are ~0.69 - too tall to be the tent
                if (blobs[i].Height < 40 || blobs[i].Height > 100) continue;
                if (blobs[i].Width < 45) continue;
                double err = Math.Abs(ar - 1.0);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best < 0) return false;

            Rectangle t = blobs[best];
            bx = t.X + t.Width / 2;
            by = t.Y + t.Height - 8;                 // near the bottom, so ClickBaseAt walks up onto it
            Log("   Base tent detected at (" + bx + "," + by + ") size " + t.Width + "x" + t.Height);
            return true;
        }
        catch (Exception e) { Log("   tent detect failed: " + e.Message); return false; }
    }

    // The warhead buttons carry tiny, low-contrast labels the OCR mangles ("TBG-7B" read
    // as "rBC.7B"), so do not rely on the text and do not use hard-coded panel pixels (the
    // panel moves and scales with the game window). Instead find the actual grid cells by
    // colour + shape and click the Nth one, then REQUIRE the green equipped highlight before
    // the flow is allowed to continue to Deploy As Drone.
    bool ClickWarhead(int g)
    {
        int idx = System.Array.IndexOf(BombsFor(_drone), _bomb);
        if (idx <= 0) { Log("   " + _bomb + " is not a " + _drone + " warhead"); return false; }
        int slot = idx - 1;                       // 0-based grid position
        int count = BombsFor(_drone).Length - 1;  // number of warheads on the panel

        Thread.Sleep(250);   // let the TEAM BASE panel finish laying out before measuring the grid

        // If the anchored grid is a few px off (different game height), the first click lands just
        // outside the cell and nothing turns green. Retry while sliding the grid vertically, so
        // the step self-calibrates instead of clicking the same wrong spot four times.
        int[] nudges = new int[] { 0, -14, 14, -28 };
        for (int t = 1; t <= 4 && Alive(g); t++)
        {
            _whRowNudge = nudges[t - 1];
            // Hovering a cell animates/scales it, which nudges the whole grid, so measure with
            // the pointer OFF the panel. On the first try the previous step already left the
            // pointer off the grid, so only move on a retry (when it is sitting on the cell).
            if (t > 1) MoveToDeployAsDrone();
            InvalidateGrab();
            Thread.Sleep(90);

            List<Point> cells = FindWarheadCells(count);
            if (cells.Count <= slot)
            {
                Log("   found " + cells.Count + " warhead cells, need " + (slot + 1) + " - not clicking");
                Thread.Sleep(200);
                continue;
            }
            // Which slot is the green SELECTED one? Compare ALL the cells, not just ours - the
            // translucent panel lets green terrain show through, so a single "is our cell green?"
            // test false-positives over grass and the step wrongly concludes the warhead is
            // already equipped, leaving the default in place.
            int gW, gH; int[] gpx = Grab(out gW, out gH);
            int cur = GreenSlot(gpx, gW, gH, cells, count);
            Log("   warhead slots: green now = " +
                (cur < 0 ? "none" : cur + " (" + BombsFor(_drone)[cur + 1] + ")") +
                ", want slot " + slot + " (" + _bomb + ")");
            if (cur == slot)
            {
                Log("   " + _bomb + " already equipped (green box)");
                return true;
            }

            // sample the resting cell BEFORE clicking so we can prove it actually changed
            int[] before = CellSample(cells[slot].X, cells[slot].Y);

            // Click the cell at its RESTING position. The cell scales up under the cursor, but
            // re-measuring the grid while hovered proved unreliable - it reported cells ~35px
            // away and the click missed (the warhead stayed on the default). The resting centre
            // is correct (verified against a clean screenshot).
            int tx = cells[slot].X, ty = cells[slot].Y;
            FocusRoblox();
            MoveTo(tx, ty);
            Thread.Sleep(120);
            Log("   clicking warhead cell " + _bomb + " at (" + tx + "," + ty + ") (" + t + "/4)");
            if (t == 1)
            {
                // two quick clicks - Roblox often swallows the first one (focus/resync). The
                // second is a bare press since the pointer is already on the cell.
                ClickPrimaryLogged(tx, ty, _bomb);
                Thread.Sleep(45);
                ClickAgain(40);
            }
            else if (t == 2 && _clickKeyOn && _clickVk != 0) ClickLogged(tx, ty, _bomb);
            else { ClickHard(tx, ty); InvalidateOcr(); }
            Thread.Sleep(150);

            // Move off the cell and verify - but move toward the NEXT step (the Deploy As Drone
            // button) instead of parking far away and coming back. The translucent panel lets
            // green terrain show through, so "is it green" alone can false-positive; require the
            // cell to have actually CHANGED colour at its resting position.
            MoveToDeployAsDrone();
            InvalidateGrab();
            Thread.Sleep(80);
            int aW, aH; int[] apx = Grab(out aW, out aH);
            int cur2 = GreenSlot(apx, aW, aH, cells, count);   // did the green box move onto our slot?
            int[] after = CellSample(cells[slot].X, cells[slot].Y);
            int diff = Math.Abs(after[0] - before[0]) + Math.Abs(after[1] - before[1]) + Math.Abs(after[2] - before[2]);
            bool green = cur2 == slot || (diff >= 40 && CellIsGreen(cells[slot].X, cells[slot].Y));
            if (green)
            {
                // Trust the grid + green box: we already know how many warheads the drone has and
                // which name sits in which slot, so the colour change at the measured cell is the
                // proof. The tiny label OCR is only an ENHANCED cross-check around the cell's own
                // coordinates - and it never blocks when it cannot read anything.
                string cellLabel = WarheadCellLabel(cells[slot].X, cells[slot].Y);
                if (cellLabel == "" || cellLabel == _bomb)
                {
                    Log("   " + _bomb + " confirmed (green box" +
                        (cellLabel == "" ? ", label unread" : " + cell reads " + cellLabel) + ")");
                    return true;
                }
                Log("   " + _bomb + " is green but the cell reads \"" + cellLabel + "\" - retrying");
                continue;
            }
            Log("   " + _bomb + " not confirmed (green slot now=" + cur2 + " diff=" + diff + ") - retrying");
        }
        Log("   " + _bomb + " is NOT green - leaving the flow here so we do not deploy the wrong loadout");
        return false;
    }

    // average colour of a small spread inside a warhead cell. Used to prove the cell actually
    // changed when it was clicked (a translucent panel over green terrain can look green even
    // when it is not selected).
    int[] CellSample(int cx, int cy)
    {
        int W, H; int[] px = Grab(out W, out H);
        long r = 0, g = 0, b = 0; int n = 0;
        int[] dxs = new int[] { -34, 0, 34 };
        int[] dys = new int[] { -14, 0, 14 };
        foreach (int dy in dys)
            foreach (int dx in dxs)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                int v = px[y * W + x];
                r += (v >> 16) & 0xFF; g += (v >> 8) & 0xFF; b += v & 0xFF; n++;
            }
        if (n == 0) return new int[] { 0, 0, 0 };
        return new int[] { (int)(r / n), (int)(g / n), (int)(b / n) };
    }

    // Enhanced read of a SMALL area around a known coordinate (a warhead cell's own centre).
    // Crops tight, runs the mask + upscale pass at high scale, then scores the words against
    // every warhead this drone can carry and returns the closest name, or "" if unreadable.
    // This is the "if a whole-screen read fails, enhance just the region we care about" path.
    string WarheadCellLabel(int cx, int cy)
    {
        try
        {
            List<string[]> ws = OcrMaskedRegion(cx - 72, cy - 22, cx + 72, cy + 22, 4);
            if (ws == null || ws.Count == 0)
            {
                ws = OcrMaskedRegion(cx - 72, cy - 22, cx + 72, cy + 22, 6);
                if (ws == null || ws.Count == 0) return "";
            }
            string all = "";
            foreach (string[] w in ws) all += (w[4] ?? "").ToUpperInvariant() + " ";
            string best = null; int bestScore = 0;
            foreach (string b in BombsFor(_drone))
            {
                if (b == "(none)") continue;
                int s = TokenOverlap(all, b);
                if (s > bestScore) { bestScore = s; best = b; }
            }
            Log("   cell (" + cx + "," + cy + ") enhanced read: \"" + all.Trim() + "\"" +
                (bestScore > 0 ? " -> " + best + " (" + bestScore + ")" : " (no match)"));
            return bestScore > 0 ? best : "";
        }
        catch { return ""; }
    }

    // Cross-reference for the warhead: the panel prints "WARHEAD: <name> - <desc>" above the
    // grid. Read that line and check it names the bomb we wanted. Returns TRUE when the line
    // cannot be read (so a bad OCR never blocks), and FALSE only when it IS read and clearly
    // names a different warhead - that is the "false flag" guard.
    bool WarheadDescMatches(string bomb)
    {
        // The label OCR mangles words (e.g. "Light Rocket" -> "Light - s ripped-do M"), so do
        // not demand every word. Instead score the label against EVERY warhead this drone can
        // carry and take the closest one: if that is our bomb we are good, if it is clearly a
        // different one the click hit the wrong cell, and if nothing scores we do not block.
        string label = WarheadLabelText();
        if (label == "") return true;
        string best = null; int bestScore = 0;
        foreach (string b in BombsFor(_drone))
        {
            if (b == "(none)") continue;
            int s = TokenOverlap(label, b);
            if (s > bestScore) { bestScore = s; best = b; }
        }
        Log("   warhead label: \"" + label + "\" -> closest \"" + (best == null ? "-" : best) + "\" (" + bestScore + ")");
        if (bestScore == 0) return true;      // nothing recognisable - do not block
        return best == bomb;
    }

    // The panel prints "WARHEAD: <name> - <desc>". Return that line, or "" when not read.
    string WarheadLabelText()
    {
        try
        {
            foreach (List<string[]> ws in new List<string[]>[] { OcrWords(), OcrWordsWhiten() })
            {
                for (int i = 0; i < ws.Count; i++)
                {
                    string t = (ws[i][4] ?? "").ToUpperInvariant();
                    if (t.IndexOf("WARHEAD") < 0 && t.IndexOf("ARHEAD") < 0 && t.IndexOf("WARHE") < 0) continue;
                    int y;
                    try { y = int.Parse(ws[i][1]); } catch { continue; }
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(t);
                    for (int j = i + 1; j < ws.Count && j < i + 6; j++)
                    {
                        int wy;
                        try { wy = int.Parse(ws[j][1]); } catch { break; }
                        if (Math.Abs(wy - y) > 14) break;
                        sb.Append(' ').Append(ws[j][4] ?? "");
                    }
                    return sb.ToString();
                }
            }
        }
        catch { }
        return "";
    }

    static int TokenOverlap(string label, string bomb)
    {
        string[] lw = label.ToUpperInvariant().Split(' ');
        int hit = 0;
        foreach (string p in bomb.ToUpperInvariant().Split(' '))
        {
            if (p.Length < 2) continue;
            foreach (string w in lw)
            {
                if (w.Length < 2) continue;
                if (w.Contains(p) || p.Contains(w) || SimPct(w, p) >= 70) { hit++; break; }
            }
        }
        return hit;
    }

    // The TEAM BASE panel ALWAYS stacks its buttons above the warhead grid:
    //   TEAM BASE / Deploy / Return / "Deploy As Drone" / WARHEAD line / the warhead grid.
    // So the "Deploy As Drone" button is a dependable anchor - once we can SEE it, we know the
    // cells sit directly under it, left-to-right then wrapping, at a fixed pixel size. That is
    // deterministic and does not care what terrain shows through the translucent cells (which is
    // exactly what made the old grey-box hunt come back with 0 cells, or the wrong column, over
    // green ground). Colour is then only used to tell WHICH slot is the selected (green) one.
    List<Point> FindWarheadCells(int count)
    {
        float sc = Screen.PrimaryScreen.Bounds.Height / 1080f;
        if (sc <= 0f) sc = 1f;

        int ax, ay;
        if (!WarheadAnchor(out ax, out ay))
        {
            Log("   no 'Deploy As Drone' anchor on screen - falling back to the colour hunt");
            return FindWarheadCellsByColour(count);
        }

        int colStep = (int)(106 * sc), rowStep = (int)(52 * sc);
        int col0 = ax - colStep;

        // Auto-fit the vertical offset: the cells are dark boxes (the selected one is green), so
        // slide the grid down and keep the offset that lands on the most cell-coloured patches.
        // This absorbs whatever the game's panel spacing really is at this resolution, instead of
        // trusting a single hard-coded number.
        int W, H; int[] px = Grab(out W, out H);
        int baseOff = 85 + _whRowNudge;
        int bestOff = baseOff, bestHits = -1;
        // Wide range on purpose: the grid can start anywhere from ~15px to ~150px below the
        // anchor text, and if we only searched the lower part we locked onto the SECOND row and
        // everything was off by one slot (that is what left the default warhead in place).
        for (int off = 15; off <= 150; off += 5)
        {
            int hits = 0;
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 3; c++)
                {
                    if (r * 3 + c >= count) continue;
                    int cx = col0 + c * colStep;
                    int cy = ay + (int)((off + r * 52) * sc);
                    if (LooksLikeCell(px, W, H, cx, cy)) hits++;
                }
            if (hits > bestHits || (hits == bestHits && Math.Abs(off - baseOff) < Math.Abs(bestOff - baseOff)))
            { bestHits = hits; bestOff = off; }
        }
        int row0 = ay + (int)(bestOff * sc);

        List<Point> grid = new List<Point>();
        for (int r = 0; grid.Count < count && r < 8; r++)
            for (int c = 0; c < 3 && grid.Count < count; c++)
                grid.Add(new Point(col0 + c * colStep, row0 + r * rowStep));
        Log("   warhead grid under Deploy As Drone (" + ax + "," + ay + "): fitted offset " + bestOff +
            " (cell hits " + bestHits + "/" + count + "), col0 " + col0 + ", step " + colStep + "x" + rowStep);
        return grid;
    }

    // Does a small patch centred at (x,y) look like a warhead cell? Unselected cells are dark,
    // low-saturation boxes; the selected one is green. Used to auto-fit the grid's y position.
    bool LooksLikeCell(int[] px, int W, int H, int x, int y)
    {
        int cell = 0, n = 0;
        int[] dxs = new int[] { -30, 0, 30 };
        int[] dys = new int[] { -12, 0, 12 };
        foreach (int dy in dys)
            foreach (int dx in dxs)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                int v = px[yy * W + xx];
                int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                // real capture: selected cell ~R75 G126 B50; unselected cells are neutral grey
                // (~R114 G115 B113 and ~R45 G46 B43). Terrain is tinted, so low saturation is the
                // real tell for an unselected cell.
                bool green = (g - Math.Max(r, b)) >= 30 && g >= 95;
                bool greyCell = lum <= 132 && (mx - mn) <= 32;
                if (green || greyCell) cell++;
                n++;
            }
        return n > 0 && cell * 2 >= n;
    }

    // Find the "Deploy As Drone" button (the lowest match, so the nav-bar DEPLOY is ignored) and
    // return its centre - the anchor the whole warhead grid hangs off.
    bool WarheadAnchor(out int ax, out int ay)
    {
        ax = 0; ay = 0;
        List<Hit> h = FindPhraseAll("Deploy As Drone", null, OcrWords());
        if (h.Count == 0) h = FindPhraseAll("Deploy As Drone", null, OcrWordsWhiten());
        if (h.Count == 0) h = FindPhraseAll("Deploy As", null, OcrWords());
        if (h.Count == 0) h = FindPhraseAll("Deploy As", null, OcrWordsWhiten());
        if (h.Count == 0) h = FindPhraseAll("Drone", null, OcrWords());
        if (h.Count == 0) return false;
        int best = 0;
        for (int i = 1; i < h.Count; i++) if (h[i].Y > h[best].Y) best = i;   // lowest = the button
        ax = h[best].X; ay = h[best].Y;
        return true;
    }

    static int _whRowNudge = 0;   // per-run y calibration (px @1080); nudged when a click misses

    List<Point> FindWarheadCellsByColour(int count)
    {
        int W, H; int[] px = Grab(out W, out H);
        bool[] grey = new bool[W * H];
        for (int i = 0; i < px.Length; i++)
        {
            int v = px[i];
            int b = v & 0xFF, gg = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
            int mx = Math.Max(r, Math.Max(gg, b)), mn = Math.Min(r, Math.Min(gg, b));
            grey[i] = (mx - mn) <= 14 && r >= 45 && r <= 115;
        }

        List<Point> found = new List<Point>();
        CollectCells(grey, W, H, found);
        if (found.Count < 2) return new List<Point>();

        List<int> xs = Cluster(found, true);
        List<int> ys = Cluster(found, false);
        if (xs.Count < 1 || ys.Count < 1) return new List<Point>();

        // The equipped warhead box is green, so it drops out of the grey set and the grid comes
        // back short - e.g. when the lone bottom-row cell is the selected one, only the top row
        // is seen and ys has a single entry. Rebuild the full grid by extrapolating the missing
        // rows from the detected spacing, so the green slot still gets a usable coordinate.
        int rowSpacing = _whRowSpacing;
        if (ys.Count >= 2)
        {
            rowSpacing = (ys[ys.Count - 1] - ys[0]) / (ys.Count - 1);
            if (rowSpacing > 0) _whRowSpacing = rowSpacing;
        }

        List<Point> grid = new List<Point>();
        int row = 0;
        while (grid.Count < count && row < 8)
        {
            int y = row < ys.Count ? ys[row] : ys[ys.Count - 1] + rowSpacing * (row - ys.Count + 1);
            for (int c = 0; c < xs.Count && grid.Count < count; c++)
                grid.Add(new Point(xs[c], y));
            row++;
        }
        return grid;
    }

    static int _whRowSpacing = 52;   // learnt from a two-row grid, reused when only one row shows

    // collapse near-equal coordinates into single averaged centres
    static List<int> Cluster(List<Point> pts, bool byX)
    {
        List<int> vals = new List<int>();
        foreach (Point p in pts) vals.Add(byX ? p.X : p.Y);
        vals.Sort();
        List<int> centres = new List<int>();
        int i = 0;
        while (i < vals.Count)
        {
            int sum = 0, n = 0, first = vals[i];
            while (i < vals.Count && vals[i] - first <= 35) { sum += vals[i]; n++; i++; }
            centres.Add(sum / n);
        }
        return centres;
    }

    void CollectCells(bool[] m, int W, int H, List<Point> outCells)
    {
        bool[] seen = new bool[W * H];
        int[] stack = new int[W * H];
        for (int i = 0; i < m.Length; i++)
        {
            if (!m[i] || seen[i]) continue;
            int sp = 0; stack[sp++] = i; seen[i] = true;
            int x0 = i % W, y0 = i / W, x1 = x0, y1 = y0, area = 0;
            while (sp > 0)
            {
                int p = stack[--sp]; area++;
                int x = p % W, y = p / W;
                if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y;
                if (x > 0 && m[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack[sp++] = p - 1; }
                if (x < W - 1 && m[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack[sp++] = p + 1; }
                if (y > 0 && m[p - W] && !seen[p - W]) { seen[p - W] = true; stack[sp++] = p - W; }
                if (y < H - 1 && m[p + W] && !seen[p + W]) { seen[p + W] = true; stack[sp++] = p + W; }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bw < 60 || bw > 160) continue;
            if (bh < 30 || bh > 80) continue;
            double ar = (double)bw / bh;
            if (ar < 1.3 || ar > 3.0) continue;
            if ((double)area / (bw * bh) < 0.55) continue;
            outCells.Add(new Point((x0 + x1) / 2, (y0 + y1) / 2));
        }
    }

    // Fraction of samples inside a cell that are STRONGLY green. Measured from a real capture:
    // the selected warhead overlay reads about R75 G126 B50 (g-max(r,b) ~ 50, g ~ 126), while an
    // unselected cell is neutral grey (R114 G115 B113) and terrain showing through is darker and
    // mottled. So we count only bright, saturated green - a green-looking patch of grass does not
    // score 1.0 the way the real overlay does.
    float CellGreenFrac(int[] px, int W, int H, int cx, int cy)
    {
        int green = 0, n = 0;
        int[] dxs = new int[] { -34, 0, 34 };
        int[] dys = new int[] { -14, 0, 14 };
        foreach (int dy in dys)
            foreach (int dx in dxs)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                int v = px[y * W + x];
                int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                if ((g - Math.Max(r, b)) >= 30 && g >= 95) green++;
                n++;
            }
        return n > 0 ? (float)green / n : 0f;
    }

    // Which warhead slot is the green selected one? Returns the slot index, or -1 when no single
    // cell clearly stands out (so the caller clicks and verifies instead of assuming). We require
    // BOTH a strong score and a clear margin over the runner-up, so terrain can never fake it.
    int GreenSlot(int[] px, int W, int H, List<Point> cells, int count)
    {
        float best = -1f, second = -1f; int bi = -1;
        for (int i = 0; i < count && i < cells.Count; i++)
        {
            float v = CellGreenFrac(px, W, H, cells[i].X, cells[i].Y);
            if (v > best) { second = best; best = v; bi = i; }
            else if (v > second) second = v;
        }
        if (bi >= 0 && best >= 0.55f && (best - second) >= 0.25f) return bi;
        return -1;
    }

    // The equipped warhead box is drawn green. Sample a 3x3 of points inside the cell so a
    // run of label text through the middle cannot hide the colour.
    bool CellIsGreen(int cx, int cy)
    {
        int W, H; int[] px = Grab(out W, out H);
        int green = 0, total = 0;
        int[] dxs = new int[] { -34, 0, 34 };
        int[] dys = new int[] { -14, 0, 14 };
        foreach (int dy in dys)
            foreach (int dx in dxs)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                total++;
                int v = px[y * W + x];
                int b = v & 0xFF, gg = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                if ((gg - r) >= 18 && (gg - b) >= 18 && gg >= 60) green++;
            }
        return total > 0 && green * 2 >= total;
    }

    // ================= OCR =================
    // OCR shells out to PowerShell (~1s). A single step often asks for the same screen
    // several times, so cache the result briefly and invalidate it after any click.
    List<string[]> _ocrCache = null;
    DateTime _ocrTime = DateTime.MinValue;
    static int _ocrShotSeq = 0;

    void InvalidateOcr()
    {
        _ocrCache = null;
        _ocrTime = DateTime.MinValue;
        _wOcrCache = null;
        _wOcrTime = DateTime.MinValue;
        _grabCache = null;
        _grabTime = DateTime.MinValue;
    }

    List<string[]> OcrWords()
    {
        if (_ocrCache != null && (DateTime.Now - _ocrTime).TotalMilliseconds < 250)
            return _ocrCache;

        List<string[]> words = OcrWordsRaw();
        _ocrCache = words;
        _ocrTime = DateTime.Now;
        return words;
    }

    // A second, independent read for light-on-dark text the plain pass misses. Keep only
    // near-white pixels (black), drop everything else (white), then scale the image up:
    // Windows OCR then reads "Base" and "TEAM BASE" where the untouched screen read nothing.
    // Coordinates are scaled back to screen pixels so the hits stay clickable.
    List<string[]> _wOcrCache = null;
    DateTime _wOcrTime = DateTime.MinValue;
    const int WhitenScale = 2;

    List<string[]> OcrWordsWhiten()
    {
        if (_wOcrCache != null && (DateTime.Now - _wOcrTime).TotalMilliseconds < 250)
            return _wOcrCache;
        List<string[]> words = OcrWordsWhitenRaw();
        _wOcrCache = words;
        _wOcrTime = DateTime.Now;
        return words;
    }

    List<string[]> OcrWordsWhitenRaw()
    {
        List<string[]> words = new List<string[]>();
        string shot = null;
        try
        {
            shot = Path.Combine(Path.GetTempPath(),
                "robloxauto-wn-" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                "-" + (++_ocrShotSeq) + ".bmp");

            int w, h;
            int[] px = Grab(out w, out h);
            int W = w * WhitenScale, H = h * WhitenScale;
            int[] outPx = new int[W * H];
            int black = unchecked((int)0xFF000000), white = unchecked((int)0xFFFFFFFF);
            for (int y = 0; y < h; y++)
            {
                int oy = y * WhitenScale;
                for (int x = 0; x < w; x++)
                {
                    int v = px[y * w + x];
                    int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                    int c = (r > 195 && g > 195 && b > 195) ? black : white;
                    int ox = x * WhitenScale;
                    for (int dy = 0; dy < WhitenScale; dy++)
                    {
                        int row = (oy + dy) * W + ox;
                        for (int dx = 0; dx < WhitenScale; dx++) outPx[row + dx] = c;
                    }
                }
            }

            using (Bitmap bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(
                    new Rectangle(0, 0, W, H),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(outPx, 0, bd.Scan0, outPx.Length);
                bmp.UnlockBits(bd);
                bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Bmp);
            }

            List<string[]> raw = OcrServerRead(shot);
            if (raw == null) return words;
            foreach (string[] p in raw)
            {
                try
                {
                    for (int k = 0; k < 4; k++)
                        p[k] = (int.Parse(p[k]) / WhitenScale).ToString();
                }
                catch { }
                words.Add(p);
            }
        }
        catch (Exception e) { Log("whiten OCR failed: " + e.Message); }
        finally { try { if (shot != null && File.Exists(shot)) File.Delete(shot); } catch { } }
        return words;
    }

    bool PhraseOnScreenWhiten(string phrase)
    {
        return FindPhraseAll(phrase, null, OcrWordsWhiten()).Count > 0;
    }

    // ---- resident OCR helper ----
    // Spawning PowerShell costs ~600ms per read, and the sequence reads many times. Keep
    // one helper alive and feed it image paths over stdin; it answers with words and
    // "===END===". If it ever dies or stalls, fall back to a one-shot spawn so the
    // sequence keeps working instead of failing.
    Process _ocrProc = null;
    readonly object _ocrLock = new object();

    static bool SafeExited(Process p) { try { return p.HasExited; } catch { return true; } }

    void KillOcrServer()
    {
        try { if (_ocrProc != null && !SafeExited(_ocrProc)) _ocrProc.Kill(); } catch { }
        _ocrProc = null;
    }

    Process OcrServer()
    {
        if (_ocrProc != null && !SafeExited(_ocrProc)) return _ocrProc;
        try
        {
            string ps = Path.Combine(_appDir, "ocr.ps1");
            if (!File.Exists(ps)) return null;
            ProcessStartInfo si = new ProcessStartInfo("powershell",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\" -Server");
            si.UseShellExecute = false;
            si.RedirectStandardInput = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            si.CreateNoWindow = true;
            _ocrProc = Process.Start(si);
            Log("OCR helper started once and stays resident - no per-read PowerShell startup");
            return _ocrProc;
        }
        catch (Exception e) { Log("OCR helper failed to start: " + e.Message); _ocrProc = null; return null; }
    }

    static string ReadLineTimeout(StreamReader r, int ms)
    {
        System.Threading.Tasks.Task<string> t = r.ReadLineAsync();
        if (!t.Wait(ms)) return null;      // stalled - caller restarts the helper
        try { return t.Result; } catch { return null; }
    }

    // returns null when the helper is unavailable so the caller can fall back
    List<string[]> OcrServerRead(string shot)
    {
        lock (_ocrLock)
        {
            Process p = OcrServer();
            if (p == null) return null;
            try
            {
                p.StandardInput.WriteLine(shot);
                p.StandardInput.Flush();

                List<string[]> words = new List<string[]>();
                while (true)
                {
                    string line = ReadLineTimeout(p.StandardOutput, 8000);
                    if (line == null) { Log("OCR helper stalled - restarting it"); KillOcrServer(); return null; }
                    line = line.Trim();
                    if (line == "===END===") return words;
                    if (line.Length == 0 || line == "===READY===") continue;
                    if (line == "===ERROR===") { KillOcrServer(); return null; }
                    string[] parts = line.Split('|');
                    if (parts.Length >= 5) words.Add(parts);
                }
            }
            catch (Exception e) { Log("OCR helper read failed: " + e.Message); KillOcrServer(); return null; }
        }
    }

    List<string[]> OcrWordsRaw()
    {
        List<string[]> words = new List<string[]>();
        string shot = null;
        try
        {
            // A unique file per call. A fixed name raced when an AUTO restart left two
            // threads reading/writing it at once, which is what produced
            // "OCR failed: A generic error occurred in GDI+".
            // BMP, not PNG: encoding the full-screen photo to PNG costs ~180ms, BMP ~65ms, and
            // the helper decodes uncompressed BMP faster too. The file is bigger but transient.
            shot = Path.Combine(Path.GetTempPath(),
                "robloxauto-ocr-" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                "-" + (++_ocrShotSeq) + ".bmp");

            // reuse the screen we already grabbed for blob/change detection instead of
            // capturing a second time - cheaper and it cannot tear mid-transition
            int w, h;
            int[] px = Grab(out w, out h);
            using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(
                    new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
                bmp.UnlockBits(bd);
                bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Bmp);
            }

            // resident helper first - avoids ~600ms of PowerShell startup per read
            List<string[]> fast = OcrServerRead(shot);
            if (fast != null) return fast;

            Log("OCR helper unavailable - falling back to a one-shot read");
            string ps = Path.Combine(_appDir, "ocr.ps1");
            if (!File.Exists(ps)) { Log("ocr.ps1 is missing next to the exe"); return words; }

            ProcessStartInfo si = new ProcessStartInfo("powershell",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\" \"" + shot + "\"");
            si.UseShellExecute = false;
            si.RedirectStandardOutput = true;
            si.CreateNoWindow = true;

            using (Process pr = Process.Start(si))
            {
                System.Threading.Tasks.Task<string> reader = pr.StandardOutput.ReadToEndAsync();
                // hard timeout - never leave a PowerShell child behind
                if (!pr.WaitForExit(20000))
                {
                    try { pr.Kill(); } catch { }
                    try { pr.WaitForExit(3000); } catch { }
                    Log("OCR timed out - child killed");
                    return words;
                }
                string all = "";
                try { all = reader.Result; } catch { }
                foreach (string line in all.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.Length == 0) continue;
                    string[] p = l.Split('|');
                    if (p.Length >= 5) words.Add(p);
                }
            }
        }
        catch (Exception e) { Log("OCR failed: " + e.Message); }
        finally
        {
            // do not leave a PNG behind for every single read
            try { if (shot != null && File.Exists(shot)) File.Delete(shot); } catch { }
        }
        return words;
    }

    // ================= screen helpers =================
    static int[] _grabCache = null;
    static int _grabW = 0, _grabH = 0;
    static DateTime _grabTime = DateTime.MinValue;

    static int[] Grab(out int w, out int h)
    {
        // the blob search and the loading-screen check both grab the whole screen and
        // usually run back to back - reuse a capture that is at most 250ms old
        if (_grabCache != null && (DateTime.Now - _grabTime).TotalMilliseconds < 250)
        {
            w = _grabW; h = _grabH;
            return _grabCache;
        }

        Rectangle b = Screen.PrimaryScreen.Bounds;
        w = b.Width; h = b.Height;
        using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int[] px = new int[w * h];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
            _grabCache = px; _grabW = w; _grabH = h; _grabTime = DateTime.Now;
            return px;
        }
    }

    // save the current screen next to the exe, for diagnosing a mis-detected panel
    void SaveGrab(string name)
    {
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            string dir = System.Windows.Forms.Application.StartupPath;
            using (Bitmap bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
                bmp.UnlockBits(bd);
                bmp.Save(Path.Combine(dir, "debug_" + name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch { }
    }

    // the join screen is dominated by one flat colour; in-game it is varied
    static bool IsLoadingScreen()
    {
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            const int STEP = 32;
            Dictionary<int, int> buckets = new Dictionary<int, int>();
            int total = 0;
            for (int y = STEP / 2; y < H; y += STEP)
                for (int x = STEP / 2; x < W; x += STEP)
                {
                    int c = px[y * W + x];
                    int key = ((((c >> 16) & 0xFF) >> 4) << 8) | ((((c >> 8) & 0xFF) >> 4) << 4) | (((c & 0xFF) >> 4));
                    int n; buckets.TryGetValue(key, out n);
                    buckets[key] = n + 1; total++;
                }
            if (total == 0) return false;
            int best = 0;
            foreach (int v in buckets.Values) if (v > best) best = v;
            return ((double)best / total) * 100.0 >= 55.0;
        }
        catch { return false; }
    }

    static bool FindBlob(Func<int, int, int, bool> match, int minCells, out int cx, out int cy)
    {
        cx = cy = -1;
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            const int STEP = 4;
            int gw = W / STEP, gh = H / STEP;
            bool[] mask = new bool[gw * gh];
            for (int j = 0; j < gh; j++)
                for (int i = 0; i < gw; i++)
                {
                    int c = px[(j * STEP) * W + (i * STEP)];
                    if (match((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF)) mask[j * gw + i] = true;
                }
            bool[] seen = new bool[gw * gh];
            int[] stack = new int[gw * gh];
            int bestCount = 0; long bx = 0, by = 0;
            for (int idx = 0; idx < mask.Length; idx++)
            {
                if (!mask[idx] || seen[idx]) continue;
                int sp = 0; stack[sp++] = idx; seen[idx] = true;
                int count = 0; long sx = 0, sy = 0;
                while (sp > 0)
                {
                    int cur = stack[--sp];
                    int cxx = cur % gw, cyy = cur / gw;
                    count++; sx += cxx; sy += cyy;
                    if (cxx > 0 && mask[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack[sp++] = cur - 1; }
                    if (cxx < gw - 1 && mask[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack[sp++] = cur + 1; }
                    if (cyy > 0 && mask[cur - gw] && !seen[cur - gw]) { seen[cur - gw] = true; stack[sp++] = cur - gw; }
                    if (cyy < gh - 1 && mask[cur + gw] && !seen[cur + gw]) { seen[cur + gw] = true; stack[sp++] = cur + gw; }
                }
                if (count > bestCount) { bestCount = count; bx = sx / count; by = sy / count; }
            }
            if (bestCount < minCells) return false;
            cx = (int)(bx * STEP + STEP / 2);
            cy = (int)(by * STEP + STEP / 2);
            return true;
        }
        catch { return false; }
    }

    // NOTE: there is deliberately no red-blob "find the tent" helper any more. The red
    // POINT D/E/F pins match the same colour test as the red Base tent, so blob matching
    // clicked a control point instead of the drone spawn. The "Base" label is the anchor.

    // ================= screen-change (false-flag) detection =================
    // A click can be accepted by the OS and still do nothing: the game lagged, the element
    // moved, or it was never a button to begin with. Compare a coarse grid of the screen
    // before and after so a wasted click is detected instead of hammered forever.
    const int SIG_W = 48, SIG_H = 27;

    static void InvalidateGrab() { _grabCache = null; _grabTime = DateTime.MinValue; }

    static int[] Signature()
    {
        int w, h;
        int[] px = Grab(out w, out h);
        int[] sig = new int[SIG_W * SIG_H];
        int bw = Math.Max(1, w / SIG_W), bh = Math.Max(1, h / SIG_H);
        for (int y = 0; y < SIG_H; y++)
        {
            int sy = y * bh + bh / 2;
            for (int x = 0; x < SIG_W; x++)
            {
                int sx = x * bw + bw / 2;
                int sum = 0, n = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int yy = sy + dy; if (yy < 0 || yy >= h) continue;
                    int row = yy * w;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = sx + dx; if (xx < 0 || xx >= w) continue;
                        int c = px[row + xx];
                        sum += ((c >> 16) & 0xFF) + ((c >> 8) & 0xFF) + (c & 0xFF);
                        n++;
                    }
                }
                sig[y * SIG_W + x] = n == 0 ? 0 : sum / n;
            }
        }
        return sig;
    }

    // percentage of grid cells that changed by more than the noise floor
    static double DiffPct(int[] a, int[] b)
    {
        if (a == null || b == null) return 100;
        int n = Math.Min(a.Length, b.Length), changed = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(a[i] - b[i]) > 18) changed++;
        return n == 0 ? 100 : changed * 100.0 / n;
    }

    // wait until the screen has moved at least pct percent away from "before"
    bool WaitChange(int[] before, double pct, int ms, int g)
    {
        int spent = 0;
        while (Alive(g) && spent < ms)
        {
            Thread.Sleep(90); spent += 90;
            InvalidateOcr();                    // fresh pixels AND a fresh OCR read next loop
            if (DiffPct(before, Signature()) >= pct) return true;
        }
        return false;
    }

    // click, then confirm the screen actually reacted. Lag usually clears by the second or
    // third attempt, so each retry waits a little longer and settles a little more.
    bool ClickVerify(int x, int y, string what, int tries, int g)
    {
        for (int t = 1; t <= tries && Alive(g); t++)
        {
            int[] before = Signature();
            if (t == 1)
                ClickPrimaryLogged(x, y, what);
            else
                ClickLogged(x, y, what);
            if (WaitChange(before, 2.0, 500 + t * 300, g)) return true;
            Log("   no screen change after \"" + what + "\" - false flag (" + t + "/" + tries + ")");
            Thread.Sleep(90 * t);
        }
        return false;
    }
    // returns how many of the 3 SendInput calls were accepted (3 = all good, 0 = rejected)
    static int ClickAt(int x, int y) { return ClickAt(x, y, 70); }

    static readonly Random _rng = new Random();

    // Glide the cursor from wherever it is to the target in small, jittered steps instead of
    // one SetCursorPos teleport. A single jump does not generate the intermediate mouse-move
    // events the game needs to update its hover/highlight, so clicks could land on the old
    // target. Moving from the CURRENT position also passes the pointer over the marker, which
    // is what highlights it before the press.
    static void HumanMove(int tx, int ty)
    {
        Point cur;
        try { cur = Cursor.Position; } catch { cur = new Point(tx, ty); }
        int steps = 6 + _rng.Next(0, 5);
        for (int i = 1; i <= steps; i++)
        {
            SetCursorPos(cur.X + (tx - cur.X) * i / steps + _rng.Next(-2, 3),
                         cur.Y + (ty - cur.Y) * i / steps + _rng.Next(-2, 3));
            Thread.Sleep(_rng.Next(6, 14));
        }
        SetCursorPos(tx, ty);
        Thread.Sleep(_rng.Next(20, 40));
    }

    static Point _lastCursor = Point.Empty;

    // Optional hardware click: send a keyboard key that a Razer macro turns into a real
    // left click. The game accepts that as genuine input where an injected SendInput click
    // is sometimes ignored. 0 disables it (fall back to the injected mouse click).
    static ushort _clickVk = 0x30;      // '0'
    static bool _clickKeyOn = true;

    // Roblox only arms a button once the pointer has hovered it for a moment. After gliding
    // onto the target, nudge a pixel to force a fresh hover and wait this long before the
    // press so the button is actually live when the click fires.
    //
    // Kept short for a fluid run and raised automatically whenever a click is missed (see
    // BumpHover), so a fast machine stays snappy and a laggy one self-corrects.
    static int _hoverBase = 150;        // configured start (settings "hoverMs")
    static int _hoverMs = 300;          // live value used by ClickAt
    static int _hoverMax = 1600;
    static void BumpHover() { if (_hoverMs < _hoverMax) { _hoverMs = Math.Min(_hoverMax, _hoverMs + 300); } }

    static void SendKey(ushort vk)
    {
        int cb = Marshal.SizeOf(typeof(INPUT));
        INPUT[] kd = new INPUT[1]; kd[0].type = IN_KEY; kd[0].U.ki.vk = vk;
        SendInput(1, kd, cb);
        Thread.Sleep(40);
        INPUT[] ku = new INPUT[1]; ku[0].type = IN_KEY; ku[0].U.ki.vk = vk; ku[0].U.ki.flags = KEYUP;
        SendInput(1, ku, cb);
    }

    // "0" -> VK 0x30, "off"/"" -> disabled, "0xNN" -> raw VK
    static ushort ParseVk(string s)
    {
        if (s == null) return 0;
        s = s.Trim();
        if (s.Length == 0 || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            int v;
            if (int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v)) return (ushort)v;
        }
        return (ushort)char.ToUpperInvariant(s[0]);
    }

    // Send a relative mouse MOVE. Unlike SetCursorPos this raises raw input, which is what
    // makes Roblox re-read the real cursor position before a click lands.
    static void NudgeRaw(int dx, int dy)
    {
        try
        {
            int cb = Marshal.SizeOf(typeof(INPUT));
            INPUT[] mv = new INPUT[1];
            mv[0].type = IN_MOUSE;
            mv[0].U.mi.flags = MV_MOVE;     // MOUSEEVENTF_MOVE (raw input)
            mv[0].U.mi.dx = dx; mv[0].U.mi.dy = dy;
            SendInput(1, mv, cb);
            Thread.Sleep(15);
        }
        catch { }
    }

    // A click the game ignores is almost always focus or timing. Put the REAL cursor there
    // first (some games read the OS cursor, not the injected move), hold the button longer,
    // and fall back to mouse_event when SendInput is refused - that is the "forceful" path.
    static int ClickAt(int x, int y, int holdMs)
    {
        FocusRoblox();

        HumanMove(x, y);                        // glide in so hover/highlight updates first
        try { _lastCursor = Cursor.Position; } catch { }

        // Roblox only resyncs its internal pointer when it sees a REAL raw mouse-move, which
        // is why a physical bump makes an otherwise-ignored click land. Nudge with a genuine
        // relative MOVE (raw input), step aside, come back, then sit still so the button
        // arms before we press.
        NudgeRaw(3, 2);
        try { SetCursorPos(x + (_rng.Next(0, 2) == 0 ? -3 : 3), y); Thread.Sleep(20); SetCursorPos(x, y); } catch { }
        NudgeRaw(-2, -1);
        try { SetCursorPos(x, y); } catch { }
        if (_hoverMs > 0) Thread.Sleep(_hoverMs);

        int cb = Marshal.SizeOf(typeof(INPUT));

        // NO MOUSEEVENTF_MOVE / ABSOLUTE here on purpose. Absolute coordinates are
        // normalised against the WHOLE VIRTUAL DESKTOP, but this machine has three monitors
        // (virtual 4920x2036 starting at X=-1920), so normalising against the primary
        // screen put every click on the wrong monitor - it "did nothing" or hit the Roblox
        // menu. SetCursorPos already places the cursor exactly, so click where it is.
        INPUT[] dn = new INPUT[1];
        dn[0].type = IN_MOUSE;
        dn[0].U.mi.flags = MV_LDOWN;
        uint r2 = SendInput(1, dn, cb);
        Thread.Sleep(holdMs);

        INPUT[] up = new INPUT[1];
        up[0].type = IN_MOUSE;
        up[0].U.mi.flags = MV_LUP;
        uint r3 = SendInput(1, up, cb);

        if (r2 == 0 || r3 == 0)
        {
            // the older input path is not blocked by the UIPI checks that can eat SendInput
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(holdMs);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
        return (int)(r2 + r3);
    }

    // Fast second press for the "double click" pattern: the pointer is already on the target
    // from the first click, so skip focus/glide/nudge/hover and just press. Saves ~0.5s per
    // double click and the flow is full of them.
    static int ClickAgain(int holdMs)
    {
        int cb = Marshal.SizeOf(typeof(INPUT));
        INPUT[] dn = new INPUT[1]; dn[0].type = IN_MOUSE; dn[0].U.mi.flags = MV_LDOWN;
        uint r1 = SendInput(1, dn, cb);
        Thread.Sleep(holdMs);
        INPUT[] up = new INPUT[1]; up[0].type = IN_MOUSE; up[0].U.mi.flags = MV_LUP;
        uint r2 = SendInput(1, up, cb);
        if (r1 == 0 || r2 == 0)
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(holdMs);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
        return (int)(r1 + r2);
    }
    static int ClickAgain() { return ClickAgain(40); }

    // a deliberately heavier press for when the normal click is being ignored
    static int ClickHard(int x, int y)
    {
        FocusRoblox();
        return ClickAt(x, y, 190);
    }

    // Backup click: fire the keyboard key a Razer macro turns into a real hardware click.
    // Used only when the normal injected click did not change the screen.
    int ClickMacroLogged(int x, int y, string what)
    {
        if (!_clickKeyOn || _clickVk == 0) return 0;
        FocusRoblox();
        HumanMove(x, y);
        try { _lastCursor = Cursor.Position; } catch { }
        SendKey(_clickVk);
        InvalidateOcr();
        Log("   macro click " + what + " (" + x + "," + y + ")  key=" + _clickKeyText);
        return 2;
    }

    // Preferred click: the macro key (real hardware click) when enabled, else the injected
    // click. The injected click is still logged so failures show up in the log.
    int ClickPrimaryLogged(int x, int y, string what)
    {
        if (_clickKeyOn && _clickVk != 0) return ClickMacroLogged(x, y, what);
        return ClickLogged(x, y, what);
    }

    int ClickLogged(int x, int y, string what)
    {
        // if our own always-on-top window sits over the point, the click lands on US
        bool overUs = false;
        try { overUs = Bounds.Contains(x, y); } catch { }

        int n = ClickAt(x, y);
        InvalidateOcr();          // the screen changed - force the next check to re-read

        // focus is the thing that makes Roblox ignore a perfectly aimed click, and the
        // actual cursor position tells us whether the move landed where we asked
        IntPtr rw = RobloxWindow();
        IntPtr fg = GetForegroundWindow();
        string focus = rw == IntPtr.Zero ? "ROBLOX WINDOW NOT FOUND"
                     : (fg == rw ? "roblox is foreground" : "roblox NOT foreground (fg=0x" + fg.ToString("X") + ")");

        Log("   click " + what + " (" + x + "," + y + ")  accepted=" + n + "/2" +
            "   cursor=(" + _lastCursor.X + "," + _lastCursor.Y + ")   " + focus +
            (n == 0 ? "   <-- SendInput REJECTED (input blocked?)" : "") +
            (overUs ? "   <-- OUR PANEL IS OVER THIS POINT, move the panel!" : ""));
        return n;
    }

    // mouse wheel: negative = zoom out
    // the mouse wheel goes to the window UNDER THE CURSOR, so park the cursor over the game
    // before scrolling or the wheel scrolls our own panel instead
    // every deliberate move glides (HumanMove) - a teleport with SetCursorPos does not raise
    // the raw move events the game needs, so the pointer would arrive "without moving"
    static void MoveTo(int x, int y) { HumanMove(x, y); }

    // find a spot that is over the game and NOT under our own window, then park there
    void MoveOverGame()
    {
        Rectangle pb = Screen.PrimaryScreen.Bounds;
        int[] xs = { pb.Width / 2, (int)(pb.Width * 0.72), (int)(pb.Width * 0.28) };
        int[] ys = { pb.Height / 2, (int)(pb.Height * 0.38) };
        foreach (int x in xs)
            foreach (int y in ys)
                if (!Bounds.Contains(x, y)) { MoveTo(x, y); return; }
        MoveTo(pb.Width / 2, pb.Height / 2);
    }

    // Drift the pointer gently around the middle of the map while zooming. A frozen cursor can
    // make the game ignore the wheel, and the soft motion keeps real movement flowing and lets
    // the Base marker sweep under the cursor as the view expands. Steps alternate direction.
    void MoveOverGameSoft(int step)
    {
        Rectangle pb = Screen.PrimaryScreen.Bounds;
        int cx = pb.Width / 2, cy = pb.Height / 2;
        int sx = (step % 2 == 0) ? 1 : -1;
        int sy = (step % 3 == 0) ? 1 : -1;
        int x = cx + sx * (40 + (step * 37) % 160);
        int y = cy + sy * (30 + (step * 23) % 120);
        if (Bounds.Contains(x, y)) { x = cx - sx * 90; y = cy - sy * 70; }
        MoveTo(x, y);
    }

    // Park the pointer somewhere over the map that is nowhere near the centred TEAM BASE
    // panel. Warhead cells scale up under the cursor, which shifts the whole grid, so the
    // grid must be measured and verified with the pointer away from it.
    void ParkOffPanel()
    {
        Rectangle pb = Screen.PrimaryScreen.Bounds;
        int px = (int)(pb.Width * 0.10), py = (int)(pb.Height * 0.28);
        if (Bounds.Contains(px, py)) px = (int)(pb.Width * 0.90);   // don't park under our panel
        MoveTo(px, py);
    }

    // Move off the warhead grid and onto the button we need next anyway - saves the wasted trip
    // to the far park position and back. Falls back to ParkOffPanel if the button is not read.
    void MoveToDeployAsDrone()
    {
        List<Hit> dep = _asDrone ? FindPhraseAll("Deploy As Drone") : FindPhraseAll("Deploy", "As");
        if (dep.Count > 0) { MoveTo(dep[0].X, dep[0].Y); return; }
        ParkOffPanel();
    }

    static void ScrollOut(int notches)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = IN_MOUSE;
        inp[0].U.mi.data = (uint)(-120 * notches);
        inp[0].U.mi.flags = MV_WHEEL;
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }

    // Hold the left button and pull the map around. A drag pans the view, which can bring the
    // Base into sight when it sits off to one side. Real relative moves while the button is
    // held so the game sees a genuine drag.
    static void DragPan(int dx, int dy)
    {
        Rectangle pb = Screen.PrimaryScreen.Bounds;
        int cx = pb.Width / 2, cy = pb.Height / 2;
        int cb = Marshal.SizeOf(typeof(INPUT));
        SetCursorPos(cx, cy); Thread.Sleep(80);
        INPUT[] dn = new INPUT[1]; dn[0].type = IN_MOUSE; dn[0].U.mi.flags = MV_LDOWN;
        SendInput(1, dn, cb); Thread.Sleep(80);
        // Keep the pointer well inside the window. A large drag used to push it past the top
        // edge, where Windows clamps it and the Roblox window gets shoved around - so clamp the
        // absolute position with a margin instead.
        int mgx = pb.Width / 5, mgy = pb.Height / 5;
        int steps = 24;
        int px = cx, py = cy;
        for (int i = 1; i <= steps; i++)
        {
            int tx = cx + dx * i / steps, ty = cy + dy * i / steps;
            if (tx < mgx) tx = mgx; else if (tx > pb.Width - mgx) tx = pb.Width - mgx;
            if (ty < mgy) ty = mgy; else if (ty > pb.Height - mgy) ty = pb.Height - mgy;
            INPUT[] mv = new INPUT[1]; mv[0].type = IN_MOUSE; mv[0].U.mi.flags = MV_MOVE;
            mv[0].U.mi.dx = tx - px; mv[0].U.mi.dy = ty - py;
            SendInput(1, mv, cb); px = tx; py = ty;
            Thread.Sleep(12);
        }
        INPUT[] up = new INPUT[1]; up[0].type = IN_MOUSE; up[0].U.mi.flags = MV_LUP;
        SendInput(1, up, cb); Thread.Sleep(80);
    }

    // ---- verify-and-retry ----
    // click the word, then wait for it to LEAVE the screen; if it is still there, click again
    bool ClickWordUntilGone(string word, int perTryMs, int tries, int g)
    {
        for (int t = 1; t <= tries && Alive(g); t++)
        {
            if (!ClickWord(word, perTryMs, g)) return false;
            if (WaitGone(word, 4000, g)) return true;
            Log("   \"" + word + "\" still on screen - clicking again (" + t + "/" + tries + ")");
        }
        return false;
    }

    bool WaitGone(string word, int ms, int g)
    {
        int spent = 0;
        while (Alive(g) && spent < ms)
        {
            if (!WordOnScreen(word)) return true;
            InvalidateOcr();
            Thread.Sleep(120); spent += 120;
        }
        return !WordOnScreen(word);
    }

    bool WaitAppear(string word, int ms, int g)
    {
        int spent = 0;
        while (Alive(g) && spent < ms)
        {
            if (WordOnScreen(word)) return true;
            InvalidateOcr();
            Thread.Sleep(120); spent += 120;
        }
        return false;
    }

    bool WaitPhraseGone(string phrase, int ms, int g)
    {
        int spent = 0;
        while (Alive(g) && spent < ms)
        {
            if (!PhraseOnScreen(phrase)) return true;
            InvalidateOcr();
            Thread.Sleep(120); spent += 120;
        }
        return !PhraseOnScreen(phrase);
    }

    // click a coloured blob, then wait for the given phrase to DISAPPEAR (proof it worked;
    // "CHANGE TEAM" would be useless here because it never leaves the nav bar)
    bool ClickBlobUntil(string wordAfter, bool blue, int tries, int g)
    {
        int tx, ty;
        for (int t = 1; t <= tries && Alive(g); t++)
        {
            if (FindBlob(delegate (int rr, int gg, int bb)
            {
                return blue ? (bb > 110 && bb > rr + 40 && bb > gg + 40)
                            : (rr > 110 && rr > gg + 50 && rr > bb + 50);
            }, 200, out tx, out ty))
            {
                int[] before = Signature();
                if (t == 1) ClickPrimaryLogged(tx, ty, _team + " team blob");
                else if (t == 2 && _clickKeyOn && _clickVk != 0)
                    ClickLogged(tx, ty, _team + " team blob");
                else
                {
                    int n = ClickHard(tx, ty);
                    InvalidateOcr();
                    Log("   hard click " + _team + " team blob (" + tx + "," + ty + ") accepted=" + n + "/3");
                }
                if (WaitChange(before, 2.0, 800, g) && WaitPhraseGone(wordAfter, 8000, g)) return true;
                BumpHover();
                Log("   team not selected yet - retrying (" + t + "/" + tries + ")");
            }
            else
            {
                Thread.Sleep(250);
            }
        }
        return false;
    }

    // Raise the Roblox client's priority so the game keeps the CPU when the tool is working.
    // Needs the elevated token the app already has, otherwise the OS refuses to raise it.
    void SetRobloxPriority()
    {
        int n = 0;
        foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try { p.PriorityClass = ProcessPriorityClass.High; n++; }
            catch (Exception e) { Log("priority change failed: " + e.Message); }
        }
        if (n == 0) Log("no Roblox process running - start the game first");
        else Log("set " + n + " Roblox process(es) to HIGH priority");
    }

    // ================= watchers =================
    static string ReadFrom(string path, long pos)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (pos > fs.Length) return "";
                fs.Seek(pos, SeekOrigin.Begin);
                byte[] buf = new byte[fs.Length - pos];
                int read = fs.Read(buf, 0, buf.Length);
                return System.Text.Encoding.UTF8.GetString(buf, 0, read);
            }
        }
        catch { return ""; }
    }

    // If the program is started while the drone is ALREADY in the air (no Deploy As Drone ran
    // this session), pick the feed up from the screen instead of waiting for a deploy.
    void DroneTick(object sender, EventArgs e)
    {
        try
        {
            if (_running) return;
            if (_rfForm != null) return;                     // already running
            if (!_hudOn && !_watchHome) return;              // nothing to show
            // Normally the HUD starts from the flow (Deploy As Drone) - only scan the screen for a
            // drone view when the user opts in, i.e. they picked a drone by hand. Otherwise the
            // scan false-flagged on random UI text and popped the HUD up out of nowhere.
            if (!_hudAutoDetect) return;
            bool dv = DroneViewOnScreen();
            if (dv)
            {
                Log("drone view detected -> starting the RF/HUD");
                StartRfWatch();
            }
            else
            {
                if (_lastDroneDet) Log("drone view lost");
                else if (Environment.TickCount - _lastDetLog >= 6000)
                {
                    _lastDetLog = Environment.TickCount;
                    Log("   drone detect: nothing found - screen read: " + _detDbg);
                }
            }
            _lastDroneDet = dv;
        }
        catch { }
    }

    volatile bool _lastDroneDet = false;
    long _lastDetLog = 0;
    string _detDbg = "";
    bool DroneViewOnScreen()
    {
        // Big, high-contrast OSD text is far easier for the OCR than the small top-right badge:
        //  * the game's own FLIGHT mm:ss clock (top-left) - only exists in the drone view
        //  * the top-right RC LIVE / LINK LIVE badge
        // Any one of them means we are in a drone. The loadout / menus have neither.
        bool flight = ReadFlightSecs() >= 0;
        bool linked = !flight && CornerLinked();
        _detDbg = "flightClock=" + (flight ? "yes" : "no") + "  rcL live=" + (linked ? "yes" : "no");
        return flight || linked;
    }

    // 50fps horizon fusion on the UI thread. A complementary filter: the CONTROLLER is the
    // primary (rate) source and the camera is the absolute reference that removes drift. Running
    // this every 20ms makes the horizon smooth instead of stepping once per RF-loop tick.
    void HudTick(object sender, EventArgs e)
    {
        if (!_hudShown || !_hudOn)
        {
            // Tell the OBS overlay too, not just the on-monitor form - otherwise /state keeps
            // reporting hud:true and the stream stays stuck on the last HUD frame instead of
            // falling back to the CLI terminal.
            OverlayHub.I.SetHud(false, "", "", "", 0f, 0f, false);
            if (_hudForm != null) StopHud();
            return;
        }
        long now = Environment.TickCount;
        if (_hudPrevTick == 0) { _hudPrevTick = now; return; }
        float dt = (now - _hudPrevTick) / 1000f;
        _hudPrevTick = now;
        if (dt <= 0f) return;
        if (dt > 0.1f) dt = 0.1f;

        // Roll = RIGHT stick X ONLY. The left stick X is YAW (turning), and the old "else _padLx"
        // fallback meant turning the drone rolled the whole horizon ladder. Pitch = right stick Y
        // (the left stick Y is throttle).
        // Roll = RIGHT stick X only (the left stick X is YAW - adding it made turning roll the HUD).
        // Pitch RATE = right stick Y only.
        float sx = _padRx;
        float sy = _padRy;
        // kill stick rest/drift below 5% so "centred" actually happens - a stick sitting at 0.13
        // used to leave sx non-zero and the camera correction never ran (lines never levelled)
        if (sx > -0.05f && sx < 0.05f) sx = 0f;
        if (sy > -0.05f && sy < 0.05f) sy = 0f;

        // controller priority: integrate the stick every frame (rate -> angle)
        _hudFRoll += -sx * _hudRollRate * dt;    // tunable (roll deg/s)
        _hudFPitch -= sy * _hudPitchRate * dt;   // tunable (pitch px/s); inverted on purpose:
                                                 // pitching up must move the horizon DOWN

        // The LEFT stick (throttle) is a small, BOUNDED proportional nudge, NOT integrated. Adding
        // it to the rate meant holding throttle walked the horizon clean off the screen.
        float leftPitch = -_padLy * _hudLeftPx;

        // Complementary filter: the STICK is the fast rate input, the CAMERA is the slow absolute
        // reference - so the correction runs ALWAYS, not only when centred (gating it meant that
        // during flight, when a stick is nearly always held, the roll was pure stick drift and
        // never levelled onto the real horizon). Slow tau while flying so it does not fight the
        // stick, quicker once centred. A fix older than 1.5s is not trusted.
        bool det = _hudDetValid && (now - _hudDetAt) < 1500;
        if (det)
        {
            float tau = (sx == 0f && sy == 0f) ? _hudLockTau : _hudFlyTau;
            float a = 1f - (float)Math.Pow(0.5, dt / tau);
            _hudFRoll += (_hudDetRoll - _hudFRoll) * a;
            _hudFPitch += (_hudDetPitch - _hudFPitch) * a;
        }
        else if (sx == 0f && sy == 0f)
        {
            // no camera fix and hands off: level the roll back AND ease the pitch toward centre so
            // a run of failed detections can't leave the horizon stuck way high or low
            _hudFRoll += (0f - _hudFRoll) * (1f - (float)Math.Pow(0.5, dt / 0.6));
            _hudFPitch += (0f - _hudFPitch) * (1f - (float)Math.Pow(0.5, dt / 1.5));
        }

        if (_hudFRoll > 180f) _hudFRoll = 180f;
        if (_hudFRoll < -180f) _hudFRoll = -180f;
        if (_hudFPitch > 900f) _hudFPitch = 900f;    // was +-280px (~35deg) - it ran out of travel
        if (_hudFPitch < -900f) _hudFPitch = -900f;

        _hudRoll = _hudFRoll;
        _hudPitch = _hudFPitch + leftPitch;   // right-stick rate + small bounded left-stick offset

        string alt = _hudAgl != "" ? _hudAgl + " m" : (_hudAlt != "" ? _hudAlt + " m" : "");
        OverlayHub.I.SetHud(true, _hudHdg, _hudSpd != "" ? _hudSpd + " m/s" : "", alt, _hudFRoll, _hudPitch, _hudStyleUav);

        if (_hudForm == null) EnsureHud();          // UI thread - safe to create here
        if (_hudForm != null)
        {
            _hudForm.Home = _hudHome;
            _hudForm.Spd = _hudSpd;
            _hudForm.Agl = alt;
            _hudForm.Hdg = _hudHdg;
            _hudForm.Roll = _hudFRoll;
            _hudForm.PitchPx = _hudPitch;
            _hudForm.Uav = _hudStyleUav;
            _hudForm.Timer = string.Format("{0:00}:{1:00}", _hudSecs / 60, _hudSecs % 60);
            _hudForm.Secs = _hudSecs;
            _hudForm.Invalidate();
        }
    }

    void LogTick(object sender, EventArgs e)
    {
        if (!_autoRecon || _running || _halted) return;   // never fight an AUTO run, honour STOP
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
            if (!Directory.Exists(dir)) return;
            FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.log");
            List<FileInfo> cand = new List<FileInfo>();
            foreach (FileInfo fi in files)
                if (fi.Length > 2048 || fi.Name.IndexOf("_last", StringComparison.OrdinalIgnoreCase) >= 0) cand.Add(fi);
            if (cand.Count == 0) return;
            cand.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });

            DateTime cutoff = DateTime.Now.AddSeconds(-600);
            int n = Math.Min(4, cand.Count);
            for (int i = 0; i < n; i++)
            {
                FileInfo f = cand[i];
                if (f.LastWriteTime < cutoff) continue;
                long pos;
                // Start at the END of a log we have not seen before. Reading the tail meant
                // every app restart re-read a recent "Disconnection Notification" and fired a
                // needless reconnect (that is why it only happened while the tool was being
                // restarted). Only NEW lines should trigger a reconnect.
                if (!_logPos.TryGetValue(f.FullName, out pos)) pos = f.Length;
                if (f.Length < pos) pos = 0;
                if (f.Length <= pos) { _logPos[f.FullName] = f.Length; continue; }
                string chunk = ReadFrom(f.FullName, pos);
                _logPos[f.FullName] = f.Length;

                // A VOLUNTARY leave is logged as leaveUGCGameInternal / sendAnalyticsBeforeLeave /
                // "disconnect with reason: 285". Do not treat that as a crash and reconnect the
                // player straight back into the server they just left.
                if (chunk.IndexOf("leaveUGCGameInternal") >= 0 ||
                    chunk.IndexOf("sendAnalyticsBeforeLeave") >= 0 ||
                    chunk.IndexOf("disconnect with reason: 285") >= 0)
                {
                    _leftAt = DateTime.Now;
                    Log("left the server - suppressing auto-reconnect");
                }
                if (chunk.IndexOf("Disconnection Notification") >= 0)
                {
                    if ((DateTime.Now - _leftAt).TotalSeconds < 15)
                    {
                        Log("disconnect was part of leaving the server - not reconnecting");
                    }
                    else
                    {
                        Log("disconnected -> reconnecting");
                        Rejoin("auto (disconnect)");
                    }
                }
            }
        }
        catch { }
    }

    void OcrTick(object sender, EventArgs e)
    {
        if (!_ocrWatch || _running || _halted) return;
        string want = (_watchWords == null ? "" : _watchWords.Trim());
        if (want == "") return;

        bool on = WordOnScreen(want);
        if (on)
        {
            if (!_watchOn) { _watchSince = DateTime.Now; _watchOn = true; Log("OCR: \"" + want + "\" on screen - timing"); }
            double secs = (DateTime.Now - _watchSince).TotalSeconds;
            if (secs >= _watchStuckSec)
            {
                Log("OCR: stuck on \"" + want + "\" for " + secs.ToString("0") + "s -> reconnecting");
                _watchOn = false;
                Rejoin("ocr stuck");
            }
        }
        else if (_watchOn)
        {
            Log("OCR: \"" + want + "\" gone - load finished after " + (DateTime.Now - _watchSince).TotalSeconds.ToString("0") + "s");
            _watchOn = false;
        }
    }

    // thumbstick -> -1..1 with a dead-zone, so a resting stick is exactly 0
    static float NormStick(short v)
    {
        float f = v / 32767f;
        if (f > 1f) f = 1f; if (f < -1f) f = -1f;
        if (Math.Abs(f) < 0.12f) return 0f;
        return f;
    }

    static bool TryPad(int idx, out XINPUT_STATE st)
    {
        st = new XINPUT_STATE();
        try { if (XInput14(idx, ref st) == 0) return true; } catch { }
        try { if (XInput910(idx, ref st) == 0) return true; } catch { }
        return false;
    }

    void PadTick(object sender, EventArgs e)
    {
        ushort buttons = 0; bool conn = false;
        for (int i = 0; i < 4; i++)
        {
            XINPUT_STATE st;
            if (TryPad(i, out st))
            {
                conn = true; buttons = st.Gamepad.wButtons;
                _padLx = NormStick(st.Gamepad.lx);
                _padLy = NormStick(st.Gamepad.ly);
                _padRx = NormStick(st.Gamepad.rx);
                _padRy = NormStick(st.Gamepad.ry);
                break;
            }
        }

        if (!conn)
        {
            lblPadStatus.Text = "no controller";
            _padRejoinWasDown = _padAutoWasDown = false;
            return;
        }

        lblPadStatus.Text = "pad connected   |   rejoin: " + (_padRejoinOn ? _padRejoin : "off") +
                            "   |   auto: " + (_padAutoOn ? _padAuto : "off") +
                            "   |   stop: " + (_padStopOn ? _padStop : "off");

        // diagnostic: log every button-state change so we can see what the pad reports
        if (buttons != _lastButtons)
        {
            Log("pad buttons 0x" + buttons.ToString("X4"));
            _lastButtons = buttons;
        }

        if (_padRejoinOn)
        {
            ushort m = PAD.ContainsKey(_padRejoin) ? PAD[_padRejoin] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            // fire on the PRESS EDGE - a hold requirement silently swallowed short taps
            if (d && !_padRejoinWasDown)
            {
                Log("controller " + _padRejoin + " pressed -> rejoin");
                Rejoin("controller " + _padRejoin);
            }
            _padRejoinWasDown = d;
        }

        if (_padAutoOn)
        {
            ushort m = PAD.ContainsKey(_padAuto) ? PAD[_padAuto] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            if (d && !_padAutoWasDown)
            {
                Log("controller " + _padAuto + " pressed -> AUTO");
                AutoRun();
            }
            _padAutoWasDown = d;
        }

        if (_padStopOn)
        {
            ushort m = PAD.ContainsKey(_padStop) ? PAD[_padStop] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            if (d && !_padStopWasDown)
            {
                Log("controller " + _padStop + " pressed -> STOP");
                StopAuto();
            }
            _padStopWasDown = d;
        }

        if (_padLandOn)
        {
            ushort m = PAD.ContainsKey(_padLand) ? PAD[_padLand] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            if (d && !_padLandWasDown)
            {
                Log("controller " + _padLand + " pressed -> LAND NOW");
                ShowLandNow();
            }
            _padLandWasDown = d;
        }

        if (_padReconnectOn)
        {
            ushort m = PAD.ContainsKey(_padReconnect) ? PAD[_padReconnect] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            if (d && !_padReconnectWasDown)
            {
                Log("controller " + _padReconnect + " pressed -> straight reconnect (no overlay)");
                Rejoin("controller " + _padReconnect, false);
            }
            _padReconnectWasDown = d;
        }
    }

    // ================= hotkeys =================
    void RegisterHotkeys()
    {
        UnregisterHotKey(Handle, HK_REJOIN);
        UnregisterHotKey(Handle, HK_AUTO);
        UnregisterHotKey(Handle, HK_NIGHT);
        UnregisterHotKey(Handle, HK_STOP);
        // F6 = STOP, so the flight stick can stop the flow (hotkey fires from any focus)
        if (!RegisterHotKey(Handle, HK_STOP, 0, 0x75))
            Log("WARNING: could not register F6 for STOP - another app already has it.");
        if (!RegisterHotKey(Handle, HK_REJOIN, 0, _hkRejoinKey))
            Log("WARNING: could not register " + ((Keys)_hkRejoinKey) + " - another app already has it. Use 'Set key' to choose another.");
        if (!RegisterHotKey(Handle, HK_AUTO, 0, _hkAutoKey))
            Log("WARNING: could not register F5 - another app already has it.");
        if (!RegisterHotKey(Handle, HK_NIGHT, 0, _hkNightKey))
            Log("WARNING: could not register " + ((Keys)_hkNightKey) + " for night vision - another app already has it.");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_capturingKey || _capturingNight)
        {
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Escape)
            {
                EndCaptureKey();
                Log("key capture cancelled");
                return;
            }
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
            if (e.Control || e.Shift || e.Alt) { Log("use a single key with no modifiers"); return; }

            if (_capturingNight)
            {
                _hkNightKey = (uint)e.KeyCode;
                if (lblNightVal != null) lblNightVal.Text = e.KeyCode.ToString();
                Log("night vision keybind set to " + e.KeyCode);
            }
            else
            {
                _hkRejoinKey = (uint)e.KeyCode;
                if (lblKeyVal != null) lblKeyVal.Text = e.KeyCode.ToString();
                Log("rejoin keybind set to " + e.KeyCode);
            }
            EndCaptureKey();
            RegisterHotkeys();
            SaveCfg();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HK_REJOIN) { Log("hotkey " + ((Keys)_hkRejoinKey) + " pressed"); Rejoin("hotkey"); }
            else if (id == HK_AUTO) AutoRun();
            else if (id == HK_NIGHT) ToggleNightVision();
            else if (id == HK_STOP) { Log("hotkey F6 -> STOP"); StopAuto(); }
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyNoActivate();
        // Keep the panel OUT of our own screen grabs. Tabbing out of the game brings this window
        // forward, and the OCR was then reading the panel (including log lines like "NO SIGNAL")
        // and deciding the drone had crashed - which stopped the HUD the moment you tabbed away.
        ExcludeFromCapture(Handle);
        _hudStyleUav = _drone == "MAVIC";   // the OSD style is set by the chosen drone
        // Always start with a clean display: if a previous run died while the colour effect was
        // on (the invert bug), this clears it so a restart is never needed.
        _nightVision = false;
        ApplyNightVision(false);
        RegisterHotkeys();
        lblKeyVal.Text = ((Keys)_hkRejoinKey).ToString();
        if (lblNightVal != null) lblNightVal.Text = ((Keys)_hkNightKey).ToString();
        Log("ready.  " + ((Keys)_hkRejoinKey) + " = rejoin,  F5 = AUTO RUN,  " + ((Keys)_hkNightKey) + " = night vision.");
        if (_placeId != "") Log("server ready: " + _serverId);
        else Log("no server detected yet - launch Roblox once, then Refresh");
        try { OverlayHub.I.SetMission(_team, _drone, _bomb); OverlayHub.I.Start(_appDir); Log("OBS overlay: add a Browser Source -> http://localhost:8730/"); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _running = false;
        KillOcrServer();       // do not leave the resident helper behind
        UnregisterHotKey(Handle, HK_REJOIN);
        UnregisterHotKey(Handle, HK_AUTO);
        UnregisterHotKey(Handle, HK_NIGHT);
        UnregisterHotKey(Handle, HK_STOP);
        ApplyNightVision(false);   // never leave the display inverted after exit
        SaveCfg();
        base.OnFormClosing(e);
    }

    // ================= config =================
    // The two drones carry completely different warheads. MAVIC drops grenade racks; FPV
    // uses the rocket/grenade launcher rounds. Keeping them separate stops us clicking a
    // warhead that is not on the panel.
    static bool MavicBomb(string b)
    {
        foreach (string x in BombsFor("MAVIC")) if (x == b) return true;
        return false;
    }

    static string[] BombsFor(string drone)
    {
        if (drone == "MAVIC")
            return new string[] { "(none)", "M67 Rack", "RGO-5 Triple Rack", "RGO Rack", "RGO Triple Rack" };
        return new string[] { "(none)", "Standard Frag", "PG-7VS Shaped", "PG-7VS Thermo", "Light Rocket", "TBG-7B Thermobaric" };
    }

    void FillBombs()
    {
        if (cmbBomb == null) return;      // the drone combo is built before the bomb combo
        _filling = true;
        cmbBomb.Items.Clear();
        foreach (string b in BombsFor(_drone)) cmbBomb.Items.Add(b);
        if (!cmbBomb.Items.Contains(_bomb)) _bomb = "(none)";   // drop a warhead from the other drone
        cmbBomb.SelectedItem = _bomb;
        _filling = false;
    }

    void LoadCfg()
    {
        try
        {
            if (!File.Exists(_cfgPath)) return;
            foreach (string line in File.ReadAllLines(_cfgPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (k == "team") _team = v;
                else if (k == "drone") _drone = v;
                else if (k == "bomb") _bomb = v;
                else if (k == "asDrone") _asDrone = v == "1";
            else if (k == "padRejoin") _padRejoin = v;
            else if (k == "padAuto") _padAuto = v;
            else if (k == "padStop") _padStop = v;
            else if (k == "padRejoinOn") _padRejoinOn = v == "1";
            else if (k == "padAutoOn") _padAutoOn = v == "1";
            else if (k == "padStopOn") _padStopOn = v == "1";
            else if (k == "padLand") _padLand = v;
            else if (k == "padLandOn") _padLandOn = v == "1";
            else if (k == "padReconnect") _padReconnect = v;
            else if (k == "padReconnectOn") _padReconnectOn = v == "1";
            else if (k == "autoRecon") _autoRecon = v == "1";
            else if (k == "topMost") _topMost = v == "1";
            else if (k == "noActivate") _noActivate = v == "1";
            else if (k == "clickKeyOn") _clickKeyOn = v == "1";
            else if (k == "clickKey") _clickKeyText = v;
            else if (k == "ocrWatch") _ocrWatch = v == "1";
            else if (k == "watchWords") _watchWords = v;
            else if (k == "watchStuck") _watchStuckSec = int.Parse(v);
            else if (k == "matchPct") _matchPct = int.Parse(v);
                else if (k == "hoverMs") { _hoverBase = int.Parse(v); _hoverMs = _hoverBase; }
                else if (k == "autoAfterRejoin") _autoAfterRejoin = v == "1";
                else if (k == "autoAfterRejoinMs") _autoAfterRejoinMs = int.Parse(v);
                else if (k == "preRejoinMs") _preRejoinMs = int.Parse(v);
                else if (k == "watchHome") _watchHome = v == "1";
                else if (k == "hudAutoDetect") _hudAutoDetect = v == "1";
            else if (k == "hud") _hudOn = v == "1";
            else if (k == "hudPitch") _hudPitchRate = ParseF(v);
            else if (k == "hudRoll") _hudRollRate = ParseF(v);
            else if (k == "hudLock") _hudLockTau = ParseF(v);
            else if (k == "hudFly") _hudFlyTau = ParseF(v);
            else if (k == "hudBias") _hudBias = ParseF(v);
            else if (k == "uav") _hudStyleUav = v == "1";
            else if (k == "night") _nightVision = v == "1";
            else if (k == "nightKey") { try { _hkNightKey = (uint)int.Parse(v); } catch { } }
                else if (k == "blackScreen") _blackScreen = v == "1";
                else if (k == "lang") _lang = v;
            }
            _clickVk = ParseVk(_clickKeyText);
        }
        catch { }
    }

    void SaveCfg()
    {
        try
        {
            File.WriteAllLines(_cfgPath, new string[] {
                "team=" + _team,
                "drone=" + _drone,
                "bomb=" + _bomb,
                "asDrone=" + (_asDrone ? "1" : "0"),
                "padRejoin=" + _padRejoin,
                "padAuto=" + _padAuto,
                "padStop=" + _padStop,
                "padRejoinOn=" + (_padRejoinOn ? "1" : "0"),
                "padAutoOn=" + (_padAutoOn ? "1" : "0"),
                "padStopOn=" + (_padStopOn ? "1" : "0"),
                "padLand=" + _padLand,
                "padLandOn=" + (_padLandOn ? "1" : "0"),
                "padReconnect=" + _padReconnect,
                "padReconnectOn=" + (_padReconnectOn ? "1" : "0"),
                "autoRecon=" + (_autoRecon ? "1" : "0"),
                "topMost=" + (_topMost ? "1" : "0"),
                "noActivate=" + (_noActivate ? "1" : "0"),
                "clickKeyOn=" + (_clickKeyOn ? "1" : "0"),
                "clickKey=" + _clickKeyText,
                "ocrWatch=" + (_ocrWatch ? "1" : "0"),
                "watchWords=" + _watchWords,
                "watchStuck=" + _watchStuckSec,
                "matchPct=" + _matchPct,
                "hoverMs=" + _hoverBase,
                "autoAfterRejoin=" + (_autoAfterRejoin ? "1" : "0"),
                "autoAfterRejoinMs=" + _autoAfterRejoinMs,
                "preRejoinMs=" + _preRejoinMs,
            "watchHome=" + (_watchHome ? "1" : "0"),
            "hudAutoDetect=" + (_hudAutoDetect ? "1" : "0"),
            "blackScreen=" + (_blackScreen ? "1" : "0"),
            "hud=" + (_hudOn ? "1" : "0"),
            "hudPitch=" + _hudPitchRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudRoll=" + _hudRollRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudLock=" + _hudLockTau.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudFly=" + _hudFlyTau.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudBias=" + _hudBias.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "uav=" + (_hudStyleUav ? "1" : "0"),
            "night=" + (_nightVision ? "1" : "0"),
            "nightKey=" + _hkNightKey,
                "lang=" + _lang
            });
        }
        catch { }
    }

    // Fake RF video feed: TRANSLUCENT grayscale static over the game. It has to be a per-pixel
    // alpha layered window (UpdateLayeredWindow) - a normal Form paints its own background
    // opaque, which is exactly why the old version buried the game under near-black noise.
    // Palette is black/white only, like a real analog feed.
    class RFOverlayForm : Form
    {
        public string Readout = "RF LINK   HOME  ----";
        public int Phase = 0;
        public float Static = 0.2f;         // 0.05 = barely any grain, 1 = heavy
        readonly Font _f = new Font("Consolas", 22F, FontStyle.Bold);
        Bitmap[] _frames;
        float _baked = -1f;
        int _bw, _bh;
        public bool UlwFailed = false;   // set if UpdateLayeredWindow refuses, so the caller can log

        public RFOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT - click-through
                return cp;
            }
        }

        // Bake 4 premultiplied grayscale noise frames. Grain alpha stays low so the game shows
        // through; the readout and signal block are white.
        void BuildFrames(float lvl)
        {
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            Random r = new Random();
            int aMax = (int)(14 + 70 * lvl);        // grain strength: visible, but the game shows
            if (aMax < 4) aMax = 4;                 // through it (a full-opacity cover is the bug)
            Bitmap[] fr = new Bitmap[4];
            for (int f = 0; f < 4; f++)
            {
                Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                int[] buf = new int[w * h];
                for (int y = 0; y < h; y++)
                {
                    bool scan = ((y + f) & 1) == 1;             // fine interlace lines
                    for (int x = 0; x < w; x++)
                    {
                        int v = r.Next(0, 256);
                        int a = (v * aMax) / 255;
                        int pv = (v * a) / 255;                 // premultiplied
                        int px = (a << 24) | (pv << 16) | (pv << 8) | pv;
                        if (scan) px = (12 << 24);              // faint dark line
                        buf[y * w + x] = px;
                    }
                }
                System.Drawing.Imaging.BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
                b.UnlockBits(bd);

                using (Graphics g = Graphics.FromImage(b))
                {
                    SizeF sz = g.MeasureString(Readout, _f);
                    float rx = (w - sz.Width) / 2f, ry = h - 90;
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                        g.DrawString(Readout, _f, fb, rx, ry);
                    int bl = 210 - (f % 3) * 60;
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(bl, 255, 255, 255)))
                        g.FillRectangle(sb, rx + sz.Width + 16, ry + 12, 24, 24);
                }
                fr[f] = b;
            }
            _frames = fr;
            _baked = lvl; _bw = w; _bh = h;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] struct BMIH
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] struct BMI { public BMIH h; public int colors; }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BMI bi, uint usage, out IntPtr bits, IntPtr sect, uint off);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT ptDst, ref SIZE sz, IntPtr src, ref POINT ptSrc, int key, ref BLENDFUNCTION blend, int flags);

        public void Render()
        {
            try
            {
                if (Width <= 0 || Height <= 0) return;
                float bucket = (float)Math.Round(Static * 4.0) / 4f;   // only 5 strength levels, so
                if (_frames == null || _bw != Width || _bh != Height       // we rarely re-bake
                    || Math.Abs(_baked - bucket) > 0.01f)
                    BuildFrames(bucket);
                if (_frames == null) return;
                Bitmap bmp = _frames[Phase & 3];

                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr mem = CreateCompatibleDC(screen);
                BMI bi = new BMI();
                bi.h.biSize = Marshal.SizeOf(typeof(BMIH));
                bi.h.biWidth = bmp.Width;
                bi.h.biHeight = -bmp.Height;
                bi.h.biPlanes = 1;
                bi.h.biBitCount = 32;
                IntPtr bits;
                IntPtr dib = CreateDIBSection(mem, ref bi, 0, out bits, IntPtr.Zero, 0);
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                int n = bmp.Width * bmp.Height * 4;
                byte[] tmp = new byte[n];
                Marshal.Copy(bd.Scan0, tmp, 0, n);
                bmp.UnlockBits(bd);
                Marshal.Copy(tmp, 0, bits, n);

                IntPtr old = SelectObject(mem, dib);
                POINT dst = new POINT { X = Left, Y = Top };
                POINT src = new POINT { X = 0, Y = 0 };
                SIZE sz = new SIZE { cx = bmp.Width, cy = bmp.Height };
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref blend, 2))
                    UlwFailed = true;

                SelectObject(mem, old);
                DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
            catch { }
        }
    }

    // The FPV/UAV OSD drawn over the game on the monitor: artificial horizon + pitch ladder,
    // a compass tape, speed/altitude ladders, a centre reticle and the HOME readout.
    class FpvHudForm : Form
    {
        public string Home = "----", Spd = "", Alt = "", Agl = "", Hdg = "", Timer = "00:00";
        public int Secs = 0;   // flight seconds - drives the draining battery readout
        public float Roll = 0f, PitchPx = 0f;
        public bool Uav = false;
        readonly Font _f = new Font("Consolas", 12F, FontStyle.Bold);
        readonly Font _fb = new Font("Consolas", 15F, FontStyle.Bold);
        // DJI-Fly style fonts for the MAVIC (UAV) layout
        readonly Font _fm = new Font("Segoe UI", 16F, FontStyle.Bold);
        readonly Font _fs = new Font("Segoe UI", 13F, FontStyle.Regular);
        // black/white only, like a real monochrome FPV OSD (no green tint)
        readonly SolidBrush _g = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        readonly SolidBrush _gb = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        readonly Pen _p = new Pen(Color.FromArgb(230, 255, 255, 255), 2);
        readonly Pen _pt = new Pen(Color.FromArgb(165, 230, 230, 230), 1);

        public FpvHudForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            // no global Opacity here - that would paint the form's grey background over the whole
            // screen and hide the RF static underneath. Key out black so only the drawn HUD shows.
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT - click-through
                return cp;
            }
        }

        PointF R(float x, float y, int cx, int cy, float rad)
        {
            float dx = x - cx, dy = y - cy;
            float cs = (float)Math.Cos(rad), sn = (float)Math.Sin(rad);
            return new PointF(cx + dx * cs - dy * sn, cy + dx * sn + dy * cs);
        }

        float DistMetres()
        {
            float v = Num(Home);
            if ((Home ?? "").ToUpperInvariant().IndexOf("KM") >= 0) v *= 1000f;
            return v;
        }

        static float Num(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            string n = "";
            foreach (char c in s)
            {
                if ((c >= '0' && c <= '9') || c == '.' || c == '-') n += c;
                else if (n.Length > 0) break;
            }
            float v;
            return float.TryParse(n, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        // DJI-Fly style overlay for the MAVIC: rule-of-thirds grid, mode/status up top,
        // telemetry + exposure along the bottom, and a record button. Clean phone-recording look.
        void DrawMavic(Graphics g, int W, int H)
        {
            SolidBrush white = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
            SolidBrush dim = new SolidBrush(Color.FromArgb(205, 235, 235, 235));
            SolidBrush recB = new SolidBrush(Color.FromArgb(230, 226, 32, 32));
            Pen grid = new Pen(Color.FromArgb(58, 255, 255, 255), 1);
            Pen thin = new Pen(Color.FromArgb(150, 255, 255, 255), 1);
            Pen recP = new Pen(Color.FromArgb(230, 226, 32, 32), 2);

            for (int i = 1; i <= 2; i++)
            {
                g.DrawLine(grid, W * i / 3, 0, W * i / 3, H);
                g.DrawLine(grid, 0, H * i / 3, W, H * i / 3);
            }
            g.DrawLine(thin, W / 2 - 24, H / 2, W / 2 - 9, H / 2);
            g.DrawLine(thin, W / 2 + 9, H / 2, W / 2 + 24, H / 2);
            g.DrawLine(thin, W / 2, H / 2 - 24, W / 2, H / 2 - 9);
            g.DrawLine(thin, W / 2, H / 2 + 9, W / 2, H / 2 + 24);

            // top-left under the game's phone icons
            g.DrawString("N Mode", _fm, white, 30, 76);

            // top-right, under the game's "RC LIVE": battery pill + nub, %, signal bars
            int rEdge = W - 26;
            // battery drains with the flight timer: 100% at launch, 50% at 5 min, empty at 15 min
            int bat = Secs <= 300 ? 100 - (int)(Secs * 50f / 300f) : 50 - (int)((Secs - 300) * 50f / 600f);
            if (bat < 0) bat = 0; if (bat > 100) bat = 100;
            bool low = bat <= 30;
            SolidBrush batB = low ? new SolidBrush(Color.FromArgb(235, 235, 60, 60)) : white;
            string pctTxt = bat + "%";
            SizeF pct = g.MeasureString(pctTxt, _fs);
            g.DrawRectangle(thin, rEdge - 48, 70, 44, 20);
            int fw = (int)(32 * bat / 100f);
            if (fw > 0) g.FillRectangle(batB, rEdge - 45, 73, fw, 14);
            g.FillRectangle(batB, rEdge - 4, 76, 4, 8);      // nub
            g.DrawString(pctTxt, _fs, batB, rEdge - 56 - pct.Width, 72);   // centred on the pill
            // signal bars track the distance to HOME (4 close -> 1 far), like a real RC link
            float dm = DistMetres();
            int bars = 4 - (int)(dm / 300f);
            if (bars < 1) bars = 1; if (bars > 4) bars = 4;
            for (int i = 0; i < 4; i++)
                g.FillRectangle(i < bars ? white : dim, rEdge - 64 - (int)pct.Width - 40 + i * 8, 84 - i * 4, 5, 6 + i * 4);
            // (no ALT readout - the game already prints the height bottom-left)

            // bottom-centre (free): resolution + recording time
            g.DrawString("4K 30", _fm, white, W / 2 - 130, H - 94);
            g.DrawString(string.IsNullOrEmpty(Timer) ? "00:00" : Timer, _fm, white, W / 2 + 4, H - 94);

            // exposure above the game's PAYLOAD block, not on it
            g.DrawString("1/60", _fm, white, W * 60 / 100, H - 150);
            g.DrawString("F2.8", _fm, white, W * 68 / 100, H - 150);
            g.DrawString("ISO 100", _fs, white, W * 60 / 100, H - 120);
            g.DrawString("EV+0.3", _fs, white, W * 68 / 100, H - 120);

            // record button
            g.FillEllipse(recB, W - 126, H / 2 - 34, 62, 62);
            g.DrawEllipse(recP, W - 114, H / 2 - 22, 38, 38);
            g.DrawString("REC", _fs, white, W - 128, H / 2 - 58);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int W = Width, H = Height, cx = W / 2, cy = H / 2;
            float rad = Roll * (float)Math.PI / 180f;

            if (Uav) { DrawMavic(g, W, H); base.OnPaint(e); return; }   // DJI-Fly style

            // artificial horizon + pitch ladder - every endpoint is rotated about the centre by
            // the bank, so the whole ladder rolls with the controller input. Runs out to +-90 deg
            // and fades with distance from the centre so it never hard-stops or clutters the view.
            for (int d = -90; d <= 90; d += 10)
            {
                float yy = PitchPx + d * 8f;                 // 10 deg = 8px
                float dist = Math.Abs(yy);
                float af = dist <= 200f ? 1f : 1f - (dist - 200f) / 320f;   // fade 200px -> 520px
                if (af <= 0.02f) continue;
                float half = d == 0 ? 150f : 70f;
                int aMain = (int)(230 * af), aThin = (int)(165 * af), aTxt = (int)(235 * af);
                Pen pen = d == 0
                    ? new Pen(Color.FromArgb(aMain, 255, 255, 255), 2)
                    : new Pen(Color.FromArgb(aThin, 230, 230, 230), 1);
                PointF a = R(cx - half, cy + yy, cx, cy, rad), b = R(cx + half, cy + yy, cx, cy, rad);
                g.DrawLine(pen, a, b);
                if (d == 0)
                {
                    g.DrawLine(pen, a, R(cx - half, cy + yy + 12, cx, cy, rad));   // end caps
                    g.DrawLine(pen, b, R(cx + half, cy + yy + 12, cx, cy, rad));
                }
                else
                {
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(aTxt, 255, 255, 255)))
                        g.DrawString((d > 0 ? "+" : "") + d, _f, lb, R(cx + half + 6, cy + yy - 8, cx, cy, rad));
                }
                pen.Dispose();
            }

            // bank indicator: fixed tick at the top, marker swings with the roll
            g.DrawLine(_pt, cx, 26, cx, 40);
            PointF bm = R(cx, 34, cx, cy, rad);
            g.FillEllipse(_g, bm.X - 5, bm.Y - 5, 10, 10);

            // centre reticle
            g.DrawLine(_p, cx - 42, cy, cx - 12, cy);
            g.DrawLine(_p, cx + 12, cy, cx + 42, cy);
            g.DrawLine(_p, cx, cy - 42, cx, cy - 12);
            g.DrawLine(_p, cx, cy + 12, cx, cy + 42);
            g.DrawEllipse(_pt, cx - 3, cy - 3, 6, 6);

            // compass tape, top-centre
            int hdg;
            if (int.TryParse(Hdg, out hdg))
            {
                float pxDeg = 6f;
                for (int d = -60; d <= 60; d += 10)
                {
                    int deg = ((hdg + d) % 360 + 360) % 360;
                    float x = cx + d * pxDeg;
                    if (d == 0) { g.FillRectangle(_gb, x - 24, 18, 48, 26); g.DrawRectangle(_pt, x - 24, 18, 48, 26); }
                    else { g.DrawLine(_pt, x, 26, x, 40); if (deg % 30 == 0) g.DrawString(deg.ToString(), _f, _g, x - 14, 44); }
                }
                g.DrawString(hdg + "Â°", _f, _g, cx - 18, 22);
            }

            DrawLadder(g, W, cy, true, Spd);                       // speed on the left
            DrawLadder(g, W, cy, false, Alt != "" ? Alt : Agl);     // ALT on the right
            // no centre HOME, no fly timer / style text - the game already prints those
            base.OnPaint(e);
        }

        void DrawLadder(Graphics g, int W, int cy, bool left, string val)
        {
            int lx = left ? 170 : W - 170;
            for (int k = -5; k <= 5; k++)
            {
                int y = cy + k * 40;
                g.DrawLine(_pt, left ? lx - 18 : lx, y, left ? lx : lx + 18, y);
            }
            if (!string.IsNullOrEmpty(val))
            {
                SizeF sz = g.MeasureString(val, _f);
                int bw = (int)sz.Width + 18, bh = 26;
                Rectangle box = new Rectangle(left ? lx - bw + 18 : lx - 18, cy - bh / 2, bw, bh);
                g.FillRectangle(_gb, box);
                g.DrawRectangle(_pt, box);
                g.DrawString(val, _f, _g, box.X + 9, box.Y + 4);
            }
        }
    }

    void EnsureHud()
    {
        try
        {
            if (_hudForm != null) return;
            _hudForm = new FpvHudForm();
            _hudForm.FormBorderStyle = FormBorderStyle.None;
            _hudForm.StartPosition = FormStartPosition.Manual;
            _hudForm.TopMost = true;
            _hudForm.ShowInTaskbar = false;
            _hudForm.SetBounds(0, 0, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            _hudForm.Uav = _hudStyleUav;
            _hudForm.Show();
            ExcludeFromCapture(_hudForm.Handle);   // keep the HUD out of the OCR's screen grabs
        }
        catch { }
    }

    void StopHud()
    {
        try { if (_hudForm != null) { _hudForm.Close(); _hudForm = null; } } catch { }
    }

    // Poll the HUD for the HOME distance and drive the RF overlay. Runs until stopped.
    void StartRfWatch()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { StartRfWatch(); }); return; }
            if (_rfForm != null) return;

            _rfForm = new RFOverlayForm();
            _rfForm.FormBorderStyle = FormBorderStyle.None;
            _rfForm.StartPosition = FormStartPosition.Manual;
            _rfForm.TopMost = true;
            _rfForm.ShowInTaskbar = false;
            _rfForm.SetBounds(0, 0, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            _rfForm.Show();
            ExcludeFromCapture(_rfForm.Handle);   // keep the feed out of the OCR's screen grabs
            _rfForm.Visible = false;              // stay hidden until "LINK LIVE" is confirmed, so
                                                  // the feed never flashes up during the deploy
            _rfRun = true;
            _flightStart = DateTime.Now;
            _flightOcrBase = -1; _flightOcrAt = 0;   // re-read the OSD flight clock for this flight
            _lastInDroneAt = Environment.TickCount;  // assume we start in the drone (we just deployed)
            _rfHomeText = "----";
            _homeLastM = -1f; _homeLastText = null; _homeLastAt = 0;
            _hudSmSeeded = false;   // re-seed the smoothed horizon for this flight
            OverlayHub.I.SetFlight(true, 0.18f, 0);

            Thread t = new Thread(delegate ()
            {
                // The watch only starts once we ARE in the drone (the flow's Deploy As Drone, or
                // the opt-in screen detect), so show immediately - requiring LINK here meant a
                // flaky corner read kept the whole HUD hidden.
                bool shown = true;
                _hudLocked = false;    // re-lock the horizon each time the feed comes up
                _hudNeedLock = true;   // start hunting for the spawn horizon immediately
                long lockArmedAt = Environment.TickCount + 1200;   // skip the base panel/map frames
                long lastOcr = 0;      // slower cadence: NO SIGNAL / LINK / heading / AGL
                long lastHome = 0;     // faster cadence: HOME / SPD / ALT
                long lastLink = Environment.TickCount;   // LINK-loss watchdog baseline
                float targetLvl = 0.2f;      // from the last HOME read (the "true" value)
                float shownLvl = 0.2f;       // eased toward the target every tick
                string txt = "RF LINK   HOME  ----";
                float lastSet = -1f;
                string lastTxt = null;
                bool lastOk = false;
                long lastHud = 0;
                long lastDet = 0;      // horizon measurement cadence (faster than the slow path)
                int inHits = 0, outHits = 0;   // debounce so one bad OCR frame cannot flap the feed

                while (_rfRun)
                {
                    try
                    {
                    // ---- HOME / SPD / ALT on their own faster cadence so the distance keeps up
                    // (everything here is one small cropped read, not a full-screen grab) ----
                    if (shown && Environment.TickCount - lastHome >= 700)
                    {
                        lastHome = Environment.TickCount;
                        // MAVIC uses the H/D/H.S/V.S layout, FPV uses SPD/ALT/HOME
                        string dh = FilterHome(_hudStyleUav ? ReadMavicBottom() : ReadHudBottom());
                        if (dh != null && dh != _rfHomeText) { _rfHomeText = dh; Log("   RF HOME: " + dh); }
                        OverlayHub.I.SetHome(dh);
                        float hm = HomeMetres(dh);
                        targetLvl = (hm < 0f) ? 0.2f : hm / 1200f;      // faintest at HOME, full at 1.2 km
                        if (targetLvl < 0.08f) targetLvl = 0.08f;
                        if (targetLvl > 1f) targetLvl = 1f;
                        txt = "RF LINK   HOME  " + (dh != null ? dh : "----");
                    }

                    // ---- slower path, every ~1.5s ----
                    if (Environment.TickCount - lastOcr >= 1500)
                    {
                        lastOcr = Environment.TickCount;

                        // the crash screen ("NO SIGNAL") means the drone is gone - reconnect to
                        // the same server straight away
                        if (IsNoSignal())
                        {
                            Log("   NO SIGNAL detected - drone crashed, rejoining the same server");
                            StopRfWatch();
                            Rejoin("no signal");   // LAND NOW shows first, then the black cover follows
                            return;
                        }

                        // the top-right "LINK LIVE" indicator is the proof we are actually in
                        // the drone view - without it the feed should not run at all
                        // NOTE: no LINK-based auto-off any more. The watch only starts when we ARE in
                        // the drone, and the flaky corner read was shutting the HUD off at random.
                        // A real crash is caught by the NO SIGNAL check above.
                        // Is this still a drone view? The FLIGHT clock (top-left) and the RC/LINK
                        // LIVE badge (top-right) only exist in the drone OSD - the map, loadout and
                        // menus have neither. If they are both gone for a few seconds we have left
                        // the drone, so drop the HUD instead of leaving it stuck on screen.
                        int fs = ReadFlightSecs();   // FLIGHT (MAVIC) / FLY (FPV) clock
                        bool corner = CornerLinked();
                        // "not flying" proof. Menu words, plus ONLY the unambiguous screen names.
                        // We must NOT use the whole ScreenName() here: its "map" case matches the
                        // bare word POINT, and the drone OSD prints "Return to a supply point" -
                        // which was dropping the MAVIC HUD the moment the payload ran out.
                        string sn = ScreenName(OcrWords());
                        bool menu = MenuOnScreen()
                            || sn == "team select" || sn == "lobby" || sn == "loadout"
                            || sn == "team base" || sn == "loading";
                        bool inDrone = (fs >= 0 || corner || DroneKeyword()) && !menu;
                        if (fs >= 0) { _flightOcrBase = fs; _flightOcrAt = Environment.TickCount; }

                        // Debounce BOTH directions. A single frame of bad OCR used to drop the
                        // feed, and the very next frame turned it back on - so the HUD flapped off
                        // and on "at random". Require two consecutive reads to agree.
                        if (inDrone) { inHits++; outHits = 0; } else { outHits++; inHits = 0; }
                        if (inHits >= 2)
                        {
                            _lastInDroneAt = Environment.TickCount;
                            if (!shown) { shown = true; Log("   RF feed: drone view - overlay on"); }
                        }
                        if (shown && (outHits >= 2 || Environment.TickCount - _lastInDroneAt > 30000))
                        {
                            // two agreeing "not flying" reads (or the 30s quiet timeout) -> drop.
                            shown = false;
                            Log(menu ? "   menu on screen - overlay off" : "   RF feed: no drone view - overlay off");
                            // tell the OBS terminal so it visibly drops back to the CLI instead of
                            // freezing on the last HUD frame
                            AddBlackLine(menu ? "returning to command line - menu detected"
                                              : "rf feed lost - reverting to command line", "OK");
                        }
                        if (shown && inDrone)
                        {
                            ReadHudTop();             // heading + AGL
                            DetectHorizon();          // bank the artificial horizon
                            int st = DroneStyle();    // 1 MAVIC / 0 FPV / -1 unknown - only a positive read may flip
                            if (st >= 0 && (st == 1) != _hudStyleUav)
                            {
                                _hudStyleUav = (st == 1);
                                Log("   drone on screen -> " + (_hudStyleUav ? "UAV (DJI)" : "FPV") + " HUD");
                            }
                        }
                    }

                    // ---- fast path: ease toward the target, so when the distance is climbing
                    // the static creeps up and when it is dropping it creeps down. The trend
                    // is what drives it; no extra screenshots in between. ----
                    shownLvl += (targetLvl - shownLvl) * 0.08f;
                    // the game's own FLIGHT clock when we have read it, ticking between reads;
                    // otherwise fall back to our own elapsed count since Deploy As Drone
                    int secs;
                    if (_flightOcrBase >= 0) secs = _flightOcrBase + (int)((Environment.TickCount - _flightOcrAt) / 1000);
                    else secs = (int)(DateTime.Now - _flightStart).TotalSeconds;
                    if (secs < 0) secs = 0;
                    float lvl = shownLvl + 0.02f * (float)Math.Sin(secs * 0.7);
                    if (lvl < 0.02f) lvl = 0.02f;
                    if (lvl > 1f) lvl = 1f;
                    // MAVIC is an HD digital feed - no analog RF static on it
                    bool staticOn = !_hudStyleUav;
                    // report the REAL feed state - it used to always say "flight:true" even after
                    // the drone view was lost, so the OBS side never saw the feed go away
                    OverlayHub.I.SetFlight(shown, staticOn ? lvl : 0f, secs);

                    // only touch the window when something actually changed; the 180ms blink
                    // timer does the repaint, so the overlay is not recomposited every tick
                    bool rfVis = shown && _watchHome && staticOn;   // RF feed = analog only (not MAVIC)
                    if (Math.Abs(lvl - lastSet) > 0.01f || txt != lastTxt || rfVis != lastOk)
                    {
                        lastSet = lvl; lastTxt = txt; lastOk = rfVis;
                        bool ok = rfVis;
                        float lc = lvl;
                        string tc = txt;
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (_rfForm != null)
                                {
                                    _rfForm.Visible = ok;
                                    _rfForm.Static = lc;
                                    _rfForm.Readout = tc;
                                }
                            });
                        }
                        catch { }
                    }

                    if (_rfForm != null && _rfForm.UlwFailed && !_ulwLogged)
                    { _ulwLogged = true; Log("RF overlay: UpdateLayeredWindow failed - static will not show"); }

                    // ---- horizon measurement (~3x/s) + spawn lock. The smooth 50fps fusion runs
                    // in HudTick on the UI thread so it can drive the layered repaint. ----
                    if (shown && Environment.TickCount - lastDet >= 250)
                    {
                        lastDet = Environment.TickCount;
                        DetectHorizon();
                    }
                    if (!_hudLocked && (_hudNeedLock || shown) && Environment.TickCount >= lockArmedAt)
                    {
                        DetectHorizon();
                        if (_hudDetValid)
                        {
                            _hudLocked = true;
                            _hudNeedLock = false;
                            _hudFRoll = _hudDetRoll;
                            _hudFPitch = _hudDetPitch;
                            Log("   horizon locked at spawn: roll " + _hudFRoll.ToString("0") +
                                " deg, pitch " + _hudFPitch.ToString("0") + "px");
                        }
                    }
                    _hudShown = shown;   // HudTick reads these
                    _hudSecs = secs;
                    }
                    catch (Exception ex) { Log("RF loop error: " + ex.Message); }
                    Thread.Sleep(120);
                }
            });
            t.IsBackground = true;
            t.Start();

            _rfBlink = new System.Windows.Forms.Timer();
            _rfBlink.Interval = 180;   // slower flicker = much less full-screen compositing
            _rfBlink.Tick += delegate { if (_rfForm != null) { _rfForm.Phase++; _rfForm.Render(); } };
            _rfBlink.Start();
            Log("RF watch started (HOME distance)");
        }
        catch { }
    }

    void StopRfWatch()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { StopRfWatch(); }); return; }
            _rfRun = false;
            _hudShown = false;
            OverlayHub.I.SetHome("");
            OverlayHub.I.SetFlight(false, 0f, 0);
            OverlayHub.I.SetHud(false, "", "", "", 0f, 0f, false);
            if (_rfBlink != null) { _rfBlink.Stop(); _rfBlink.Dispose(); _rfBlink = null; }
            if (_rfForm != null) { _rfForm.Close(); _rfForm = null; }
            StopHud();
            Log("RF watch stopped");
        }
        catch { }
    }

    // THE single "are we in a drone view" test. The drone OSD always shows "RC LIVE" (MAVIC) or
    // "LINK LIVE" (FPV) in the TOP-RIGHT corner. That combination - "LIVE" AND ("RC" or "LINK") -
    // in that corner is specific to the drone: menus, chat, the nav bar and the loadout never have
    // it there. Everything that decides the HUD/feed goes through this.
    bool CornerLinked()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width;
            string all = "";
            List<string[]> ws = OcrMaskedRegion(W * 78 / 100, 2, W, 130, 3);
            if (ws != null) foreach (string[] w in ws) all += (w[4] ?? "").ToUpperInvariant() + " ";
            if (CornerOk(all)) return true;
            // plain read of the SAME corner (white text over pale sky can defeat the whiten mask)
            string all2 = "";
            foreach (string[] w in OcrWords())
            {
                int wx, wy;
                try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
                if (wx < W * 78 / 100 || wy > 130) continue;
                all2 += (w[4] ?? "").ToUpperInvariant() + " ";
            }
            return CornerOk(all2);
        }
        catch { return false; }
    }

    static bool CornerOk(string all)
    {
        if (string.IsNullOrEmpty(all)) return false;
        return all.IndexOf("LIVE") >= 0 && (all.IndexOf("RC") >= 0 || all.IndexOf("LINK") >= 0);
    }

    bool IsLinked() { return CornerLinked(); }

    // Drone-ONLY words: "AGL" (the altitude readout) and "MOUNTED" (the payload line). Neither
    // appears on the nav bar, the loadout, the map or any menu, so they are safe "we are in the
    // drone" tells. Scanned anywhere on screen, plain + whiten.
    bool DroneKeyword()
    {
        try
        {
            foreach (string[] w in OcrWords())
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                if (t.IndexOf("AGL") >= 0 || t.IndexOf("MOUNTED") >= 0) return true;
            }
        }
        catch { }
        try
        {
            foreach (string[] w in OcrWordsWhiten())
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                if (t.IndexOf("AGL") >= 0 || t.IndexOf("MOUNTED") >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    // Words that ONLY ever appear on the nav bar, the lobby, the loadout, the map or the panels -
    // NEVER on the drone OSD. Seeing any one of them is proof we are not flying, so the HUD comes
    // down immediately; the 30s timeout only covers the case where the OSD simply reads blank.
    bool MenuOnScreen()
    {
        string[] keys = new string[] {
            "LOADOUT", "SETTINGS", "DEPLOY", "WARHEAD", "SQUAD", "FEATURED",
            "COMPLETED", "GHILLE", "GRILLE", "SECONDARY", "PRIMARY", "MARKSMAN",
            "EQUIPMENT", "CUSTOMIZATION", "CHANGE TEAM", "TEAM BASE", "SELECT DRONE", "TOP KILLS",
            // team select / respawn / loading / spectating - these screens have NO nav bar, so the
            // nav words above never matched and the HUD used to linger on them for the full 30s
            "PLAYERS", "JOINING", "RESPAWN", "SPECTAT", "DEPLOYING"
        };
        try
        {
            foreach (string[] w in OcrWords())
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                for (int i = 0; i < keys.Length; i++) if (t.IndexOf(keys[i]) >= 0) return true;
            }
        }
        catch { }
        try
        {
            foreach (string[] w in OcrWordsWhiten())
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                for (int i = 0; i < keys.Length; i++) if (t.IndexOf(keys[i]) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    // The game prints the payload bottom-right ("LIGHT ROCKET MOUNTED"). Rockets / RPG-type
    // payloads mean the FPV drone, rack/grenade payloads mean the MAVIC - so the OSD itself
    // tells us which HUD to draw, no matter what the dropdown says.
    // The top-right corner literally names the drone ("RC LIVE" over "MAVIC" / "FPV"). Prefer it
    // as the source of truth and fall back to the payload line only if it is unreadable.
    bool DroneLooksMavic()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width;
            List<string[]> ws = OcrMaskedRegion(W * 80 / 100, 4, W, 120, 3);
            string dbg = DumpWords(ws);
            if (dbg != _lastDroneDbg) { _lastDroneDbg = dbg; Log("   top-right OCR: " + dbg); }
            if (ws != null)
                foreach (string[] w in ws)
                {
                    string t = (w[4] ?? "").ToUpperInvariant();
                    if (t.IndexOf("MAVIC") >= 0 || t.IndexOf("MAV") >= 0 || t.IndexOf("MAV1C") >= 0) return true;
                    if (t.IndexOf("FPV") >= 0 || t.IndexOf("FPY") >= 0) return false;
                }
        }
        catch { }
        return PayloadLooksMavic();
    }

    bool PayloadLooksMavic()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            // wider band so the payload STATUS line ("3 grenades ready" / "Payload empty") is caught
            List<string[]> ws = OcrMaskedRegion(W * 62 / 100, H - 95 * H / 1080, W, H - 4 * H / 1080, 3);
            string all = "";
            if (ws != null)
                foreach (string[] w in ws) all += (w[4] ?? "").ToUpperInvariant() + " ";
            // check MAVIC keywords FIRST - the old guard bailed out before ever looking for
            // GRENADE/RACK, so "3 grenades ready" never switched the style
            foreach (string k in new string[] { "RGO", "RACK", "GRENADE", "M67" })
                if (all.IndexOf(k) >= 0) return true;    // MAVIC
            foreach (string k in new string[] { "ROCKET", "PG-7", "PG7", "TBG", "THERMO", "SHAPED", "FRAG", "RPG" })
                if (all.IndexOf(k) >= 0) return false;   // FPV
            return _hudStyleUav;                         // nothing readable - keep the current style
        }
        catch { return _hudStyleUav; }
    }

    // Which drone, from the clock LABEL alone: MAVIC prints "FLIGHT mm:ss" (top-left),
    // FPV prints "FLY mm:ss" (bottom-right). 1 = MAVIC, 0 = FPV, -1 = not read.
    int StyleByClock()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            string a = "";
            List<string[]> wa = OcrMaskedRegion(2, 2, 340 * W / 1920, 64 * H / 1080, 4);
            if (wa != null) foreach (string[] w in wa) a += (w[4] ?? "").ToUpperInvariant() + " ";
            if (a.IndexOf("FLIGHT") >= 0 || a.IndexOf("FL1GHT") >= 0) return 1;
            if (a.IndexOf("FLY") >= 0) return 0;

            string b = "";
            List<string[]> wb = OcrMaskedRegion(W * 88 / 100, H * 88 / 100, W, H - 2, 3);
            if (wb != null) foreach (string[] w in wb) b += (w[4] ?? "").ToUpperInvariant() + " ";
            if (b.IndexOf("FLIGHT") >= 0 || b.IndexOf("FL1GHT") >= 0) return 1;
            if (b.IndexOf("FLY") >= 0) return 0;
        }
        catch { }
        return -1;
    }

    // 1 = MAVIC, 0 = FPV, -1 = could not tell. Only a POSITIVE read may change the HUD style, so a
    // failed read can never flip a MAVIC into the FPV layout (which is what happened in the drone).
    int DroneStyle()
    {
        // 0) the CLEANEST tell, no overlap: the flight clock label. MAVIC prints "FLIGHT mm:ss",
        //    FPV prints "FLY mm:ss". "FLY" is not part of "FLIGHT", so these are unambiguous.
        int byClock = StyleByClock();
        if (byClock >= 0) return byClock;
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width;
            List<string[]> ws = OcrMaskedRegion(W * 78 / 100, 2, W, 130, 3);
            string dbg = DumpWords(ws);
            if (dbg != _lastDroneDbg) { _lastDroneDbg = dbg; Log("   top-right OCR: " + dbg); }
            if (ws != null)
                foreach (string[] w in ws)
                {
                    string t = (w[4] ?? "").ToUpperInvariant();
                    if (t.IndexOf("MAVIC") >= 0 || t.IndexOf("MAV") >= 0 || t.IndexOf("MAV1C") >= 0) return 1;
                    if (t.IndexOf("FPV") >= 0 || t.IndexOf("FPY") >= 0) return 0;
                }
            // plain read of the same corner - the white drone name over pale sky defeats the mask
            string all2 = "";
            foreach (string[] w in OcrWords())
            {
                int wx, wy;
                try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
                if (wx < W * 78 / 100 || wy > 150) continue;
                all2 += (w[4] ?? "").ToUpperInvariant() + " ";
            }
            if (all2.IndexOf("MAVIC") >= 0 || all2.IndexOf("MAV") >= 0) return 1;
            if (all2.IndexOf("FPV") >= 0 || all2.IndexOf("FPY") >= 0) return 0;
        }
        catch { }
        // fall back to the payload line - return -1 when unreadable so nothing changes
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            List<string[]> ws = OcrMaskedRegion(W * 62 / 100, H - 95 * H / 1080, W, H - 4 * H / 1080, 3);
            string all = "";
            if (ws != null) foreach (string[] w in ws) all += (w[4] ?? "").ToUpperInvariant() + " ";
            foreach (string k in new string[] { "RGO", "RACK", "GRENADE", "M67" }) if (all.IndexOf(k) >= 0) return 1;
            foreach (string k in new string[] { "ROCKET", "PG-7", "PG7", "TBG", "THERMO", "SHAPED", "FRAG", "RPG" }) if (all.IndexOf(k) >= 0) return 0;
        }
        catch { }
        return -1;
    }

    static bool LinkWords(List<string[]> ws)   // retained for any legacy callers
    {
        if (ws == null) return false;
        string all = "";
        foreach (string[] w in ws) all += (w[4] ?? "").ToUpperInvariant() + " ";
        return CornerOk(all);
    }

    // The crash screen is a big white "NO SIGNAL" on light grey - the plain read sees it, the
    // whiten pass does not (bright background). Returns true if both words are present.
    bool IsNoSignal()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            bool no = false, sig = false;
            foreach (string[] w in OcrWords())
            {
                // the crash screen is big text in the MIDDLE of the view - only trust words there,
                // so a stray "no"/"signal" in the UI, nav bar or another window cannot match
                int wx, wy;
                try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
                if (wx < W * 25 / 100 || wx > W * 75 / 100) continue;
                if (wy < H * 28 / 100 || wy > H * 72 / 100) continue;
                string t = (w[4] ?? "").ToUpperInvariant();
                if (t.IndexOf("SIGNAL") >= 0) sig = true;
                if (t == "NO" || t.IndexOf("NOSIG") >= 0) no = true;
            }
            return sig && no;
        }
        catch { return false; }
    }

    // Find the HOME readout on the HUD. Looks for the word HOME and then a nearby token with
    // digits (the metres). Returns null if nothing is read. The drone HUD is white text over
    // bright snow, so the PLAIN read works where the whiten pass (built for dark backgrounds)
    // mangles it - so plain first, whiten only as a fallback.
    string FindHomeDistance()
    {
        string r = FindHomeIn(OcrWords());
        if (r == null) r = ReadHomeHud();          // crop the HUD and isolate the text
        if (r == null) r = FindHomeIn(OcrWordsWhiten());
        return r;
    }

    // The drone HUD (SPD/ALT/HOME/V-S) is white text with a black outline drawn at a fixed
    // spot in the bottom-left. Whole-screen OCR misses it once the terrain behind it is bright
    // or busy. So crop just that block, keep only near-white pixels that have a dark outline
    // pixel beside them, thicken them by a pixel, upscale and read - text stays, snow/grass go.
    // Crop a screen rectangle, keep only near-white text pixels that have a dark outline
    // pixel beside them, thicken them 1px, upscale xS and OCR. Returns the OCR words with
    // coordinates scaled back to the (unscaled) crop, or null when OCR is unavailable.
    List<string[]> OcrMaskedRegion(int x0, int y0, int x1, int y1, int S)
    {
        string shot = null;
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
            if (x1 > W) x1 = W; if (y1 > H) y1 = H;
            int cw = x1 - x0, ch = y1 - y0;
            if (cw < 8 || ch < 8) return null;

            byte[] gray = new byte[cw * ch];
            bool[] bright = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int c = px[(y0 + y) * W + (x0 + x)];
                    int b = c & 0xFF, g = (c >> 8) & 0xFF, r = (c >> 16) & 0xFF;
                    int i = y * cw + x;
                    gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    bright[i] = r > 190 && g > 190 && b > 190;
                }
            bool[] txt = new bool[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int i = y * cw + x;
                    if (!bright[i]) continue;
                    bool outline = false;
                    for (int dy = -1; dy <= 1 && !outline; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                            if (gray[ny * cw + nx] < 90) { outline = true; break; }
                        }
                    if (outline) { txt[i] = true; continue; }
                    // No dark outline: white text over a LIGHT background (sky, grass, the green
                    // TEAM BASE title). Fall back to local contrast - a glyph pixel is noticeably
                    // brighter than the 9x9 around it. This is what makes those reads work.
                    int sum = 0, cnt = 0;
                    for (int dy = -4; dy <= 4; dy++)
                    {
                        int ny = y + dy; if (ny < 0 || ny >= ch) continue;
                        for (int dx = -4; dx <= 4; dx++)
                        {
                            int nx = x + dx; if (nx < 0 || nx >= cw) continue;
                            sum += gray[ny * cw + nx]; cnt++;
                        }
                    }
                    if (cnt > 0 && gray[i] - sum / cnt > 18) txt[i] = true;
                }
            int OW = cw * S, OH = ch * S;
            int[] outPx = new int[OW * OH];
            int white = unchecked((int)0xFFFFFFFF), black = unchecked((int)0xFF000000);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    bool on = txt[y * cw + x];
                    if (!on)
                        for (int dy = -1; dy <= 1 && !on; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                                if (txt[ny * cw + nx]) { on = true; break; }
                            }
                    int col = on ? white : black;
                    int ox = x * S, oy = y * S;
                    for (int yy = 0; yy < S; yy++)
                    {
                        int row = (oy + yy) * OW + ox;
                        for (int xx = 0; xx < S; xx++) outPx[row + xx] = col;
                    }
                }

            shot = Path.Combine(Path.GetTempPath(),
                "robloxauto-hud-" + System.Threading.Thread.CurrentThread.ManagedThreadId +
                "-" + (++_ocrShotSeq) + ".bmp");
            using (Bitmap bmp = new Bitmap(OW, OH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, OW, OH),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(outPx, 0, bd.Scan0, outPx.Length);
                bmp.UnlockBits(bd);
                bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Bmp);
            }

            List<string[]> raw = OcrServerRead(shot);
            if (raw == null) return null;
            foreach (string[] p in raw)
                for (int k = 0; k < 4; k++)
                    try { p[k] = (int.Parse(p[k]) / S).ToString(); } catch { }
            return raw;
        }
        catch (Exception e) { Log("HUD region read failed: " + e.Message); return null; }
        finally { try { if (shot != null && File.Exists(shot)) File.Delete(shot); } catch { } }
    }

    string ReadHomeHud()
    {
        int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
        // start at the very left: the labels ("SPD"/"ALT"/"HOME") begin at the screen edge, and
        // cropping at x=30 chopped the H of HOME off so the label never matched
        List<string[]> ws = OcrMaskedRegion(6 * W / 1920, H - 120 * H / 1080,
                                           260 * W / 1920, H - 4 * H / 1080, 4);
        if (ws == null) return null;
        return HomeFromHud(ws, 1);
    }

    // Read the bottom-left stats block (HOME returned; SPD/ALT into fields). Runs on its own,
    // faster cadence so the HOME distance updates quickly.
    string ReadHudBottom()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            List<string[]> bot = OcrMaskedRegion(6 * W / 1920, H - 120 * H / 1080,
                                                 260 * W / 1920, H - 4 * H / 1080, 4);

            string dbg = DumpWords(bot);
            if (dbg != _lastHudDbg) { _lastHudDbg = dbg; Log("   HUD read: " + dbg); }

            string home = bot != null ? HomeFromHud(bot, 1) : null;
            if (home == null) home = FindHomeIn(OcrWords());          // full-screen fallback 1
            if (home == null) home = FindHomeIn(OcrWordsWhiten());    // and the whitened pass
            _hudHome = home != null ? home : "----";
            if (bot != null)
            {
                _hudSpd = ValueAfterLabel(bot, "SPD");
                _hudAlt = ValueAfterLabel(bot, "ALT");
            }
            if (_hudSpd == "") _hudSpd = ValueAfterLabel(OcrWords(), "SPD");
            if (_hudAlt == "") _hudAlt = ValueAfterLabel(OcrWords(), "ALT");
            return home;
        }
        catch { return null; }
    }

    // MAVIC OSD is laid out differently: "AGL <n> m" on its own line, then a row of four numbers
    // under the labels H / D / H.S / V.S (height, distance to HOME, horizontal & vertical speed).
    // The FPV labels (SPD/ALT/HOME) never appear, so read the row left-to-right instead.
    string ReadMavicBottom()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            List<string[]> ws = OcrMaskedRegion(4 * W / 1920, H - 140 * H / 1080,
                                                620 * W / 1920, H - 4 * H / 1080, 2);
            if (ws == null) return null;
            string dbg = DumpWords(ws);
            if (dbg != _lastHudDbg) { _lastHudDbg = dbg; Log("   MAVIC HUD read: " + dbg); }

            string agl = ValueAfterLabel(ws, "AGL");
            if (agl != "") _hudAgl = agl;

            int maxY = -1;
            foreach (string[] w in ws) { int wy; try { wy = int.Parse(w[1]); } catch { continue; } if (wy > maxY) maxY = wy; }
            List<string[]> row = new List<string[]>();
            foreach (string[] w in ws)
            {
                int wy; try { wy = int.Parse(w[1]); } catch { continue; }
                if (Math.Abs(wy - maxY) > 24) continue;
                row.Add(w);
            }
            row.Sort(delegate (string[] a, string[] b) { return int.Parse(a[0]).CompareTo(int.Parse(b[0])); });
            List<string> nums = new List<string>();
            foreach (string[] w in row)
            {
                string n = NumFromToken(w[4] ?? "");
                if (n != null) nums.Add(n);
            }
            if (nums.Count >= 3)
            {
                _hudAlt = nums[0];              // H   = height
                _hudHome = nums[1] + " m";      // D   = distance to HOME
                _hudSpd = nums[2];              // H.S = horizontal speed
            }
            return _hudHome;
        }
        catch { return null; }
    }

    // top-centre strip: heading + AGL (slower cadence)
    void ReadHudTop()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width;
            List<string[]> top = OcrMaskedRegion(W * 42 / 100, 14, W * 58 / 100, 100, 3);
            if (top != null)
            {
                _hudAgl = ValueAfterLabel(top, "AGL");
                _hudHdg = HeadingFrom(top);
            }
        }
        catch { }
    }

    // HOME cannot change much between reads (the drone is ~20 m/s), so a wildly different number
    // is an OCR misread - keep the previous one instead of jumping. Gives up and accepts after
    // 8s so a genuine reset (new drone) is not blocked forever.
    string FilterHome(string d)
    {
        if (d == null) return null;
        float m = HomeMetres(d);
        if (m < 0f) return null;
        long now = Environment.TickCount;
        if (_homeLastM >= 0f)
        {
            long dt = now - _homeLastAt;
            float maxJump = 120f + 0.12f * dt;      // comfortably above the drone's travel speed
            if (Math.Abs(m - _homeLastM) > maxJump && dt < 8000)
            {
                Log("   HOME " + _homeLastM.ToString("0") + " -> " + m.ToString("0") + " looks wrong - keeping " + _homeLastText);
                return _homeLastText;
            }
        }
        _homeLastM = m; _homeLastAt = now; _homeLastText = d;
        return d;
    }

    static string DumpWords(List<string[]> ws)
    {
        if (ws == null) return "(null)";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (string[] w in ws)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(w.Length > 4 ? w[4] : "?");
        }
        return sb.ToString();
    }

    // Top-left "FLIGHT mm:ss" - the game's own flight clock, used for the HUD timer + battery.
    // Returns seconds, or -1 when unreadable (caller keeps the local elapsed count).
    int ReadFlightSecs()
    {
        try
        {
            int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
            // MAVIC prints "FLIGHT mm:ss" top-left; FPV prints "FLY mm:ss" BOTTOM-RIGHT. Read both.
            int v = ParseFlight(OcrMaskedRegion(2, 2, 340 * W / 1920, 64 * H / 1080, 4));
            if (v < 0) v = ParseFlight(OcrMaskedRegion(W * 88 / 100, H * 88 / 100, W, H - 2, 3));
            return v;
        }
        catch { return -1; }
    }

    static int ParseFlight(List<string[]> ws)
    {
        if (ws == null) return -1;
        string all = "";
        foreach (string[] w in ws) all += (w[4] ?? "") + " ";
        string up = all.ToUpperInvariant();
        bool label = up.IndexOf("FLIGHT") >= 0 || up.IndexOf("FLY") >= 0 ||
                     up.IndexOf("FL1GHT") >= 0 || up.IndexOf("FLGHT") >= 0 || up.IndexOf("FLIG") >= 0;
        // mm:ss (colon may be dropped - then fall back to 4 digits, but only next to a FLIGHT label)
        Match m = Regex.Match(all, @"(\d{1,2})\s*[:.;]\s*(\d{2})");
        if (m.Success) return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        if (label)
        {
            Match d = Regex.Match(all, @"(\d{1,2})\s?(\d{2})\b");
            if (d.Success) return int.Parse(d.Groups[1].Value) * 60 + int.Parse(d.Groups[2].Value);
        }
        return -1;
    }

    static string ValueAfterLabel(List<string[]> ws, string label)
    {
        int lx = -1, ly = -1;
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            if (t.IndexOf(label) >= 0 || SimPct(t, label) >= 70)
            { try { lx = int.Parse(w[0]); ly = int.Parse(w[1]); } catch { continue; } break; }
        }
        if (lx < 0) return "";
        string best = "";
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            if (NumFromToken(t) == null) continue;
            int wx, wy;
            try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
            if (Math.Abs(wy - ly) > 16) continue;
            if (wx < lx) continue;
            best = NumFromToken(t);
            if (t.IndexOf('.') >= 0) break;
        }
        return best;
    }

    static string HeadingFrom(List<string[]> ws)
    {
        int bestY = int.MaxValue; string best = "";
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            string n = NumFromToken(t);
            if (n == null) continue;
            int v; if (!int.TryParse(n, out v)) continue;
            if (v < 0 || v > 399) continue;          // the OSD can print e.g. 364
            if (v > 359) v -= 360;
            int wy;
            try { wy = int.Parse(w[1]); } catch { continue; }
            if (wy < bestY) { bestY = wy; best = v.ToString(); }
        }
        return best;
    }

    // The horizon is the strongest near-horizontal edge in the upper frame. Fit a line through
    // the per-column edge points to get bank (roll) and the vertical offset (pitch). Only accept
    // a confident fit, otherwise ease back to level so a forest filling the frame does not
    // throw the HUD around.
    // Real horizon lock. For each column we find the strongest vertical edge (the sky/ground
    // boundary) in the upper frame, then fit a line with RANSAC instead of plain least squares -
    // trees, buildings and the HUD markers are outliers, and least squares let them drag the line
    // off. RANSAC finds the line the majority of columns agree on, which is the actual horizon.
    void DetectHorizon()
    {
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            int yTop = H / 12, yBot = H * 80 / 100;

            // Sky reference = mean colour of the TOP BAND of the current frame. Sampling it per
            // frame is what makes this survive map changes: hazy, dusk, snow and night maps all
            // get their own reference instead of relying on a hard-coded brightness step.
            int sr = 0, sg = 0, sb = 0, sn = 0;
            for (int y = 4; y < H / 14; y += 2)
                for (int x = W / 10; x < W * 9 / 10; x += 8)
                {
                    int c = px[y * W + x];
                    sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF; sn++;
                }
            if (sn == 0) { _hudDetValid = false; return; }
            sr /= sn; sg /= sn; sb /= sn;

            int maxPts = W / 4 + 4;
            float[] pxs = new float[maxPts], pys = new float[maxPts];
            int n = 0;
            bool gOk = false; float slope = 0f, icept = 0f;

            // --- primary: GLOBAL line search. Score every candidate near-horizontal line by how
            // much "sky" (close to the top-band reference) sits above it and "ground" (far from it)
            // sits below, averaged across the whole width. Unlike a per-column threshold this stays
            // put at low altitude, where the ground fills the view and the haze horizon is faint.
            // Coarse sweep, then a fine sweep around the winner. ---
            {
                float bestScore = 0f; int bm10 = 0, bb = 0;
                for (int m10 = -30; m10 <= 30; m10 += 5)
                {
                    float m = m10 / 100f;
                    for (int b = 40; b <= H - 40; b += 20)
                    {
                        float tot = 0f; int cnt = 0;
                        for (int x = W / 12; x < W * 11 / 12; x += 40)
                        {
                            int yl = (int)(m * x + b);
                            if (yl - 14 < 0 || yl + 14 >= H) continue;
                            int above = 0, below = 0;
                            for (int kk = 1; kk <= 14; kk++)
                            {
                                int c1 = px[(yl - kk) * W + x];
                                above += Math.Abs(((c1 >> 16) & 0xFF) - sr) + Math.Abs(((c1 >> 8) & 0xFF) - sg) + Math.Abs((c1 & 0xFF) - sb);
                                int c2 = px[(yl + kk) * W + x];
                                below += Math.Abs(((c2 >> 16) & 0xFF) - sr) + Math.Abs(((c2 >> 8) & 0xFF) - sg) + Math.Abs((c2 & 0xFF) - sb);
                            }
                            tot += below - above - (above >> 2); cnt++;   // sky above weighted 1.25x (was 1.5 - locked too high)
                        }
                        if (cnt >= 12) { float s2 = tot / cnt; if (s2 > bestScore) { bestScore = s2; bm10 = m10; bb = b; } }
                    }
                }
                if (bestScore > 22f)
                {
                    float fm = bm10 / 100f;
                    for (int m10 = bm10 - 4; m10 <= bm10 + 4; m10++)
                    {
                        float m = m10 / 100f;
                        for (int b = bb - 20; b <= bb + 20; b += 2)
                        {
                            if (b < 40 || b > H - 40) continue;
                            float tot = 0f; int cnt = 0;
                            for (int x = W / 12; x < W * 11 / 12; x += 16)
                            {
                                int yl = (int)(m * x + b);
                                if (yl - 14 < 0 || yl + 14 >= H) continue;
                                int above = 0, below = 0;
                                for (int kk = 1; kk <= 14; kk++)
                                {
                                    int c1 = px[(yl - kk) * W + x];
                                    above += Math.Abs(((c1 >> 16) & 0xFF) - sr) + Math.Abs(((c1 >> 8) & 0xFF) - sg) + Math.Abs((c1 & 0xFF) - sb);
                                    int c2 = px[(yl + kk) * W + x];
                                    below += Math.Abs(((c2 >> 16) & 0xFF) - sr) + Math.Abs(((c2 >> 8) & 0xFF) - sg) + Math.Abs((c2 & 0xFF) - sb);
                                }
                                tot += below - above - (above >> 2); cnt++;   // sky above weighted 1.25x (was 1.5 - locked too high)
                            }
                            if (cnt >= 12) { float s2 = tot / cnt; if (s2 > bestScore) { bestScore = s2; slope = m; icept = b; gOk = true; } }
                        }
                    }
                    if (!gOk) { slope = fm; icept = bb; gOk = true; }
                }
            }

            // --- fallback (only if the global search found nothing, e.g. looking straight down):
            //     per-column edges + RANSAC ---
            if (!gOk)
            {
                // method A: GREEN dominance step
                for (int x = W / 12; x < W * 11 / 12 && n < maxPts; x += 4)
                {
                    for (int y = yTop + 8; y + 24 < yBot; y += 2)
                    {
                        int above = 0, below = 0;
                        for (int kk = 1; kk <= 8; kk++) { int c = px[(y - kk) * W + x]; above += ((c >> 8) & 0xFF) - (c & 0xFF); }
                        for (int kk = 0; kk < 8; kk++) { int c = px[(y + kk) * W + x]; below += ((c >> 8) & 0xFF) - (c & 0xFF); }
                        if (below - above > 100) { pxs[n] = x; pys[n] = y; n++; break; }
                    }
                }
                // method B: signed brightness step
                if (n < 40)
                {
                    n = 0;
                    for (int x = W / 12; x < W * 11 / 12 && n < maxPts; x += 4)
                    {
                        int bestY = -1, bestG = 32;
                        for (int y = yTop; y < yBot; y += 2)
                        {
                            int c1 = px[(y - 4) * W + x], c2 = px[(y + 4) * W + x];
                            int g1 = ((c1 >> 8) & 0xFF) + (c1 & 0xFF) + ((c1 >> 16) & 0xFF);
                            int g2 = ((c2 >> 8) & 0xFF) + (c2 & 0xFF) + ((c2 >> 16) & 0xFF);
                            int d = g1 - g2;
                            if (d > bestG) { bestG = d; bestY = y; }
                        }
                        if (bestY >= 0) { pxs[n] = x; pys[n] = bestY; n++; }
                    }
                }
                if (n < 30)
                {
                    _hudDetValid = false; _hudDetRoll = 0f; _hudDetPitch = 0f;
                    if (Environment.TickCount - _hudLogAt >= 2000) { _hudLogAt = Environment.TickCount; Log("horizon det: no global line, too few points (" + n + ") - no lock"); }
                    return;
                }
                Random rng = new Random();
                float bs = 0f, bi = 0f; int bestIn = -1;
                for (int it = 0; it < 240; it++)
                {
                    int i1 = rng.Next(n), i2 = rng.Next(n);
                    if (i1 == i2) continue;
                    float x1 = pxs[i1], y1 = pys[i1], x2 = pxs[i2], y2 = pys[i2];
                    if (Math.Abs(x2 - x1) < 60f) continue;
                    float m = (y2 - y1) / (x2 - x1);
                    if (m > 0.8f || m < -0.8f) continue;
                    float b = y1 - m * x1;
                    int inl = 0;
                    for (int i = 0; i < n; i++)
                        if (Math.Abs(pys[i] - (m * pxs[i] + b)) < 8f) inl++;
                    if (inl > bestIn) { bestIn = inl; bs = m; bi = b; }
                }
                if (bestIn < n * 40 / 100)
                {
                    _hudDetValid = false; _hudDetRoll = 0f; _hudDetPitch = 0f;
                    if (Environment.TickCount - _hudLogAt >= 2000) { _hudLogAt = Environment.TickCount; Log("horizon det: weak line (" + bestIn + "/" + n + " inliers) - no lock"); }
                    return;
                }
                float sx = 0, sy = 0, sxy = 0, sxx = 0; int ck = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Math.Abs(pys[i] - (bs * pxs[i] + bi)) >= 8f) continue;
                    sx += pxs[i]; sy += pys[i]; sxy += pxs[i] * pys[i]; sxx += pxs[i] * pxs[i]; ck++;
                }
                float den = ck * sxx - sx * sx;
                if (ck < 20 || Math.Abs(den) < 1f) { _hudDetValid = false; return; }
                slope = (ck * sxy - sx * sy) / den;
                icept = (sy - slope * sx) / ck;
            }

            // --- refinement: snap each column to the first row within +-20px of the line where the
            //     colour clearly departs from the sky reference, then refit. Tightens the coarse
            //     (20px step) global search onto the actual edge. ---
            float rsx = 0, rsy = 0, rsxy = 0, rsxx = 0; int rk = 0;
            for (int x = W / 12; x < W * 11 / 12; x += 2)
            {
                int cy = (int)(slope * x + icept);
                int lo = cy - 20, hi = cy + 20;
                if (lo < yTop + 8) lo = yTop + 8;
                if (hi > yBot - 24) hi = yBot - 24;
                for (int y = lo; y <= hi; y++)
                {
                    int c = px[y * W + x];
                    int d2 = Math.Abs(((c >> 16) & 0xFF) - sr) + Math.Abs(((c >> 8) & 0xFF) - sg) + Math.Abs((c & 0xFF) - sb);
                    if (d2 > 36) { rsx += x; rsy += y; rsxy += x * y; rsxx += x * x; rk++; break; }
                }
            }
            if (rk >= 40)
            {
                float rden = rk * rsxx - rsx * rsx;
                if (Math.Abs(rden) > 1f)
                {
                    slope = (rk * rsxy - rsx * rsy) / rden;
                    icept = (rsy - slope * rsx) / rk;
                }
            }

            float roll = (float)(Math.Atan(slope) * 180.0 / Math.PI);
            if (roll > 45f) roll = 45f; if (roll < -45f) roll = -45f;
            float pitch = (slope * (W / 2f) + icept) - H / 2f;
            pitch += _hudBias;   // tunable downward bias (dial "bias px")
            if (pitch > H / 4f) pitch = H / 4f; if (pitch < -H / 4f) pitch = -H / 4f;
            // smooth across measurements (measurements are only ~3/s, so a single noisy fit would
            // make the lock twitch) - seeded on the first good fix so it is not biased to 0
            if (!_hudSmSeeded) { _hudSmRoll = roll; _hudSmPitch = pitch; _hudSmSeeded = true; }
            else
            {
                _hudSmRoll = _hudSmRoll * 0.55f + roll * 0.45f;
                _hudSmPitch = _hudSmPitch * 0.55f + pitch * 0.45f;
            }
            _hudDetRoll = _hudSmRoll;
            _hudDetPitch = _hudSmPitch;
            _hudDetValid = true;
            _hudDetAt = Environment.TickCount;
            if (Environment.TickCount - _hudLogAt >= 2000)
            {
                _hudLogAt = Environment.TickCount;
                Log("horizon det: " + (gOk ? "global" : n + " pts") + ", roll " + roll.ToString("0") + " deg, pitch " + pitch.ToString("0") + " px");
            }
        }
        catch { }
    }

    // "0.2 m" / "560.7 m" / "1.20 km" -> metres as a float, or -1 when nothing readable
    static float HomeMetres(string d)
    {
        if (d == null) return -1f;
        string s = d.ToUpperInvariant();
        string n = NumFromToken(s);
        if (n == null) return -1f;
        float v;
        if (!float.TryParse(n, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return -1f;
        if (s.IndexOf("KM") >= 0) v *= 1000f;
        return v;
    }

    static string NumFromToken(string t)
    {
        string s = (t ?? "").Replace(",", ".");
        int start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { if (start < 0) start = i; }
            else if (c == '.' && start >= 0) { }
            else if (start >= 0) break;
        }
        if (start < 0) return null;
        string n = "";
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') n += c;
            else if (c == '.' && n.IndexOf('.') < 0) n += c;
            else break;
        }
        return n.Length > 0 ? n : null;
    }

    string HomeFromHud(List<string[]> ws, int scale)
    {
        int hx = -1, hy = -1;
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            if (t.IndexOf("HOM") >= 0 || t.IndexOf("H0M") >= 0 || SimPct(t, "HOME") >= 65)
            {
                try { hx = int.Parse(w[0]); hy = int.Parse(w[1]); } catch { continue; }
                break;
            }
        }
        if (hx < 0)
        {
            // The HOME word was missed. Do NOT just take "the 3rd row" - if the rows are grouped
            // wrongly that is the ALT or SPD number, which is exactly the long-range false-flag.
            // Instead locate HOME as the line BETWEEN the ALT and V/S labels; without both of
            // those we refuse to guess at all.
            int altY = LabelY(ws, "ALT");
            int vsY = LabelY(ws, "V/S");
            if (vsY == 0) vsY = LabelY(ws, "VS");
            if (altY <= 0 || vsY <= altY) { Log("   HOME: no label and no ALT/V-S anchors - skipping"); return null; }
            hy = (altY + vsY) / 2;
            hx = 0;
        }

        string best = null;
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            string n = NumFromToken(t);
            if (n == null) continue;
            int wx, wy;
            try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
            if (Math.Abs(wy - hy) > 14 * scale) continue;      // must be on the HOME line
            if (wx < hx - 4 * scale) continue;                 // and to its right
            best = n;
            if (t.IndexOf('.') >= 0) break;
        }
        if (best == null) return null;

        // sanity: HOME is a small non-negative distance. A big or negative number means we grabbed
        // something else on the screen, so drop it rather than show a false distance.
        float v;
        if (!float.TryParse(best, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return null;
        if (v < 0f || v > 3000f) { Log("   HOME: rejected implausible value " + best); return null; }
        return best + " m";
    }

    // y of the first word matching a label ("ALT", "V/S", ...), or 0 when it is not on screen
    static int LabelY(List<string[]> ws, string label)
    {
        foreach (string[] w in ws)
        {
            string t = (w[4] ?? "").ToUpperInvariant();
            if (t.IndexOf(label) >= 0 || SimPct(t, label) >= 65)
            { try { return int.Parse(w[1]); } catch { } }
        }
        return 0;
    }

    string FindHomeIn(List<string[]> ws)
    {
        try
        {
            int homeX = -1, homeY = -1;
            foreach (string[] w in ws)
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                if (t.IndexOf("HOM") >= 0 || SimPct(t, "HOME") >= 70)
                { homeX = int.Parse(w[0]); homeY = int.Parse(w[1]); break; }
            }
            if (homeX < 0) return null;      // no HOME on screen - show nothing rather than a random number

            // the readout is "HOME 560.7 m" on one line, so only accept a number token on the
            // SAME line as HOME and to its right - otherwise the SPD/ALT lines / heading win
            string best = null;
            foreach (string[] w in ws)
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                bool hasDigit = false;
                foreach (char ch in t) if (ch >= '0' && ch <= '9') { hasDigit = true; break; }
                if (!hasDigit) continue;
                int wx = int.Parse(w[0]), wy = int.Parse(w[1]);
                if (Math.Abs(wy - homeY) > 20 || wx < homeX || wx - homeX > 320) continue;
                best = t.EndsWith("M") ? t : t + " m";
                if (t.IndexOf('.') >= 0) break;   // 560.7 - the metres, done
            }
            return best;
        }
        catch { return null; }
    }

    // A borderless overlay that never takes focus and lets clicks pass straight through to the
    // game - used for the "LAND NOW" alert after a reconnect.
    class OverlayForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED (needed for Opacity)
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT - click-through
                return cp;
            }
        }
    }

    // Serves the overlay state to an OBS Browser Source (add http://localhost:8730/ as a
    // browser source). The stream gets an opaque black CLI screen; the on-monitor form stays
    // semi-transparent so you can still see the game.
    class OverlayHub
    {
        public static readonly OverlayHub I = new OverlayHub();
        readonly object _lock = new object();
        readonly List<string[]> _lines = new List<string[]>();
        float _progress = 0f, _target = 0f;
        string _label = "", _home = "";
        string _team = "", _drone = "", _bomb = "";
        bool _active = false;
        bool _flight = false;            // drone deployed - show RF static in the stream overlay
        float _level = 0f;               // static intensity, based on time since deploy
        int _secs = 0;
        bool _hud = false;               // draw the FPV/UAV HUD in the stream overlay
        string _hdg = "", _spd = "", _agl = "";
        float _roll = 0f, _pit = 0f;
        bool _uav = false;
        System.Net.HttpListener _lis;
        System.Windows.Forms.Timer _tick;
        string _dir = "";

        public void Start(string dir)
        {
            _dir = dir;
            try
            {
                _lis = new System.Net.HttpListener();
                _lis.Prefixes.Add("http://localhost:8730/");
                _lis.Start();
                Thread t = new Thread(delegate () { while (true) { try { Serve(_lis.GetContext()); } catch { } } });
                t.IsBackground = true; t.Start();
                _tick = new System.Windows.Forms.Timer();
                _tick.Interval = 30;
                _tick.Tick += delegate { Step(); };
                _tick.Start();
            }
            catch { }
        }

        public void Active(bool a)
        {
            lock (_lock) { _active = a; if (a) { _lines.Clear(); _progress = 0; _target = 0; _label = ""; } }
        }
        public void AddLine(string text, string status) { lock (_lock) _lines.Add(new string[] { text, status ?? "" }); }
        public void SetProgress(float pct, string label) { lock (_lock) { _target = pct; _label = label ?? ""; } }
        public void SetHome(string h) { lock (_lock) _home = h ?? ""; }
        public void SetMission(string team, string drone, string bomb) { lock (_lock) { _team = team ?? ""; _drone = drone ?? ""; _bomb = bomb ?? ""; } }
        public void SetFlight(bool f, float level, int secs) { lock (_lock) { _flight = f; _level = level; _secs = secs; } }
        public void SetHud(bool on, string hdg, string spd, string agl, float roll, float pit, bool uav)
        { lock (_lock) { _hud = on; _hdg = hdg ?? ""; _spd = spd ?? ""; _agl = agl ?? ""; _roll = roll; _pit = pit; _uav = uav; } }
        void Step() { lock (_lock) { _progress += (_target - _progress) * 0.12f; if (Math.Abs(_target - _progress) < 0.002f) _progress = _target; } }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        void Serve(System.Net.HttpListenerContext c)
        {
            string path = c.Request.Url.AbsolutePath;
            string body, ctype;
            if (path == "/state")
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                lock (_lock)
                {
                    sb.Append("{\"active\":").Append(_active ? "true" : "false");
                    sb.Append(",\"progress\":").Append(_progress.ToString("0.###"));
                    sb.Append(",\"label\":\"").Append(Esc(_label)).Append("\"");
                    sb.Append(",\"home\":\"").Append(Esc(_home)).Append("\"");
                    sb.Append(",\"team\":\"").Append(Esc(_team)).Append("\"");
                    sb.Append(",\"drone\":\"").Append(Esc(_drone)).Append("\"");
                    sb.Append(",\"bomb\":\"").Append(Esc(_bomb)).Append("\"");
                    sb.Append(",\"flight\":").Append(_flight ? "true" : "false");
                    sb.Append(",\"level\":").Append(_level.ToString("0.###"));
                    sb.Append(",\"secs\":").Append(_secs);
                    sb.Append(",\"hud\":").Append(_hud ? "true" : "false");
                    sb.Append(",\"hdg\":\"").Append(Esc(_hdg)).Append("\"");
                    sb.Append(",\"spd\":\"").Append(Esc(_spd)).Append("\"");
                    sb.Append(",\"agl\":\"").Append(Esc(_agl)).Append("\"");
                    sb.Append(",\"roll\":").Append(_roll.ToString("0.#"));
                    sb.Append(",\"pit\":").Append(_pit.ToString("0.#"));
                    sb.Append(",\"uav\":").Append(_uav ? "true" : "false");
                    sb.Append(",\"lines\":[");
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("[\"").Append(Esc(_lines[i][0])).Append("\",\"").Append(Esc(_lines[i][1])).Append("\"]");
                    }
                    sb.Append("]}");
                }
                body = sb.ToString(); ctype = "application/json";
                try { c.Response.Headers.Add("Access-Control-Allow-Origin", "*"); } catch { }
            }
            else
            {
                string f = Path.Combine(_dir, "overlay.html");
                try { body = File.Exists(f) ? File.ReadAllText(f) : "<html><body style='background:#000;color:#8cff9c;font-family:Consolas;font-size:28px'>overlay.html missing</body></html>"; }
                catch { body = "<html><body style='background:#000'></body></html>"; }
                ctype = "text/html";
            }
            byte[] buf = System.Text.Encoding.UTF8.GetBytes(body);
            c.Response.ContentType = ctype;
            // tell the OBS browser source never to cache, so updated overlay.html is picked up
            try
            {
                c.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                c.Response.Headers["Pragma"] = "no-cache";
                c.Response.Headers["Expires"] = "0";
            }
            catch { }
            try { c.Response.OutputStream.Write(buf, 0, buf.Length); c.Response.OutputStream.Close(); } catch { }
        }
    }

    // Terminal-style cover: a nearly-black screen with a green monospace connection log that
    // streams in, with [OK]/[BAD] verdicts. Click-through and never focused.
    class ConsoleForm : Form
    {
        public readonly List<string[]> Lines = new List<string[]>();
        public bool ShowCursor = true;
        readonly Font _f = new Font("Consolas", 17F, FontStyle.Regular);
        readonly Font _fh = new Font("Consolas", 17F, FontStyle.Bold);
        readonly SolidBrush _txt = new SolidBrush(Color.FromArgb(140, 255, 160));
        readonly SolidBrush _ok = new SolidBrush(Color.FromArgb(90, 255, 120));
        readonly SolidBrush _bad = new SolidBrush(Color.FromArgb(255, 80, 80));
        readonly SolidBrush _cur = new SolidBrush(Color.FromArgb(160, 140, 255, 150));
        readonly Pen _barPen = new Pen(Color.FromArgb(150, 255, 160), 2);
        readonly SolidBrush _barFill = new SolidBrush(Color.FromArgb(210, 90, 255, 120));
        readonly System.Windows.Forms.Timer _blink;
        readonly System.Windows.Forms.Timer _anim;
        public float Progress = 0f;
        public float Target = 0f;          // Progress eases toward this so the bar fills smoothly
        public string ProgressLabel = "";

        public ConsoleForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            // semi-transparent on the monitor so the game still shows through faintly; the OBS
            // browser-source version is fully black instead
            Opacity = 0.78;
            _blink = new System.Windows.Forms.Timer();
            _blink.Interval = 480;
            _blink.Tick += delegate { ShowCursor = !ShowCursor; Invalidate(); };
            _blink.Start();

            // ease the bar toward its target so it slides up instead of snapping to a number
            _anim = new System.Windows.Forms.Timer();
            _anim.Interval = 50;
            _anim.Tick += delegate
            {
                float d = Target - Progress;
                Rectangle bar = new Rectangle(40, Height - 150, 760, 110);
                if (Math.Abs(d) < 0.002f) { if (Progress != Target) { Progress = Target; Invalidate(bar); } return; }
                Progress += d * 0.12f;
                Invalidate(bar);       // repaint only the bar, not the whole full-screen window
            };
            _anim.Start();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT - click-through
                return cp;
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // FillRectangle (not Clear) so the invalid region clip is respected - Clear would
            // force a full-window repaint every animation frame
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(5, 9, 5)))
                g.FillRectangle(bg, ClientRectangle);
            int x = 56, y = 64, lh = 30;
            foreach (string[] ln in Lines)
            {
                string text = ln[0];
                g.DrawString(text, _f, _txt, x, y);
                if (ln[1] == "OK" || ln[1] == "BAD")
                {
                    SizeF sz = g.MeasureString(text, _f);
                    g.DrawString("[" + ln[1] + "]", _fh, ln[1] == "OK" ? _ok : _bad, x + sz.Width + 14, y);
                }
                y += lh;
            }
            if (ShowCursor) g.FillRectangle(_cur, x, y + 3, 12, 20);

            // progress bar that fills as the deploy sequence advances
            int bw = 720, bh = 26, bx = 56, by = Height - 130;
            g.DrawRectangle(_barPen, bx, by, bw, bh);
            int fw = (int)((bw - 4) * Math.Max(0f, Math.Min(1f, Progress)));
            if (fw > 0) g.FillRectangle(_barFill, bx + 2, by + 2, fw, bh - 4);
            g.DrawString(((int)(Progress * 100)) + "%   " + ProgressLabel, _f, _txt, bx, by + bh + 12);
            base.OnPaint(e);
        }
    }

    // Flashing "LAND NOW" HUD alert, shown for 6 seconds after a reconnect.
    void ShowLandNow()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { ShowLandNow(); }); return; }

            OverlayForm f = new OverlayForm();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.Manual;
            f.TopMost = true;
            f.ShowInTaskbar = false;
            // no panel behind the text: the background colour is keyed out entirely, so only
            // the flashing words are drawn over the game
            f.BackColor = Color.Black;
            f.TransparencyKey = Color.Black;

            Rectangle pb = Screen.PrimaryScreen.Bounds;
            int w = 820, h = 210;
            f.SetBounds((pb.Width - w) / 2, (int)(pb.Height * 0.15), w, h);

            Label lbl = new Label();
            lbl.Text = "LAND NOW";
            lbl.Font = new Font("Segoe UI", 64F, FontStyle.Bold);
            lbl.ForeColor = Color.White;
            lbl.BackColor = Color.Black;         // same as the key, so it stays see-through
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Dock = DockStyle.Fill;
            f.Controls.Add(lbl);

            System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer();
            blink.Interval = 320;
            bool on = true;
            blink.Tick += delegate
            {
                on = !on;                       // flash white on/off (black keys out to nothing)
                lbl.ForeColor = on ? Color.White : Color.Black;
            };
            blink.Start();

            System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();
            life.Interval = 6000;
            // once the alert has had its 6s, put the black cover over the reconnect
            // once the alert has had its 6s: take the LAND NOW text down, stop the RF static too
            // (pressing LAND NOW means we are leaving this feed), then put the black cover up
            life.Tick += delegate
            {
                life.Stop(); blink.Stop();
                if (_land == f) _land = null;
                try { f.Close(); } catch { }
                StopRfWatch();
                ShowBlack();
            };
            life.Start();

            // NOT excluded from capture - LAND NOW shows up in recordings. OCR is not needed
            // while it is up, so it does not matter that it appears in our screen grabs too.
            f.Show();
            _land = f;
        }
        catch { }
    }

    void HideLandNow()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { HideLandNow(); }); return; }
            if (_land != null) { _land.Close(); _land = null; }
        }
        catch { }
    }

    // Full-screen terminal cover, shown while the reconnect process runs so the ugly connect/
    // loading is hidden and it looks like a drone-link CLI booting. Excluded from screen
    // capture, so the OCR still sees the real game underneath and can tell when the UI is back.
    void ShowBlack()
    {
        try
        {
            if (!_blackScreen || _halted) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { ShowBlack(); }); return; }
            if (_black != null) return;

            _black = new ConsoleForm();
            _black.FormBorderStyle = FormBorderStyle.None;
            _black.StartPosition = FormStartPosition.Manual;
            _black.TopMost = true;
            _black.ShowInTaskbar = false;
            _black.SetBounds(0, 0, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            _black.Show();
            ExcludeFromCapture(_black.Handle);

            // If the flow is already part-way through, this is a RECOVERY, not a cold start: keep
            // the progress bar where it was and let the boot narrate the recovery. A fresh start
            // (progress ~0 or already finished) clears the terminal and begins at 0%.
            bool resuming = _flowPct > 0.05f && _flowPct < 0.99f;
            if (resuming) OverlayHub.I.SetProgress(_flowPct, "re-sync");
            else { _flowPct = 0f; OverlayHub.I.Active(true); }

            _blackWatch = true;
            Thread t = new Thread(delegate ()
            {
                AddBlackLine("BF-DRONE LINK  v3.2.1   [rf uplink]", "");
                // startup prompt: type a login like a real console, then sit at a standby prompt
                AddBlackLine("> login operator " + (_serverId == "" ? "guest" : _serverId.Substring(0, 8)), "");
                Thread.Sleep(620);
                AddBlackLine("authenticating operator key", "OK");
                Thread.Sleep(340);
                AddBlackLine("session established - console ready", "OK");
                AddBlackLine("standby - awaiting command", "");
                OverlayHub.I.SetProgress(_flowPct, "standby");

                // Stay at the login/standby prompt until the operator has locked a team (the flow
                // reaches "team locked"), THEN type the connect command - so "connecting" only
                // shows up after the team step is pressed, which reads logically for viewers.
                int w0 = 0;
                while (_blackWatch && _flowPct < 0.30f && w0 < 60000)
                {
                    Thread.Sleep(150); w0 += 150;
                    if (!_running && w0 > 15000) break;   // no AUTO run in flight: don't hold the cover
                    // poll the (expensive) OCR link check sparingly - hammering it here stole the
                    // OCR from the AUTO flow and could make its team step fail
                    if ((w0 % 1500) == 0 && IsLinked()) break;
                }

                string ip = RandIp();
                AddBlackLine("> connect " + ip + ":47320", "");
                Thread.Sleep(650);
                if (resuming)
                {
                    AddBlackLine("!! FAULT 0x7F: telemetry link degraded", "BAD");
                    Thread.Sleep(240);
                    AddBlackLine("   recovery protocol engaged", "OK");
                    Thread.Sleep(240);
                }
                AddBlackLine("resolving ground station " + ip, "OK");
                AddBlackLine("calibrating inertial nav (imu)", "OK");
                AddBlackLine("spooling gyro stabiliser", "OK");
                // each line waits for the REAL flow to reach its milestone (BootWait), so the
                // terminal narrates what is happening instead of racing ahead on a fixed timer
                BootWait("negotiating encrypted uplink", "OK", 0.32f, "uplink", 12000);
                BootWait("selecting airframe  [" + _drone + "]", "OK", 0.40f, "airframe", 40000);
                BootWait("checking warhead rack", "OK", 0.62f, "payload", 40000);
                BootWait("syncing telemetry stream", "OK", 0.70f, "telemetry", 40000);
                BootWait("running pre-flight checks", "OK", 0.75f, "preflight", 40000);
                BootWait("arming flight controller", "OK", 0.84f, "arm", 40000);
                BootWait("signal check - uplink degraded", "BAD", 0.88f, "degraded", 40000);
                BootWait("re-establishing uplink", "OK", 0.90f, "connecting to drone", 60000);
                AddBlackLine("awaiting drone telemetry...", "");

                // stay up through the reconnect AND the whole deploy sequence, until the drone
                // is actually online (the top-right LINK indicator). AutoSteps also hides it at
                // the Deploy As Drone step; this loop is the fallback / timeout.
                long t0 = Environment.TickCount;
                bool combat = false;
                while (_blackWatch && (Environment.TickCount - t0) < 180000)
                {
                    string st = ScreenName(OcrWords());
                    if (!combat && (st == "map" || st == "team base"))
                    { combat = true; AddBlackLine("entering combat zone", "OK"); AddBlackProgress(0.98f, "combat zone"); }
                    if (IsLinked()) { AddBlackLine("drone online", "OK"); AddBlackProgress(1.0f, "drone online"); break; }
                    InvalidateOcr();
                    Thread.Sleep(500);
                }
                HideBlack();
            });
            t.IsBackground = true;
            t.Start();
            Log("black cover up (terminal)");
        }
        catch { }
    }

    // These always feed the OBS overlay (the viewer-facing CLI) AND the on-screen console when it
    // is up. They used to bail out when _black was null, which left the OBS terminal empty during
    // every part of the flow that runs before the black cover appears.
    void AddBlackLine(string text, string status)
    {
        try
        {
            OverlayHub.I.AddLine(text, status);
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { AddConsoleLine(text, status); }); return; }
            AddConsoleLine(text, status);
        }
        catch { }
    }

    void AddConsoleLine(string text, string status)
    {
        if (_black == null) return;
        _black.Lines.Add(new string[] { text, status ?? "" });
        if (_black.Lines.Count > 40) _black.Lines.RemoveAt(0);
        _black.Invalidate();
    }

    void AddBlackProgress(float pct, string label)
    {
        try
        {
            if (pct < _flowPct) return;   // progress never runs backwards across the whole flow
            _flowPct = pct;
            OverlayHub.I.SetProgress(pct, label);
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { AddConsoleProgress(pct, label); }); return; }
            AddConsoleProgress(pct, label);
        }
        catch { }
    }

    void AddConsoleProgress(float pct, string label)
    {
        if (_black == null) return;
        _black.Target = pct;                 // the anim timer eases Progress up to this
        _black.ProgressLabel = label ?? "";
        _black.Invalidate();
    }

    // A fake milsim fault, shown on the OBS terminal when the flow has to recover/restart. The
    // progress bar is deliberately NOT reset - the recovery resumes where the flow left off.
    void AddBlackError(string code, string reason)
    {
        AddBlackLine("!! FAULT " + code + ": " + reason, "BAD");
        AddBlackLine("   recovery protocol engaged", "OK");
        AddBlackLine("   resuming uplink at " + ((int)(_flowPct * 100)) + "%", "");
    }

    // A boot line that waits (bounded) for the real flow to reach its milestone before printing,
    // so the terminal follows the actual progress instead of racing on a fixed timer. When no AUTO
    // run is in flight it does not wait at all.
    void BootWait(string text, string status, float waitPct, string label, int maxMs)
    {
        int w = 0;
        while (_blackWatch && _running && _flowPct < waitPct && w < maxMs) { Thread.Sleep(100); w += 100; }
        AddBlackLine(text, status);
        if (label != null) OverlayHub.I.SetProgress(_flowPct, label);
    }

    readonly Random _rand = new Random();
    string RandIp()
    {
        return "192.168." + _rand.Next(0, 256) + "." + _rand.Next(1, 255);
    }

    void HideBlack()
    {
        try
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { HideBlack(); }); return; }
            _blackWatch = false;
            OverlayHub.I.Active(false);
            if (_black != null) { _black.Close(); _black = null; Log("black cover down"); }
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RobloxAuto());
    }
}












