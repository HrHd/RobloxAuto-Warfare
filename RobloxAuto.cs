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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

class RobloxAuto : Form, System.Windows.Forms.IMessageFilter
{
    // ================= config =================
    string _appDir, _cfgPath;
    string _placeId = "", _serverId = "";

    string _team = "Blue";       // "Blue" or "Red" - the team NUMBER changes every match
    string _drone = "MAVIC";
    string _bomb = "(none)";     // warhead chosen on the Team Base panel
    bool _asDrone = true;

    // ---- favourite setups: one MAVIC, one FPV. Toggle with F7 or the controller button. ----
    string _favMTeam = "Red", _favMDrone = "MAVIC", _favMBomb = "(none)"; bool _favMAsDrone = true;
    string _favFTeam = "Red", _favFDrone = "FPV", _favFBomb = "Light Rocket"; bool _favFAsDrone = true;
    string _activeFav = "FPV";       // which of the two is currently live

    uint _hkRejoinKey = 0x77;   // F8
    uint _hkAutoKey = 0x74;     // F5
    uint _hkNightKey = 0x76;    // F7 - toggles night vision
    bool _capturingNight = false;

    string _padRejoin = "BACK";
    string _padAuto = "LB";
    string _padStop = "off";   // NOT Y: many joystick triggers map to Y, so Y caused a STOP on every bomb drop
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
    volatile bool _autoDeployed = false;   // set once the flow actually reaches Deploy As Drone
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
    bool _padSwapWasDown = false;
    bool _padSwapOn = false; string _padSwap = "B";
    ushort _lastButtons = 0;

    // ================= ui =================
    TextBox txtServer, txtLog;
    Button btnRejoin, btnReconnect, btnRefresh, btnAuto, btnSetKey, btnStop, btnOpenLog, btnHighPrio, btnSetNight;
    Label lblKeyVal, lblNightVal;
    bool _capturingKey = false;
    ComboBox cmbTeam, cmbDrone, cmbBomb, cmbPadRejoin, cmbPadAuto, cmbPadStop, cmbPadLand, cmbPadReconnect;
    ComboBox cmbPadSwap; CheckBox chkPadSwap; Button btnSwap; Label lblActiveFav;
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
    float _hudSmRoll = 0f, _hudSmPitch = 0f;      // smoothed across measurements (confidence-weighted)
    float _hudW = 0f, _hudSRoll = 0f, _hudSPitch = 0f;   // weighted-average accumulators (weight = confidence)
    bool _hudSmSeeded = false;
    float _padLx = 0f, _padLy = 0f, _padRx = 0f, _padRy = 0f;   // sticks, -1..1, dead-zoned
    float _hudLockRoll = 0f, _hudLockPitch = 0f;  // base horizon (image-derived, corrected over time)
    float _hudCtrlRoll = 0f, _hudCtrlPitch = 0f;  // stick rotation integrated on top of the base
    float _hudFRoll = 0f, _hudFPitch = 0f;        // fused horizon (complementary filter, 50fps)
    volatile bool _hudShown = false;              // RF loop tells HudTick the feed is up
    int _hudSecs = 0;
    bool _hudLocked = false;
    bool _hudNeedLock = false;                    // true from Deploy As Drone until we lock
    // SPAWN SETTLE: the drone can spawn in an odd pose (sometimes under the map), so the first
    // frames are garbage. Hold the ladder still for a few seconds, collect what the detector sees,
    // then lock from the MEDIAN of those samples instead of trusting one frame.
    volatile bool _hudSettling = false;
    // COLD START. For the first seconds of a flight the detector is at its most likely to latch
    // onto terrain and swing the ladder, so it is ordered to stop being clever and just report the
    // MEDIAN of the image until this expires. See MedianHorizonLine.
    int _hudColdMs = 4000;      // the quick median is a stopgap to get a line on screen fast
    volatile int _hudColdUntil = 0;
    bool _hudColdLogged = false;
    int _hudSettleMs = 2500;
    long _hudSettleUntil = 0;
    float[] _seedR = new float[80];
    float[] _seedP = new float[80];
    int _seedN = 0;
    int _hudSettleExt = 0;                         // how many times the spawn baseline window was extended
    float _hudDetSky = 1f;                        // 0..1 - fraction of the frame that is SKY (above the line)
    float _hudSkySm = 1f;                         // smoothed sky fraction (no pops in the gyro/image blend)
    bool _hudDetValid = false;                    // DetectHorizon found a confident line
    float _hudDetConf = 0f;                       // 0..1 - how sure the detector is (drives the fusion weight)
    float _hudTrkM = 0f, _hudTrkB = 0f;           // last accepted horizon line (slope/intercept)
    long _hudTrkAt = 0;                           // when we accepted it - used to guide the next search
    long _hudDetAt = 0;                           // when it did (so a stale fix is not trusted)
    long _hudLogAt = 0;
    long _hudPrevTick = 0;
    bool _hudStyleUav = false;                     // MAVIC gets the UAV-style layout
    FpvHudForm _hudForm = null;
    CheckBox chkHud, chkNight;
    // ---- PHYSICS LOG --------------------------------------------------------------------------
    // One CSV line per accepted horizon measurement, carrying the STICK and the MEASURED horizon
    // together. That is what lets the stick->horizon-rate model be FITTED against reality instead of
    // guessed: fly it, then regress d(roll)/dt on the roll stick and d(pitch)/dt on the pitch stick.
    // Columns: t_ms,sx,sy,lx,ly,detRoll,detPitch,conf,fuseRoll,fusePitch,modelRollRate,modelPitchRate
    bool _physLog = false;
    long _physT0 = 0;
    long _physAt = 0;
    float _lastSxS = 0f, _lastSyS = 0f;
    CheckBox chkPhys;
    // ---- HUD tuning dials (live-adjustable, hotkeys below) ----
    float _hudPitchRate = 660f;                    // px/s the horizon moves per unit of stick pitch
    float _hudRollRate = 220f;                     // deg/s per unit of stick roll
    float _hudPitchVel = 0f, _hudRollVel = 0f;     // current horizon velocity (px/s, deg/s) - the ACCEL model
    float _hudAccelTau = 0.12f;                    // s - how fast the velocity chases the stick (accel/brake)
    float _hudV1 = 15.0f, _hudV2 = 15.0f;          // simulated FPV pack voltages (4S), driven by throttle
    float _hudVBat = 0.5f, _hudVVel = 0f;          // internal spring state of the battery sim
    float _hudBias = 6f;                           // px - constant downward offset of the detected line
    // Pitch-ladder geometry (the "math" knobs):
    //   rung n sits at  y = cy + pit + dpp * d        (d = rung angle in degrees)
    //   its sideways slide is  xoff = -dpp * d * tan(bank) * shear   (exact shear of a rotated horizon)
    float _hudDpp = 8f;                            // px per degree of pitch (rung spacing)
    float _hudShear = 0.9f;                         // 0 = lines never slide, 1 = exact geometric shear
    float _hudLen = 1f;
    float _hudMaxTilt = 30f;                       // DEG - hard cap on how far the rungs may tilt (dial "max tilt")
    float _hudRungTilt = 0f;                       // 0 = rungs stay FLAT/horizontal, 1 = rungs parallel to the horizon                             // rung length scale
    float _hudRollOff = 0f;                         // manual roll offset, degrees (dial "roll off")
    float _hudPitOff = 0f;                          // manual pitch offset, px (dial "pitch off")
    float _hudSpreadAccel = 0f;                     // expo on how the ladder fan builds up (dial "spread accel")
    float _hudMinSpread = 5f;                        // reject frames whose colour spread is below this (dial "min colour")
    float _hudClutter = 0f;                          // 0..1 detected clutter (trees/structures) around the horizon
    float _hudTreeDrop = 3f;                         // DEG to push the horizon DOWN when clutter is high (dial "tree drop")
    float _hudSpreadAmt = 2.2f;                      // how fast the ladder FANS OUT with tilt (dial "spread")
    float _hudTexW = 1f;                            // how hard the TEXTURE cue (smooth sky / busy ground)
                                                    // backs the colour cue (dial "texture"); 0 = off
    float _hudAccPitch = 0.65f;                     // expo/acceleration on the right-stick Y (dial "pitch accel")
    float _hudAccRoll = 0f;                         // expo/acceleration on the right-stick X (dial "tilt accel")
    float _hudFineP = 1f;                           // LOW-END boost (dial "fine gain"): small stick moves do more,
                                                    // while a FULL push is unchanged
    float _hudLeftPx = 0f;                         // px - max horizon offset from the LEFT stick (dial "thr pitch"); 0 = off
    float _hudPitStick = 280f;                      // px - DIRECT horizon offset from the RIGHT stick Y (dial "stick pitch")
                                                    //      push forward = line UP, pull back = line DOWN (signed: set - to invert)
    float _hudPitStickSm = 0f;                      // smoothed stick-pitch offset (glides, never jumps)
    float _hudRollStick = 8f;                       // DEG - direct bank from the right stick X (dial "stick tilt").
                                                    // The DETECTED roll already follows the real bank (the camera rolls
                                                    // with the drone), so this ADDS on top of it: at 30 the ladder swung
                                                    // far further than the world tilted, which is what made aiming hard in
                                                    // a hard turn. Kept small as a fine-tilt assist; set 0 to remove it.
    float _hudRollStickSm = 0f;                     // smoothed stick-tilt offset
    // ---- HORIZON ESTIMATOR ------------------------------------------------------------------
    // The old fusion smoothed the horizon in PIXELS with a fixed decay average. Pixels are not
    // linear in angle (px = f*tan(theta)), so that average is biased once the line leaves centre,
    // and a decay average cannot coast. This runs a small per-axis ALPHA-BETA filter on the
    // ANGLE, with the stick as a control input, plus (a) a Mahalanobis outlier gate, (b) a learned
    // constant offset, and (c) a learned stick->rate gain (system ID against the image). With no
    // usable frame it COASTS on the last rate - exactly "hold where the horizon would be".
    float _hudFovY = 70f;                 // vertical FOV, degrees (dial "fov") - the camera model
    float _hudManeuver = 40f;             // deg^2/s the attitude uncertainty grows (dial "maneuver")
    int _hudFrameH = 1080;                // frame height the detector saw (camera model)
    float _kfRollX = 0f, _kfRollV = 0f, _kfRollOb = 0f, _kfRollGain = 1f;
    float _kfPitX = 0f, _kfPitV = 0f, _kfPitOb = 0f, _kfPitGain = 1f;
    float _kfRollZ = 0f, _kfPitZ = 0f; long _kfRollZAt = 0, _kfPitZAt = 0;
    float _kfRollP = 25f, _kfPitP = 25f;  // state variance (deg^2) - drives the Kalman gains
    bool _kfSeeded = false;
    long _kfMeasAt = 0;                   // which measurement the filter has already consumed
    // ---- REFERENCE FRAMES ("base photos") -----------------------------------------------------
    // Labelled example frames in "<exe>\hudref\" are matched against the live frame by a coarse
    // 6x6 colour signature. A name containing "bad" (or "no_"/"false") marks a BAD example - e.g.
    // "this is ground, not sky" - and a near match to one kills the lock. Everything else is GOOD
    // (a real sky/ground line) and a near match boosts the lock. This is how you TEACH it.
    List<float[]> _refSigs = new List<float[]>();
    List<bool> _refGoodL = new List<bool>();
    // Each reference may carry its OWN horizon line (a .hzn sidecar written by "capture ref"). When
    // the live frame matches such a reference VERY closely we have seen exactly this before and
    // know the answer, so we adopt its line - that is the "trained on your photos" part.
    List<float[]> _refLine = new List<float[]>();
    float[] _refBestLine = null;          // horizon line of the nearest reference (null = none stored)
    bool _refLoaded = false;
    float _refDist = 0.16f;               // dial "ref match": L2 threshold (0..1) for a match
    float _refD = 9f;                     // last frame's nearest-reference distance
    bool _refGood = true;                 // last frame's nearest-reference label
    // Raw robust measurement from the detector (BEFORE smoothing) - what the estimator consumes.
    float _hudMRoll = 0f, _hudMPitch = 0f;        // deg, px offset from centre
    float _hudAxisW = 1f;                          // dial "axis weight": influence of the sky/ground colour axis cue
    float _hudMsTau = 0.08f;                       // dial "axis smooth": low-pass time constant (s) on the detector measurement
    string _hudScene = "?";                      // matched base scene (snow/fog/grey/normal)
    float _hudSceneD = 9f;                        // distance to that base scene
    float _hudCoastMs = 0f;                       // how long the vision has been missing (ms)
    float _hudSceneTol = 0.10f;                   // max RMS distance to claim a scene match
    int _hudSceneTick = 0;
    float _hudSkyMin = 0.10f;                      // dial "sky min": discard a frame with less verified sky than this
    int _hudNoSkyFrames = 0;                       // frames discarded for having no usable sky
    float _kfSmRoll = 0f, _kfSmPit = 0f;           // smoothed measurement fed to the estimator
    bool _kfSmSeeded = false;
    float _hudMRollVar = 4f, _hudMPitchVar = 9f;  // measurement variance (deg^2, px^2)
    float _hudMConf = 0f;
    // Solid-lock memory / HUG: after enough confident locks the horizon is treated as ESTABLISHED
    // and the line hugs the controller model, resisting noisy frames until a sustained change.
    int _hudSolid = 0;
    float _hugSm = 0f;
    int _hugDisR = 0, _hugDisP = 0, _hugSignR = 0, _hugSignP = 0;
    bool _hugLogged = false;
    NumericUpDown numPitch, numRoll, numBias, numAccel, numFov, numManeuver, numRefDist, numAxisW, numMsTau, numSkyMin, numSkyTex;
    Button btnReloadRefs, btnCapRef;
    NumericUpDown numDpp, numShear, numLen, numRollOff, numPitOff, numThr, numPitStick, numRollStick, numTexW, numAccPitch, numAccRoll, numFineP, numRungTilt, numMaxTilt, numSpread, numSpreadAccel, numTreeDrop, numMinSpread;
    bool _hudCapturable = false;                    // settings "hudCap": allow capturing the monitor HUD (diagnostics)
    bool _hudOn = true;                            // draw the FPV/UAV HUD while flying
    bool _nightVision = false;                     // invert the whole display (Magnifier color effect)
    DateTime _flightStart = DateTime.MinValue;   // when Deploy As Drone happened
    int _flightOcrBase = -1;                      // last FLIGHT mm:ss read from the OSD (seconds)
    long _flightOcrAt = 0;                        // when that read was taken
    static string _userName = "";            // our in-game name; printed on every menu (never on the OSD)
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
    const int HK_REJOIN = 0x5A01, HK_AUTO = 0x5A02, HK_NIGHT = 0x5A03, HK_STOP = 0x5A04, HK_INSTANT = 0x5A05, HK_SWAP = 0x5A06, HK_REF = 0x5A07;
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

    // Is the game the window the user is actually looking at? If they tabbed away, the screen
    // grab is somebody else's app - so detection must not read it. Returns true when we cannot
    // tell, so it never wrongly blocks the HUD.
    static bool RobloxFocused()
    {
        try
        {
            IntPtr rw = RobloxWindow();
            if (rw == IntPtr.Zero) return true;
            IntPtr fg = GetForegroundWindow();
            if (fg == rw) return true;
            uint pf, pr;
            GetWindowThreadProcessId(fg, out pf);
            GetWindowThreadProcessId(rw, out pr);
            return pf != 0 && pf == pr;    // a fullscreen child of the same process also counts
        }
        catch { return true; }
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
        { "English",  new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) },
        { "EspaÃ±ol",  new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","RECONECTAR"},{"Refresh","Actualizar"},{"AUTO RUN","AUTO"},{"Open log","Abrir registro"},
            {"Keybind:","Tecla:"},{"Set key...","Fijar tecla..."},{"controller:","mando:"},{"land now:","aterrizar:"},
            {"reconnect:","reconectar:"},{"stuck","atascado"},{"sec -> reconnect","s -> reconectar"},
            {"Team:","Equipo:"},{"Drone:","Dron:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO tras reconectar"},
            {"RF watch (HOME)","RF (CASA)"},{"Black screen while reconnecting","Pantalla negra al reconectar"},
            {"s delay","s retraso"},{"Show LAND NOW, hold","Mostrar LAND NOW, esperar"},{"s before reconnect","s antes de reconectar"} } },
        { "Deutsch",  new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","NEU VERBINDEN"},{"Refresh","Aktualisieren"},{"AUTO RUN","AUTO"},{"Open log","Log Ã¶ffnen"},
            {"Keybind:","Taste:"},{"Set key...","Taste setzen..."},{"controller:","Controller:"},{"land now:","landen:"},
            {"reconnect:","verbinden:"},{"stuck","hÃ¤ngt"},{"sec -> reconnect","s -> neu verbinden"},
            {"Team:","Team:"},{"Drone:","Drohne:"},{"Bomb:","Bombe:"},{"AUTO after rejoin","AUTO nach Neuverb."},
            {"RF watch (HOME)","RF (BASIS)"},{"Black screen while reconnecting","Schwarzer Bildschirm"},
            {"s delay","s VerzÃ¶gerung"},{"Show LAND NOW, hold","LAND NOW zeigen, warten"},{"s before reconnect","s vor Neuverb."} } },
        { "FranÃ§ais", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","RECONNECTER"},{"Refresh","Actualiser"},{"AUTO RUN","AUTO"},{"Open log","Ouvrir log"},
            {"Keybind:","Touche:"},{"Set key...","DÃ©finir touche..."},{"controller:","manette:"},{"land now:","atterrir:"},
            {"reconnect:","reconnecter:"},{"stuck","bloquÃ©"},{"sec -> reconnect","s -> reconnecter"},
            {"Team:","Ã‰quipe:"},{"Drone:","Drone:"},{"Bomb:","Bombe:"},{"AUTO after rejoin","AUTO aprÃ¨s reconnexion"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Ã‰cran noir en reconnexion"},
            {"s delay","s dÃ©lai"},{"Show LAND NOW, hold","Afficher LAND NOW, attendre"},{"s before reconnect","s avant reconnexion"} } },
        { "PortuguÃªs", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","RECONECTAR"},{"Refresh","Atualizar"},{"AUTO RUN","AUTO"},{"Open log","Abrir log"},
            {"Keybind:","Tecla:"},{"Set key...","Definir tecla..."},{"controller:","controle:"},{"land now:","aterrissar:"},
            {"reconnect:","reconectar:"},{"stuck","travado"},{"sec -> reconnect","s -> reconectar"},
            {"Team:","Equipe:"},{"Drone:","Drone:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO apÃ³s reconectar"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Tela preta ao reconectar"},
            {"s delay","s atraso"},{"Show LAND NOW, hold","Mostrar LAND NOW, esperar"},{"s before reconnect","s antes de reconectar"} } },
        { "Ð ÑƒÑÑÐºÐ¸Ð¹",  new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","ÐŸÐ•Ð Ð•ÐŸÐžÐ”ÐšÐ›Ð®Ð§Ð˜Ð¢Ð¬Ð¡Ð¯"},{"Refresh","ÐžÐ±Ð½Ð¾Ð²Ð¸Ñ‚ÑŒ"},{"AUTO RUN","ÐÐ’Ð¢Ðž"},{"Open log","ÐžÑ‚ÐºÑ€Ñ‹Ñ‚ÑŒ Ð»Ð¾Ð³"},
            {"Keybind:","ÐšÐ»Ð°Ð²Ð¸ÑˆÐ°:"},{"Set key...","Ð—Ð°Ð´Ð°Ñ‚ÑŒ ÐºÐ»Ð°Ð²Ð¸ÑˆÑƒ..."},{"controller:","Ð³ÐµÐ¹Ð¼Ð¿Ð°Ð´:"},{"land now:","Ð¿Ð¾ÑÐ°Ð´ÐºÐ°:"},
            {"reconnect:","Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡Ð¸Ñ‚ÑŒ:"},{"stuck","Ð·Ð°Ð²Ð¸Ñ"},{"sec -> reconnect","Ñ -> Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ðµ"},
            {"Team:","ÐšÐ¾Ð¼Ð°Ð½Ð´Ð°:"},{"Drone:","Ð”Ñ€Ð¾Ð½:"},{"Bomb:","Ð‘Ð¾Ð¼Ð±Ð°:"},{"AUTO after rejoin","ÐÐ’Ð¢Ðž Ð¿Ð¾ÑÐ»Ðµ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ñ"},
            {"RF watch (HOME)","RF (Ð‘ÐÐ—Ð)"},{"Black screen while reconnecting","Ð§Ñ‘Ñ€Ð½Ñ‹Ð¹ ÑÐºÑ€Ð°Ð½ Ð¿Ñ€Ð¸ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ð¸"},
            {"s delay","Ñ Ð·Ð°Ð´ÐµÑ€Ð¶ÐºÐ°"},{"Show LAND NOW, hold","ÐŸÐ¾ÐºÐ°Ð·Ð°Ñ‚ÑŒ LAND NOW, Ð¿Ð°ÑƒÐ·Ð°"},{"s before reconnect","Ñ Ð´Ð¾ Ð¿ÐµÑ€ÐµÐ¿Ð¾Ð´ÐºÐ»ÑŽÑ‡ÐµÐ½Ð¸Ñ"} } },
        { "Italiano", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"REJOIN NOW","RICONNETTI"},{"Refresh","Aggiorna"},{"AUTO RUN","AUTO"},{"Open log","Apri log"},
            {"Keybind:","Tasto:"},{"Set key...","Imposta tasto..."},{"controller:","controller:"},{"land now:","atterra:"},
            {"reconnect:","riconnetti:"},{"stuck","bloccato"},{"sec -> reconnect","s -> riconnetti"},
            {"Team:","Squadra:"},{"Drone:","Drone:"},{"Bomb:","Bomba:"},{"AUTO after rejoin","AUTO dopo riconnessione"},
            {"RF watch (HOME)","RF (BASE)"},{"Black screen while reconnecting","Schermo nero in riconnessione"},
            {"s delay","s ritardo"},{"Show LAND NOW, hold","Mostra LAND NOW, attendi"},{"s before reconnect","s prima di riconnettere"} } }
    };

    // ---- EXTERNAL TRANSLATIONS: lang.json (UTF-8) next to the exe ----
    // Easy translation: edit "lang.json", press the reload button - no recompile, no encoding pain.
    // Shape: { "Espanol": { "HUD TUNING": "AJUSTE DEL HUD", "pitch speed": "vel. cabeceo", ... } }
    // Any key the file does not cover falls back to English, so a language can be filled gradually.
    static string LangFile() { return Path.Combine(Application.StartupPath, "lang.json"); }

    static string EscJson(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c == '\t') b.Append("\\t");
            else b.Append(c);
        }
        return b.ToString();
    }

    static void LoadLangFile()
    {
        try
        {
            string f = LangFile();
            if (!File.Exists(f)) { WriteLangTemplate(); return; }
            Dictionary<string, object> root = JsonObj.Parse(File.ReadAllText(f, System.Text.Encoding.UTF8));
            foreach (KeyValuePair<string, object> kv in root)
            {
                Dictionary<string, object> inner = kv.Value as Dictionary<string, object>;
                if (inner == null) continue;
                if (!LANG.ContainsKey(kv.Key)) LANG[kv.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> e in inner)
                {
                    string v = e.Value as string;
                    if (v != null && v.Length > 0) LANG[kv.Key][e.Key] = v;
                }
            }
        }
        catch { }
    }

    // First run: write a template with EVERY UI string as the key and the built-in translation (if any)
    // as the value, for every language, so translating is just filling in blanks.
    static void WriteLangTemplate()
    {
        try
        {
            SortedSet<string> eng = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);   // dedupe case-insensitively
            foreach (KeyValuePair<Control, string> kv in _enText) if (!string.IsNullOrEmpty(kv.Value)) eng.Add(kv.Value);
            // strings that are NOT controls: the CLI/terminal lines, the OBS overlay text, the LAND NOW banner
            string[] extra = new string[] {
                "LAND NOW", "BF-DRONE LINK", "// SECURE RF UPLINK", "TEAM", "AIRFRAME", "PAYLOAD",
                "UAV LINK ACTIVE", "FPV LINK ACTIVE", "UPLINK SYNC", "STANDBY", "bf-drone link: standby",
                "authenticating operator key", "session established - console ready", "standby - awaiting command",
                "!! FAULT 0x7F: telemetry link degraded", "   recovery protocol engaged",
                "calibrating inertial nav (imu)", "spooling gyro stabiliser", "awaiting drone telemetry...",
                "entering combat zone", "drone online", "connecting to drone", "  uplink established",
                "!! LINK PAUSED - operator halt", "   awaiting re-establishment...", "> quick reconnect",
                "returning to command line - menu detected", "re-sync", "standby",
                "paused - awaiting re-establishment", "connect", "paused", "RECONNECTING", "SYNC"
            };
            foreach (string e in extra) eng.Add(e);
            List<string> langs = new List<string>(LANG.Keys);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            for (int li = 0; li < langs.Count; li++)
            {
                string lang = langs[li];
                Dictionary<string, string> d = LANG[lang];
                sb.Append("  \"").Append(EscJson(lang)).Append("\": {\n");
                List<string> allKeys = new List<string>(eng);
                foreach (string k in d.Keys) if (!eng.Contains(k)) allKeys.Add(k);
                allKeys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < allKeys.Count; i++)
                {
                    string k = allKeys[i], v;
                    if (!d.TryGetValue(k, out v)) v = "";
                    sb.Append("    \"").Append(EscJson(k)).Append("\": \"").Append(EscJson(v)).Append("\"");
                    sb.Append(i + 1 < allKeys.Count ? ",\n" : "\n");
                }
                sb.Append("  }").Append(li + 1 < langs.Count ? ",\n" : "\n");
            }
            sb.Append("}\n");
            File.WriteAllText(LangFile(), sb.ToString(), new System.Text.UTF8Encoding(true));
        }
        catch { }
    }

    void ReloadLangs()
    {
        try
        {
            LoadLangFile();
            if (cmbLang != null)
            {
                string sel = cmbLang.SelectedItem == null ? _lang : cmbLang.SelectedItem.ToString();
                cmbLang.Items.Clear();
                foreach (string k in LANG.Keys) cmbLang.Items.Add(k);
                cmbLang.SelectedItem = LANG.ContainsKey(sel) ? sel : "English";
            }
            ApplyLanguage();
        }
        catch { }
    }

    // Tiny JSON reader (object of objects of strings) - enough for lang.json, no external deps.
    static class JsonObj
    {
        public static Dictionary<string, object> Parse(string s)
        {
            int i = 0;
            object o = Val(s, ref i);
            Dictionary<string, object> d = o as Dictionary<string, object>;
            return d ?? new Dictionary<string, object>();
        }
        static object Val(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) return null;
            if (s[i] == '{') return Obj(s, ref i);
            if (s[i] == '"') return Str(s, ref i);
            return null;
        }
        static Dictionary<string, object> Obj(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++;                                             // {
            while (i < s.Length)
            {
                Skip(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; }
                string k = Str(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                d[k] = Val(s, ref i);
            }
            return d;
        }
        static string Str(string s, ref int i)
        {
            StringBuilder b = new StringBuilder();
            i++;                                             // opening quote
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    if (e == 'n') b.Append('\n');
                    else if (e == 't') b.Append('\t');
                    else if (e == 'r') b.Append('\r');
                    else if (e == 'u' && i + 4 <= s.Length) { b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; }
                    else b.Append(e);
                }
                else b.Append(c);
            }
            if (i < s.Length && s[i] == '"') i++;
            return b.ToString();
        }
        static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
    }

    // language picker, pinned to the top-right of the panel
    // translate an English UI/CLI string for the current language (missing entry -> English)
    string T(string en)
    {
        if (string.IsNullOrEmpty(en) || _lang == "English") return en ?? "";
        Dictionary<string, string> d;
        if (!LANG.TryGetValue(_lang, out d) || d == null) return en;
        string tr;
        return d.TryGetValue(en, out tr) && !string.IsNullOrEmpty(tr) ? tr : en;
    }

    void AddLangCombo()
    {
        cmbLang = new NoWheelCombo();
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

        // reload lang.json without restarting (edit the file, click this)
        Button btnLang = new Button();
        btnLang.Text = "reload";
        btnLang.Font = new Font("Segoe UI", 7F, FontStyle.Regular);
        btnLang.FlatStyle = FlatStyle.Flat;
        btnLang.ForeColor = Color.Gainsboro;
        btnLang.BackColor = Color.FromArgb(40, 44, 52);
        btnLang.SetBounds(ClientSize.Width - 50, 8, 44, 24);
        btnLang.Click += delegate { ReloadLangs(); };
        Controls.Add(btnLang);
        btnLang.BringToFront();
    }

    void CaptureEnglish(Control c)
    {
        // only TEXT-BEARING controls - a NumericUpDown/TextBox/ComboBox "text" is a VALUE, not a caption
        if (!(c is NumericUpDown) && !(c is TextBox) && !(c is ComboBox) && !string.IsNullOrEmpty(c.Text))
            _enText[c] = c.Text;
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
        // push the translated strings the OBS overlay draws itself
        PushOverlayUi();
    }

    void PushOverlayUi()
    {
        try
        {
            OverlayHub.I.SetUi("title", T("BF-DRONE LINK"));
            OverlayHub.I.SetUi("sub",   T("// SECURE RF UPLINK"));
            OverlayHub.I.SetUi("team",  T("TEAM"));
            OverlayHub.I.SetUi("air",   T("AIRFRAME"));
            OverlayHub.I.SetUi("pay",   T("PAYLOAD"));
            OverlayHub.I.SetUi("uav",   T("UAV LINK ACTIVE"));
            OverlayHub.I.SetUi("fpv",   T("FPV LINK ACTIVE"));
            OverlayHub.I.SetUi("sync",  T("UPLINK SYNC"));
            OverlayHub.I.SetUi("stby",  T("STANDBY"));
            OverlayHub.I.SetUi("cli",   T("bf-drone link: standby"));
        }
        catch { }
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
        CaptureEnglish(this);      // record the English caption of every control
        LoadLangFile();            // then overlay lang.json (or write a template if it is missing)
        AddLangCombo();
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

        numPitch = MkTune(x, y, "pitch speed", (decimal)_hudPitchRate, 80m, 4000m, 40m, 0);
        numPitch = MkTune(x, y, "pitch speed", (decimal)_hudPitchRate, 80m, 4000m, 40m, 0);
        numRoll = MkTune(x + 168, y, "roll speed", (decimal)_hudRollRate, 20m, 900m, 10m, 0);
        numPitch.ValueChanged += delegate { _hudPitchRate = (float)numPitch.Value; SaveCfg(); };
        numRoll.ValueChanged += delegate { _hudRollRate = (float)numRoll.Value; SaveCfg(); };
        y += 28;

        numBias = MkTune(x, y, "height adj", (decimal)_hudBias, -120m, 120m, 2m, 0);
        numAccel = MkTune(x + 168, y, "smoothing", (decimal)_hudAccelTau, 0.02m, 1.0m, 0.02m, 2);
        numBias.ValueChanged += delegate { _hudBias = (float)numBias.Value; SaveCfg(); };
        numAccel.ValueChanged += delegate { _hudAccelTau = (float)numAccel.Value; SaveCfg(); };
        y += 28;

        numDpp = MkTune(x, y, "line gap", (decimal)_hudDpp, 1m, 40m, 1m, 0);
        numShear = MkTune(x + 168, y, "staircase", (decimal)_hudShear, -2m, 2m, 0.05m, 2);
        numDpp.ValueChanged += delegate { _hudDpp = (float)numDpp.Value; SaveCfg(); };
        numShear.ValueChanged += delegate { _hudShear = (float)numShear.Value; SaveCfg(); };
        y += 28;

        numLen = MkTune(x, y, "line length", (decimal)_hudLen, 0.3m, 2.5m, 0.1m, 2);
        numRungTilt = MkTune(x + 168, y, "rung tilt", (decimal)_hudRungTilt, 0m, 1m, 0.1m, 1);
        numLen.ValueChanged += delegate { _hudLen = (float)numLen.Value; SaveCfg(); };
        numRungTilt.ValueChanged += delegate { _hudRungTilt = (float)numRungTilt.Value; SaveCfg(); };
        y += 28;

        numMaxTilt = MkTune(x, y, "max tilt", (decimal)_hudMaxTilt, 0m, 90m, 5m, 0);
        numSpread = MkTune(x + 168, y, "spread", (decimal)_hudSpreadAmt, 0m, 5m, 0.2m, 1);
        numMaxTilt.ValueChanged += delegate { _hudMaxTilt = (float)numMaxTilt.Value; SaveCfg(); };
        numSpread.ValueChanged += delegate { _hudSpreadAmt = (float)numSpread.Value; SaveCfg(); };
        y += 28;

        numSpreadAccel = MkTune(x, y, "spread accel", (decimal)_hudSpreadAccel, 0m, 1m, 0.05m, 2);
        numTreeDrop = MkTune(x + 168, y, "tree drop", (decimal)_hudTreeDrop, -20m, 20m, 1m, 0);
        numSpreadAccel.ValueChanged += delegate { _hudSpreadAccel = (float)numSpreadAccel.Value; SaveCfg(); };
        numTreeDrop.ValueChanged += delegate { _hudTreeDrop = (float)numTreeDrop.Value; SaveCfg(); };
        y += 28;

        numMinSpread = MkTune(x, y, "min colour", (decimal)_hudMinSpread, 0m, 40m, 1m, 0);
        numTexW = MkTune(x + 168, y, "texture", (decimal)_hudTexW, 0m, 3m, 0.1m, 1);
        numMinSpread.ValueChanged += delegate { _hudMinSpread = (float)numMinSpread.Value; SaveCfg(); };
        numTexW.ValueChanged += delegate { _hudTexW = (float)numTexW.Value; SaveCfg(); };
        y += 28;

        numThr = MkTune(x, y, "throttle", (decimal)_hudLeftPx, 0m, 120m, 2m, 0);
        numRollOff = MkTune(x + 168, y, "roll shift", (decimal)_hudRollOff, -180m, 180m, 1m, 0);
        numThr.ValueChanged += delegate { _hudLeftPx = (float)numThr.Value; SaveCfg(); };
        numRollOff.ValueChanged += delegate { _hudRollOff = (float)numRollOff.Value; SaveCfg(); };
        y += 28;

        numPitOff = MkTune(x, y, "height shift", (decimal)_hudPitOff, -400m, 400m, 5m, 0);
        numAccPitch = MkTune(x + 168, y, "pitch accel", (decimal)_hudAccPitch, 0m, 1m, 0.05m, 2);
        numPitOff.ValueChanged += delegate { _hudPitOff = (float)numPitOff.Value; SaveCfg(); };
        numAccPitch.ValueChanged += delegate { _hudAccPitch = (float)numAccPitch.Value; SaveCfg(); };
        y += 28;

        numAccRoll = MkTune(x, y, "tilt accel", (decimal)_hudAccRoll, 0m, 1m, 0.05m, 2);
        numFineP = MkTune(x + 168, y, "fine gain", (decimal)_hudFineP, 0m, 3m, 0.1m, 1);
        numAccRoll.ValueChanged += delegate { _hudAccRoll = (float)numAccRoll.Value; SaveCfg(); };
        numFineP.ValueChanged += delegate { _hudFineP = (float)numFineP.Value; SaveCfg(); };
        y += 28;

        numPitStick = MkTune(x, y, "stick pitch", (decimal)_hudPitStick, -350m, 350m, 10m, 0);
        numRollStick = MkTune(x + 168, y, "stick tilt", (decimal)_hudRollStick, -80m, 80m, 5m, 0);
        numPitStick.ValueChanged += delegate { _hudPitStick = (float)numPitStick.Value; SaveCfg(); };
        numRollStick.ValueChanged += delegate { _hudRollStick = (float)numRollStick.Value; SaveCfg(); };
        y += 28;

        // fov = the game's vertical FOV (the camera model that turns pixels into angles);
        // maneuver = how far a measurement may sit from the model before it looks wrong.
        numFov = MkTune(x, y, "fov", (decimal)_hudFovY, 40m, 120m, 1m, 0);
        numManeuver = MkTune(x + 168, y, "maneuver", (decimal)_hudManeuver, 1m, 400m, 1m, 0);
        numFov.ValueChanged += delegate { _hudFovY = (float)numFov.Value; SaveCfg(); };
        numManeuver.ValueChanged += delegate { _hudManeuver = (float)numManeuver.Value; SaveCfg(); };
        y += 28;

        numRefDist = MkTune(x, y, "ref match", (decimal)(_refDist * 100f), 2m, 60m, 1m, 0);
        numAxisW = MkTune(x + 168, y, "axis weight", (decimal)_hudAxisW, 0m, 3m, 0.1m, 1);
        numRefDist.ValueChanged += delegate { _refDist = (float)numRefDist.Value / 100f; SaveCfg(); };
        numAxisW.ValueChanged += delegate { _hudAxisW = (float)numAxisW.Value; SaveCfg(); };
        y += 28;

        // axis smooth = low-pass on the detector measurement BEFORE the estimator (seconds);
        // sky tex = how much smoother the region above the line must be than the region below it.
        // That is the test that stops a road or a field boundary INSIDE the ground being taken for
        // a horizon, and it is why the sky cannot be "learned" from a frame pointing at the ground.
        numMsTau = MkTune(x, y, "axis smooth", (decimal)_hudMsTau, 0m, 1m, 0.02m, 2);
        numSkyTex = MkTune(x + 168, y, "sky tex", (decimal)_hudSkyTex, 1m, 30m, 1m, 0);
        numMsTau.ValueChanged += delegate { _hudMsTau = (float)numMsTau.Value; SaveCfg(); };
        numSkyTex.ValueChanged += delegate { _hudSkyTex = (float)numSkyTex.Value; SaveCfg(); };
        y += 28;

        numSkyMin = MkTune(x, y, "sky min", (decimal)(_hudSkyMin * 100f), 0m, 80m, 1m, 0);
        numSkyMin.ValueChanged += delegate { _hudSkyMin = (float)numSkyMin.Value / 100f; SaveCfg(); };
        y += 28;

        // reload the reference photos ("base photos") without restarting
        btnReloadRefs = new Button();
        btnReloadRefs.Text = "reload refs";
        btnReloadRefs.SetBounds(x, y, 120, 24);
        btnReloadRefs.Click += delegate { _refSigs.Clear(); _refGoodL.Clear(); _refLine.Clear(); _refLoaded = false; LoadHudRefs(); };
        Controls.Add(btnReloadRefs);

        // CAPTURE REF: save THIS frame + its current horizon line as a labelled reference ("good").
        // Later frames that look like it adopt that line outright - that is the teach-by-example loop.
        btnCapRef = new Button();
        btnCapRef.Text = "capture ref (F4)";
        btnCapRef.SetBounds(x + 128, y, 120, 24);
        btnCapRef.Click += delegate { SaveRef(); };
        Controls.Add(btnCapRef);
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

        // PHYSICS LOG: record stick + measured horizon together so the stick->rate model can be
        // fitted from a real flight instead of guessed. Writes hudphys.csv next to the exe.
        chkPhys = new CheckBox();
        chkPhys.Text = "physics log  (hudphys.csv - stick + horizon per frame)";
        chkPhys.SetBounds(x, y, 330, 22);
        chkPhys.Checked = _physLog;
        chkPhys.CheckedChanged += delegate
        {
            _physLog = chkPhys.Checked; SaveCfg();
            if (_physLog)
            {
                PhysLogReset("checkbox");
                Log("physics log ON - writing hudphys.csv (shaped stick + the horizon the detector measured)");
            }
            else Log("physics log off");
        };
        Controls.Add(chkPhys);
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

        cmbPadRejoin = new NoWheelCombo();
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

        cmbPadLand = new NoWheelCombo();
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

        cmbPadReconnect = new NoWheelCombo();
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
        gAu.SetBounds(x, y, w, 182);
        Controls.Add(gAu);

        var lT = new Label();
        lT.Text = "Team:";
        lT.SetBounds(12, 26, 38, 20);
        gAu.Controls.Add(lT);

        cmbTeam = new NoWheelCombo();
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

        cmbDrone = new NoWheelCombo();
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

        cmbBomb = new NoWheelCombo();
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

        cmbPadAuto = new NoWheelCombo();
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

        cmbPadStop = new NoWheelCombo();
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

        chkPadSwap = new CheckBox();
        chkPadSwap.Text = "SWAP:";
        chkPadSwap.Checked = _padSwapOn;
        chkPadSwap.CheckedChanged += delegate { _padSwapOn = chkPadSwap.Checked; SaveCfg(); };
        chkPadSwap.SetBounds(8, 136, 62, 22);
        gAu.Controls.Add(chkPadSwap);

        cmbPadSwap = new NoWheelCombo();
        cmbPadSwap.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPadSwap.SetBounds(72, 135, 74, 24);
        foreach (string k in PAD.Keys) cmbPadSwap.Items.Add(k);
        cmbPadSwap.SelectedItem = PAD.ContainsKey(_padSwap) ? _padSwap : "B";
        cmbPadSwap.SelectedIndexChanged += delegate { if (cmbPadSwap.SelectedItem != null) { _padSwap = cmbPadSwap.SelectedItem.ToString(); SaveCfg(); } };
        gAu.Controls.Add(cmbPadSwap);

        var lSwap = new Label();
        lSwap.Text = "swaps your saved MAVIC / FPV setup (same as F7) - it only changes the selection, never starts a run";
        lSwap.SetBounds(150, 139, 300, 18);
        lSwap.ForeColor = Color.Silver;
        gAu.Controls.Add(lSwap);
        y += 190;

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
        numMatch.Minimum = 70; numMatch.Maximum = 100; numMatch.Increment = 5; numMatch.Value = _matchPct;
        numMatch.SetBounds(428, 22, 46, 24);
        numMatch.ValueChanged += delegate { _matchPct = (int)numMatch.Value; SaveCfg(); };
        gSt.Controls.Add(numMatch);
        y += 64;

        // ---- favourite setups: save the current loadout under MAVIC or FPV, then swap with one tap ----
        var gFav = new GroupBox();
        gFav.Text = "Setups   (swap with F7 or the pad SWAP button)";
        gFav.ForeColor = Color.Gainsboro;
        gFav.SetBounds(x, y, w, 66);
        Controls.Add(gFav);

        var bSaveM = new Button();
        bSaveM.Text = "save MAVIC";
        bSaveM.FlatStyle = FlatStyle.Flat;
        bSaveM.SetBounds(10, 20, 100, 26);
        bSaveM.Click += delegate { SaveFav("MAVIC"); };
        gFav.Controls.Add(bSaveM);

        var bSaveF = new Button();
        bSaveF.Text = "save FPV";
        bSaveF.FlatStyle = FlatStyle.Flat;
        bSaveF.SetBounds(114, 20, 100, 26);
        bSaveF.Click += delegate { SaveFav("FPV"); };
        gFav.Controls.Add(bSaveF);

        btnSwap = new Button();
        btnSwap.Text = "SWAP  (F7)";
        btnSwap.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnSwap.BackColor = Color.FromArgb(122, 92, 28);
        btnSwap.ForeColor = Color.White;
        btnSwap.FlatStyle = FlatStyle.Flat;
        btnSwap.SetBounds(222, 20, 92, 26);
        btnSwap.Click += delegate { SwapFav(); };
        gFav.Controls.Add(btnSwap);

        lblActiveFav = new Label();
        lblActiveFav.Text = "active: " + _activeFav + "  (MAVIC: " + _favMDrone + "/" + _favMBomb + "   FPV: " + _favFDrone + "/" + _favFBomb + ")";
        lblActiveFav.SetBounds(10, 46, 430, 16);
        lblActiveFav.ForeColor = Color.FromArgb(150, 210, 255);
        gFav.Controls.Add(lblActiveFav);
        y += 74;

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

    // A tuning spinner that NEVER changes on a plain wheel scroll (that was the accidental-change
    // bug) and only steps by a FINE amount when CTRL+wheel is used.
    // A ComboBox that IGNORES the mouse wheel. Scrolling the page with the cursor over a dropdown
    // was silently changing the team / drone / bomb without any click, and the next AUTO then
    // deployed the wrong setup. WM_MOUSEWHEEL (0x020A) is swallowed here.
    class NoWheelCombo : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A) return;
            base.WndProc(ref m);
        }
    }

    class TuneNum : NumericUpDown
    {
        public decimal Fine = 1m;
        protected override void OnMouseWheel(MouseEventArgs e) { /* never change on a bare wheel */ }
        public void WheelStep(int delta)
        {
            decimal v = Value + (delta > 0 ? Fine : -Fine);
            if (v < Minimum) v = Minimum; if (v > Maximum) v = Maximum;
            Value = v;
        }
    }

    // Route the wheel ourselves so CTRL+wheel works while HOVERING a dial (WinForms only sends the
    // wheel to the focused control). Plain wheel over a dial is swallowed - no more accidental edits.
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL) return false;
        Point sp = Cursor.Position;
        foreach (Control c in Controls)
        {
            TuneNum tn = c as TuneNum;
            if (tn == null) continue;
            if (!tn.RectangleToScreen(tn.ClientRectangle).Contains(sp)) continue;
            if ((ModifierKeys & Keys.Control) != 0)
                tn.WheelStep((short)((long)m.WParam >> 16));
            return true;   // swallow either way - a bare wheel over a dial must do nothing
        }
        return false;
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { Application.AddMessageFilter(this); } catch { }
    }

    // caption + numeric spinner for a HUD tuning dial
    NumericUpDown MkTune(int cx, int cy, string caption, decimal val, decimal min, decimal max, decimal inc, int decimals)
    {
        var lbl = new Label();
        lbl.Text = caption;
        lbl.ForeColor = Color.Silver;
        lbl.SetBounds(cx, cy, 90, 20);
        Controls.Add(lbl);
        var nud = new TuneNum();
        nud.Minimum = -1000000m; nud.Maximum = 1000000m;   // NO constraints - fine tune freely
        nud.Increment = inc;
        nud.Fine = (inc >= 1m) ? 1m : inc;                 // CTRL+wheel step
        nud.DecimalPlaces = decimals;
        nud.Value = val;   // clamp into the wide bounds
        if (nud.Value < nud.Minimum) nud.Value = nud.Minimum;
        if (nud.Value > nud.Maximum) nud.Value = nud.Maximum;
        nud.BackColor = Color.FromArgb(14, 15, 18);
        nud.ForeColor = Color.Gainsboro;
        nud.SetBounds(cx + 92, cy - 2, 70, 24);
        Controls.Add(nud);
        // hover a dial (or its name) to see what it does
        if (_tips == null) { _tips = new ToolTip(); _tips.InitialDelay = 250; _tips.ReshowDelay = 80; _tips.AutoPopDelay = 25000; }
        string tip = TuneTip(caption);
        if (tip != null) { _tips.SetToolTip(lbl, tip); _tips.SetToolTip(nud, tip); }
        return nud;
    }

    ToolTip _tips;
    static string TuneTip(string c)
    {
        switch (c)
        {
            case "pitch speed": return "How fast the horizon travels up/down when you push the right stick forward/back (px/s).";
            case "roll speed": return "How fast the horizon banks when you move the right stick left/right (deg/s).";
            case "lock speed": return "How quickly the IMAGE horizon pulls the line back to the real horizon.";
            case "drift speed": return "How slowly the line drifts when there is no image lock / no stick input.";
            case "height adj": return "Nudges the whole horizon line up/down (px).";
            case "smoothing": return "Gyro smoothing time (s). Higher = smoother but laggier.";
            case "line gap": return "Vertical spacing between ladder rungs (px per degree).";
            case "staircase": return "How far the rungs slide SIDEWAYS when you bank. 0 = no slide.";
            case "line length": return "How long each ladder rung is.";
            case "rung tilt": return "0 = rungs stay FLAT; 1 = rungs tilt to stay parallel with the real horizon.";
            case "max tilt": return "Hard cap (deg) on how far the rungs may tilt when banking - stops them swinging out.";
            case "image limit": return "Max rate the IMAGE may correct the horizon (deg/s). Stops a false lock yanking the line.";
            case "down limit": return "The image is not allowed to push the horizon DOWN past this many px (soft limit).";
            case "spread": return "How fast the rungs fan apart with tilt (right stick left/right + pitch).";
            case "spread accel": return "Expo on the fan: higher = the rungs stay tight for small banks and fan out fast on big ones.";
            case "camera trust": return "How hard the image horizon pulls the lines overall.";
            case "throttle": return "Left-stick throttle nudges the horizon up/down (px).";
            case "roll shift": return "Manual roll offset (deg) if the horizon sits tilted at rest.";
            case "height shift": return "Manual pitch offset (px) if the horizon sits high/low at rest.";
            case "bad lift": return "Lift the horizon (deg) when the detector quality/confidence is poor.";
            case "texture": return "How much the texture cue backs the colour cue in the detector (0 = colour only).";
            case "pitch accel": return "Expo on the right-stick pitch. Higher = a light push does less, a hard push does more.";
            case "tilt accel": return "Expo on the right-stick bank. Higher = a light bank does less.";
            case "fine gain": return "Boosts SMALL right-stick inputs so fine movements move the line more (full push unchanged).";
            case "stick pitch": return "Direct horizon lift from the right stick Y (px). Negative inverts.";
            case "stick tilt": return "Direct bank from the right stick X (deg). Negative inverts.";
            case "tree drop": return "Push the horizon DOWN (deg) when the detector sees lots of clutter (trees/structures), which pull a lock UP onto the canopy.";
            case "min colour": return "Reject frames whose overall colour spread is below this - a flat, one-tone frame has no horizon in it.";
            case "override %": return "Right-stick travel (%) at which the STICK takes over from the image. Below it the image is in full control.";
            case "estimator": return "1 = use the new angle-space alpha-beta/Kalman estimator with the stick as a control input. 0 = the old filter.";
            case "fov": return "The game's vertical field of view (deg). Sets the focal length used to turn pixels into real angles, and back.";
            case "maneuver": return "How fast the estimator believes the attitude may swing (deg^2/s). Bigger = it reacts faster but trusts the model less.";
            case "ref match": return "How close a frame must be to a saved hudref\\\\ photo to count as a match (%) - lower = stricter.";
            case "sky flat": return "HOW FLAT the top band must be, as a % of the whole frame (real sky is far smoother than the scene). Over this and the frame is THROWN AWAY - busy grass/ruins/trees at the top mean the lock is a boundary inside the ground, so we coast on the stick instead. LOWER = stricter. 0 = off.";
            case "sky min": return "THROW THE FRAME AWAY if it has less verified sky than this (%). Banked hard looking down at ground/clutter there is no horizon to measure - discarding it stops a terrain edge becoming a false low line, and the estimator just coasts on the stick. 0 = never discard.";
            case "axis smooth": return "Low-pass time (s) on the detector measurement before the estimator uses it. Higher = steadier but laggier; 0 = raw.";
            case "axis weight": return "INFLUENCE of the new sky/ground colour axis (per-frame brightness-vs-blueness cue). 0 = off, 1 = default, higher pulls harder toward sky-above/ground-below.";
            default: return null;
        }
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

        // RECONNECT COOLDOWN - for the next 20s do NOT look for a drone view. The reconnect relaunches
        // the game, and the leftover menus / loading text / tail of the old drone view were being
        // read as a drone, popping the HUD up in the middle of a reconnect where it makes no sense.
        // The HUD can still be started by the flow (Deploy As Drone) - this only silences the SCAN.
        _hudCooldownUntil = unchecked(Environment.TickCount + 20000);
        StopRfWatch();
        Log("   reconnect: HUD detect paused for 20s");

        FindServer();   // always re-read, so we rejoin the server we are on right now
        if (_placeId == "" || _serverId == "") { Log("rejoin: no server found yet"); return; }

        // ALWAYS rejoin the exact same server. We used to drop the gameInstanceId when the same
        // server was retried within 25s, but that silently put the player into a random server -
        // the deep link with gameInstanceId is what pins the same server.
        string uri = "roblox://placeId=" + _placeId + "&gameInstanceId=" + _serverId;

        // silent reconnect: relaunch immediately, no LAND NOW and no black cover
        if (!overlay)
        {
            Log("   straight reconnect (no overlay)");
            // RESTART the OBS CLI: clear the accumulated log and print a fresh uplink line, so the
            // stream shows the reconnect from a clean slate instead of the old run's tail.
            OverlayHub.I.Active(true);
            AddBlackLine("> quick reconnect", "");
            AddBlackLine("re-engaging uplink with " +
                (_serverId.Length > 8 ? _serverId.Substring(0, 8) : _serverId), "OK");
            // DRIVE THE CLI PROGRESS. _flowPct resets to 0 once a run completes, and this branch used
            // to only print lines - so the OBS terminal just sat on 0% through the whole reconnect.
            // Run a short staged boot ramp on its own thread so it animates like the full flow does.
            string w2 = why;
            Thread bt = new Thread(delegate ()
            {
                try
                {
                    // Same narration and PACING as the full LAND NOW boot - one step at a time, each
                    // taking a beat, ~20s end to end. A quick reconnect should read like a real
                    // link-up, not snap straight to 100%.
                    AddBlackProgress(0.08f, "link down");
                    AddBlackLine("> reconnect - re-engaging uplink", "");
                    Thread.Sleep(900);
                    AddBlackLine("  session torn down", "OK");
                    AddBlackProgress(0.18f, "standby");
                    Thread.Sleep(1300);

                    string ip = RandIp();
                    AddBlackLine("> connect " + ip + ":47320", "");
                    Thread.Sleep(1500);
                    AddBlackProgress(0.30f, "uplink");
                    AddBlackLine("resolving ground station " + ip, "OK");
                    Thread.Sleep(1400);
                    AddBlackLine("calibrating inertial nav (imu)", "OK");
                    Thread.Sleep(1400);
                    AddBlackProgress(0.45f, "imu");
                    AddBlackLine("spooling gyro stabiliser", "OK");
                    Thread.Sleep(1400);
                    AddBlackLine("negotiating encrypted uplink", "OK");
                    Thread.Sleep(1500);
                    AddBlackProgress(0.62f, "airframe");
                    AddBlackLine("selecting airframe  [" + _drone + "]", "OK");
                    Thread.Sleep(1400);
                    AddBlackLine("checking warhead rack", "OK");
                    Thread.Sleep(1500);
                    AddBlackProgress(0.80f, "telemetry");
                    AddBlackLine("syncing telemetry stream", "OK");
                    Thread.Sleep(1400);
                    AddBlackProgress(0.92f, "handshake");
                    AddBlackLine("  handshake complete", "OK");
                    Thread.Sleep(1300);
                    AddBlackProgress(1.0f, "uplink online");
                    AddBlackLine("  link live", "OK");
                }
                catch { }
            });
            bt.IsBackground = true;
            bt.Start();
            LaunchRejoin(uri, w2);
            return;
        }

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
                        // WAIT FOR "JOINING SERVER". That screen is the real start of the reconnect;
                        // starting AUTO before it runs the flow against the session we are leaving.
                        if (st == "loading" || PhraseIn("Joining server", ws) || PhraseIn("Joining Server", ws))
                        { Log("   Joining Server - continuing the flow"); break; }
                        // fallback only if the joining screen is never readable (e.g. it is skipped)
                        bool live = (st == "team select" || st == "lobby" || st == "loadout" ||
                                     st == "map" || st == "team base");
                        if (!live) { gone++; if (gone >= 10) { Log("   no Joining Server read - reconnecting anyway"); break; } }
                        else gone = 0;
                        InvalidateOcr();
                        Thread.Sleep(300);
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
        _autoDeployed = false;          // cleared so the self-heal retry knows if we got there
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
            // Self-healing: if the run stops before the drone is actually deployed, back out with
            // the red Return and re-run. AutoSteps is resume-aware, so it re-reads the screen and
            // picks up from wherever we ended up - a failed base/warhead step re-plans instead of
            // leaving the run dead in a menu.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try { AutoSteps(g); }
                catch (Exception ex) { Log("AUTO error: " + ex.Message); }
                if (!Alive(g) || _autoDeployed) break;
                // If the Deploy As Drone button is actually on screen, the last click just did not
                // register - retry it rather than backing out with Return (which used to fire even
                // though the deploy button was right there).
                if (TeamBasePhraseUp(OcrWords()))   // corroborated + inside the panel, not a fused phrase
                {
                    Log("AUTO: Deploy As Drone is on screen - clicking it again instead of returning");
                    if (ClickDeployAsDrone(g))
                    {
                        _autoDeployed = true;
                        if ((_watchHome || _hudOn) && _asDrone) StartRfWatch();
                        break;
                    }
                }
                Log("AUTO: stopped before deploy - back out with Return and re-plan (" + attempt + "/2)");
                AddBlackError("0x2C", "deploy sequence incomplete - re-planning");
                try { ClickRedReturn(); } catch { }
                Thread.Sleep(700);
                InvalidateOcr();
            }
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
        OverlayHub.I.SetProgress(_flowPct, T("paused - awaiting re-establishment"));
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

        // If we start ON the map's POINT lock screen (one POINT, no TEAM BASE) a point got
        // misclicked. Get out FIRST - otherwise "POINT" reads as "map" and the flow barrels on.
        if (MapPointLockUp())
        {
            Log("entry: map POINT lock screen (misclick) - backing out with Return");
            ClickRedReturn();
            Thread.Sleep(500);
            return;
        }

        bool mapUp = PhraseIn("POINT", ws0) || PhraseIn("Base", ws0);
        bool panelUp = TeamBasePhraseUp(ws0);

        // Already FLYING? Then there is nothing to deploy - just bring the RF feed / HUD back up.
        // Pressing AUTO while already in the drone used to sit in step 1 for 120s waiting for a
        // menu that never arrives (the drone OSD reads as "unknown"), so the HUD never came back.
        if (!MenuOnScreen() && (ReadFlightSecs() >= 0 || CornerLinked() || DroneKeyword()))
        {
            Log("AUTO: already in the drone view - bringing the RF feed / HUD back up");
            AddBlackProgress(1.0f, "drone online");
            AddBlackLine("drone online", "OK");
            HideBlack();
            _hudStyleUav = MavicBomb(_bomb);
            _autoDeployed = true;
            if ((_watchHome || _hudOn) && _asDrone) StartRfWatch();
            return;
        }

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
            if (WaitPhrase("SELECT DRONE", 3000, g))
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

            // Pull the map BACK first (zoom OUT): the Base sits at the edge of the map and pulling
            // back brings the label away from the border, where it is cut off or overlapped by a
            // POINT pin. Over-scrolling is harmless - the map clamps at its own max zoom.
            // Every burst is verified against a cheap screen signature. If the map never changes,
            // say so in the log: "the wheel did nothing" must never be a silent failure again.
            int[] zsig = Signature();
            int zQuietOut = 0;
            for (int z = 1; z <= 10 && !sawBase && Alive(g) && zQuietOut < 4; z++)
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
                Log("   Base not visible - pulling the map back (" + z + "/10)");
                // the wheel goes to the window under the cursor, and the game must be
                // in front or the wheel is swallowed - so focus, drift, then scroll
                FocusRoblox();
                MoveOverGameSoft(z);
                ScrollOut(2);
                Thread.Sleep(180);
                int[] znow = Signature();
                if (DiffPct(zsig, znow) < 1.0) zQuietOut++; else zQuietOut = 0;
                zsig = znow;
            }
            if (zQuietOut >= 4)
                Log("   the map stopped reacting after " + zQuietOut + " bursts (already at max zoom, or the wheel is not landing)");

            // Then the other way - the Base can be inside a clump that needs pulling IN to separate.
            // The Base labels separate from POINT pins when zoomed in, so this pass often reads best.
            int zQuietIn = 0;
            for (int z = 1; z <= 6 && !sawBase && Alive(g) && zQuietIn < 3; z++)
            {
                List<Hit> bh = FindPhraseAll("Base", null, OcrWordsWhiten());
                if (bh.Count == 0) bh = FindPhraseAll("Base", null, OcrWords());
                if (bh.Count > 0)
                {
                    sawBase = true;
                    baseX = bh[0].X; baseY = bh[0].Y;
                    Log("   Base label is visible at (" + baseX + "," + baseY + ") (after " + (z - 1) + " zoom-ins)");
                    break;
                }
                Log("   Base still not visible - coming back in (" + z + "/6)");
                FocusRoblox();
                MoveOverGameSoft(z + 10);
                ScrollIn(3);
                Thread.Sleep(180);
                int[] znow2 = Signature();
                if (DiffPct(zsig, znow2) < 1.0) zQuietIn++; else zQuietIn = 0;
                zsig = znow2;
            }
            // both directions dead = the wheel is not reaching the game at all; the log must say it
            if (zQuietOut >= 4 && zQuietIn >= 3)
                Log("   WARNING: the map never reacted to the wheel (both directions quiet) - zooming is not working, panning instead");

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

            // LAST RESORT: OCR never produced "Base", but the base marker is still on the map. Its
            // red is the BRIGHT red (#ED2224) and, unlike a full/contested POINT pin (#D51B1E), it
            // is the biggest red thing on the map - so find it from the pixels.
            if (!sawBase)
            {
                int rx, ry;
                if (FindRedMapLabel(out rx, out ry))
                {
                    sawBase = true; baseX = rx; baseY = ry;
                    Log("   Base found by its bright-red marker at (" + rx + "," + ry + ")");
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

        // MISCLICK GUARD: if the base click selected a map POINT instead, we are on the point-lock
        // screen ("Press Deploy button to lock in your choice" + Deploy/Return). Never continue to
        // the warhead / deploy steps from here - back out with Return and let the run re-plan.
        if (MapPointLockUp())
        {
            Log("5) map POINT got selected instead of the Base (misclick) - backing out with Return");
            ClickRedReturn();
            Thread.Sleep(500);
            return;
        }

        // 6 - warhead. Click the fixed grid cell for the chosen warhead and confirm the
        //     green highlight, rather than trusting the tiny label text.
        if (!GoOn(g)) return;
        bool warheadOk = true;
        AddBlackProgress(0.62f, "payload");
        if (_stepBomb && _bomb != "(none)")
        {
            if (TeamBaseUp(panelUp))
            {
                Log("6) selecting warhead " + _bomb + "...");
                warheadOk = ClickWarhead(g);
                if (!warheadOk) Log("   " + _bomb + " is not equipped");
                AddBlackProgress(0.75f, "warhead armed");
            }
            else
            {
                // The panel could not be read, so the warhead was NEVER selected. This used to
                // leave warheadOk TRUE (it starts true and the whole block was gated on the panel),
                // so a reconnect - where the panel often is not readable yet - went straight on to
                // click Deploy As Drone with whatever was loaded. Unverifiable is a FAILURE.
                warheadOk = false;
                Log("6) could not verify/select the warhead - TEAM BASE panel not readable (will re-plan)");
            }
        }
        else Log("6) bomb step skipped (" + (_bomb == "(none)" || !_stepBomb ? "step off" : "TEAM BASE not open") + ")");

        // 7 - final deploy, only from the Team Base panel, and only once the warhead box is
        //     green. If the requested warhead could not be equipped, stop here rather than
        //     deploying with the wrong loadout.
        if (!GoOn(g)) return;
        if (!warheadOk)
        {
            // Wrong bombs / loadout: press the red Return to close the panel, WAIT for the LOADOUT
            // screen to come back, then open LOADOUT so the drone + payload can be re-picked. The AUTO
            // loop then re-runs the (resume-aware) flow and continues from the loadout screen.
            Log("7) wrong loadout (" + _bomb + " not equipped) - backing out with Return, then LOADOUT");
            AddBlackError("0x13", "payload mismatch - re-planning loadout");
            BackOutToLoadout(g);
            HideBlack();
            return;
        }
        if (MapPointLockUp())
        {
            Log("7) on the map point-lock screen (misclick) - backing out, NOT pressing Deploy");
            ClickRedReturn();
            return;
        }
        if (TeamBaseUp(panelUp))
        {
            // HARD PAYLOAD GATE: re-verify the warhead is REALLY the selected one before touching
            // Deploy As Drone. If it is not, treat it exactly like a wrong loadout - back out and
            // re-plan - instead of launching the wrong bomb.
            if (!WantedWarheadGreen())
            {
                // ONE MORE TRY before backing all the way out. A single swallowed cell click used to
                // send the flow back through Return -> LOADOUT -> base again, which is slow and is
                // what looked like "it skipped the bomb". Re-click the cell and re-test first.
                Log("7) " + _bomb + " not green - one more try at the warhead cell");
                ClickWarhead(g);
                Thread.Sleep(450);
                InvalidateOcr();
            }
            if (!WantedWarheadGreen())
            {
                Log("7) " + _bomb + " is NOT the selected warhead - NOT deploying (re-planning the loadout)");
                AddBlackError("0x13", "payload not verified - re-planning loadout");
                BackOutToLoadout(g);
                HideBlack();
                return;
            }
            AddBlackProgress(0.84f, "arm");
            if (_asDrone)
            {
                Log("7) clicking Deploy As Drone...");
                AddBlackLine("connecting to drone", "");
                AddBlackProgress(0.90f, "connecting to drone");
                bool ok = false;
                // CLICK UNTIL THE PANEL ACTUALLY CLOSES. The click was being sent (accepted=2/2) but
                // the game sometimes ignored it, leaving the TEAM BASE panel up - from the outside
                // that looks exactly like "it hovers over Deploy As Drone but never clicks". Verify
                // the panel is really gone and click again if it is not.
                for (int depTry = 1; depTry <= 4 && Alive(g); depTry++)
                {
                    if (ClickDeployAsDrone(g)) ok = true;
                    Thread.Sleep(900);
                    InvalidateOcr();
                    bool still = TeamBasePhraseUp(OcrWords());   // corroborated - see TeamBasePhraseUp
                    if (!still) { ok = true; break; }        // the panel closed - we are away
                    ok = false;
                    if (depTry < 4) Log("   Deploy As Drone: TEAM BASE panel still open (try " + (depTry + 1) + "/4) - clicking again");
                }
                if (!ok) Log("   Deploy As Drone did not react");
                AddBlackLine("  uplink established", ok ? "OK" : "BAD");
                _autoDeployed = ok;      // only a real click counts - otherwise the flow re-plans
                if (ok) _autoResets = 0;   // a real deploy clears the wrong-payload loop breaker
            }
            else
            {
                Log("7) clicking Deploy...");
                if (ClickPhraseVerified("Deploy", "As", g)) _autoDeployed = true;
                else Log("   Deploy did not react");
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
    // Is the TEAM BASE PANEL really up? The bare phrase is NOT proof: on the MAP screen the nav bar
    // carries the word TEAM (inside "CHANGE TEAM") and the map carries its own "Base" label, and the
    // matcher can fuse the two into "TEAM BASE". That made AUTO believe the panel was already open,
    // SKIP the entire Base step (the "it is not finding the base" report) and then fit the warhead
    // grid to nav-bar text. The panel also prints WARHEAD and the Deploy As Drone button, which the
    // map never has - so require one of those, or the panel button stack, as corroboration.
    // Is (x,y) inside the centred TEAM BASE panel? The panel buttons and text all live in the
    // middle of the screen; the nav bar (top) and the objectives (top-right) do not. This is the
    // test that stops a phrase being FUSED out of unrelated words elsewhere on screen.
    static bool InPanel(float x, float y)
    {
        int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
        return x > W * 22 / 100 && x < W * 80 / 100 && y > H * 25 / 100 && y < H * 85 / 100;
    }

    bool TeamBasePhraseUp(List<string[]> ws)
    {
        if (!PhraseIn("TEAM BASE", ws)) return false;
        if (BasePanelVisual()) return true;
        // The corroborating phrases MUST be in the panel. Without this, the words DEPLOY (nav bar),
        // AS (from the objective "Get 14 headshots with AS Val") and DRONE (from "SELECT DRONE")
        // fuse into "Deploy As Drone" on the LOADOUT screen - which is what made the flow click the
        // nav bar at (363,122), hit LOADOUT instead of Deploy, and then believe it had deployed.
        foreach (Hit hh in FindPhraseAll("WARHEAD", null, ws)) if (InPanel(hh.X, hh.Y)) return true;
        foreach (Hit hh in FindPhraseAll("Deploy As Drone", null, ws)) if (InPanel(hh.X, hh.Y)) return true;
        return false;
    }

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
            // CORROBORATED: the bare phrase can be faked by the MAP screen (the nav bar has
            // "CHANGE TEAM" and the map has its own "Base" label), which made the flow skip the
            // whole Base step. See TeamBasePhraseUp.
            if (TeamBasePhraseUp(OcrWords())) return true;
            if (PhraseOnScreen("DEPLOY AS DRONE") || PhraseOnScreenWhiten("DEPLOY AS DRONE")) return true;
            if (PhraseOnScreen("WARHEAD") || PhraseOnScreenWhiten("WARHEAD")) return true;
            InvalidateOcr();
            Thread.Sleep(200);
        }
        return false;
    }

    // PIXEL check (no OCR): the TEAM BASE panel is drawn with a GREEN "Deploy", a MAROON "Return" and
    // a GOLD "Deploy As Drone" button stacked at the SAME x. The map's POINT-lock screen has only the
    // first two. So a wide maroon bar with a wide green bar above AND a wide gold bar below proves we
    // are on the TEAM BASE panel - even when the bright map defeats Windows OCR entirely. This is what
    // stops the flow pressing Return (backing out) when it is actually sitting right on the panel.
    bool BasePanelVisual()
    {
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            if (px == null || W < 100 || H < 100) return false;
            int x0 = W * 12 / 100, x1 = W * 82 / 100;
            for (int y = H * 26 / 100; y < H * 74 / 100; y++)
            {
                int mc; int ml = HudRun(px, W, y, x0, x1, HudMaroon, out mc);
                if (ml < 150) continue;                       // the maroon "Return" button
                bool green = false;
                for (int yy = y - 95; yy < y - 15; yy++)
                {
                    if (yy < 0) continue;
                    int c; if (HudRun(px, W, yy, x0, x1, HudGreen, out c) >= 150 && Math.Abs(c - mc) < 70) { green = true; break; }
                }
                if (!green) continue;                          // the green "Deploy" above it
                for (int yy = y + 18; yy < y + 100 && yy < H; yy++)
                {
                    int c; if (HudRun(px, W, yy, x0, x1, HudGold, out c) >= 150 && Math.Abs(c - mc) < 70) return true;
                }                                              // the gold "Deploy As Drone" below it
            }
        }
        catch { }
        return false;
    }
    // Classify the BUTTON FILL around a text hit, by exact hex. Returns
    //   3 = Deploy As Drone  #8D7136 gold   (r>=128, g>=98, b<=64, r-g>=18, g-b>=42)
    //   2 = Return           #653C39 maroon
    //   1 = Deploy           #4B7D33 green  (g-r>=30, g-b>=55, g>=100)
    //   0 = no clear button fill (terrain / something else)
    // A majority vote over a grid around the label, so a few text/edge pixels cannot flip it and
    // the muddy map terrain (#645743..#74634A: b 65-75) fails the tight gold/green tests.
    static int ButtonKindRgb(int r, int g, int b)
    {
        if (r >= 128 && g >= 98 && b <= 64 && r - g >= 18 && g - b >= 42) return 3;      // gold
        if (r >= 90 && r <= 125 && g <= 80 && b <= 75 && r - g >= 28 && r - b >= 30) return 2; // maroon
        if (g - r >= 30 && g - b >= 55 && g >= 100) return 1;                            // green
        return 0;
    }
    int ButtonKindAt(int tx, int ty)
    {
        try
        {
            int W, H; int[] px = Grab(out W, out H);
            if (px == null) return 0;
            int k3 = 0, k2 = 0, k1 = 0, n = 0;
            for (int dx = -80; dx <= 140; dx += 8)
                for (int dy = -12; dy <= 22; dy += 4)
                {
                    int x = tx + dx, y = ty + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int v = px[y * W + x];
                    n++;
                    int k = ButtonKindRgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
                    if (k == 3) k3++; else if (k == 2) k2++; else if (k == 1) k1++;
                }
            if (n < 40) return 0;
            int best = k3; int res = 3;
            if (k2 > best) { best = k2; res = 2; }
            if (k1 > best) { best = k1; res = 1; }
            if (best < n * 45 / 100) return 0;      // no dominant button colour here
            return res;
        }
        catch { return 0; }
    }
    // Locate "Deploy As Drone" from the panel's TWO OPAQUE bars, not by OCR and not by scanning the
    // whole frame for gold. The panel is translucent, so only the green "Deploy" (#4B7D33) and maroon
    // "Return" (#653C39) fills are stable across maps and terrain; the third button's tint varies with
    // whatever map is behind it. So: find the green bar and the maroon bar (exact hex, both must be
    // wide runs), take the button PITCH from their spacing, step one pitch below Return, and VERIFY
    // the gold fill (#8D7136..#93743A) of the target before returning it.
    // Validated on a real frame: green y=539 (222px), maroon y=592 (212px), pitch 53 -> (747,645).
    bool PanelDeployAsDrone(out int px2, out int py2)
    {
        px2 = -1; py2 = -1;
        try
        {
            int W, H; int[] q = Grab(out W, out H);
            if (q == null) return false;
            // The buttons are SOLID BARS, so match the LONGEST CONTIGUOUS RUN in each row rather
            // than counting pixels: a green field or a brown patch has thousands of scattered
            // matching pixels but no long run, and counting let terrain win the row and throw the
            // pitch out. Runs cannot be fooled that way.
            int x0 = W * 12 / 100, x1 = W * 88 / 100;
            int gY = -1, mY = -1, gC = -1, mC = -1, gN = 0, mN = 0;
            for (int y = H * 24 / 100; y < H * 82 / 100; y++)
            {
                int gBest = 0, gBestC = -1, gRun = 0, gRunStart = 0;
                int mBest = 0, mBestC = -1, mRun = 0, mRunStart = 0;
                for (int x = x0; x < x1; x++)
                {
                    int v = q[y * W + x];
                    int r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
                    bool isG = (g - r >= 30 && g - b >= 55 && g >= 100);
                    bool isM = (r >= 90 && r <= 125 && g <= 80 && b <= 75 && r - g >= 28 && r - b >= 30);
                    if (isG) { if (gRun == 0) gRunStart = x; gRun++; if (gRun > gBest) { gBest = gRun; gBestC = (gRunStart + x) / 2; } }
                    else gRun = 0;
                    if (isM) { if (mRun == 0) mRunStart = x; mRun++; if (mRun > mBest) { mBest = mRun; mBestC = (mRunStart + x) / 2; } }
                    else mRun = 0;
                }
                if (gBest > gN) { gN = gBest; gY = y; gC = gBestC; }
                if (mBest > mN) { mN = mBest; mY = y; mC = mBestC; }
            }
            // The green Deploy and maroon Return bars are ~200px wide. Require a solid run so a
            // terrain streak cannot stand in for a button.
            if (gN < 120 || mN < 120 || mY <= gY)
            {
                Log("   panel anchor: green run " + gN + ", maroon run " + mN + " (y " + gY + " vs " + mY + ") - not enough" +
                    (gN < 120 || mN < 120 ? "  [the button bar was not found - a LOCKED/greyed Deploy As Drone has no solid bar]" : ""));
                return false;
            }
            int pitch = mY - gY; if (pitch < 30 || pitch > 80) pitch = 58;
            int tx = mC, ty = mY + pitch;
            // The panel is the SAME three bars every time: Deploy (green) -> Return (maroon) ->
            // Deploy As Drone (gold), one pitch apart. Now that BOTH bars are found with a solid
            // run and the maroon is below the green, the gold button IS one pitch under Return -
            // so use the geometry. The gold fill is only a CONFIDENCE reading (the button is
            // translucent, so its colour shifts with the terrain behind it); a poor fill must not
            // veto a click the panel layout already proves.
            int go = 0, n = 0;
            for (int dx = -60; dx <= 60; dx += 12)
                for (int dy = -10; dy <= 10; dy += 5)
                {
                    int sx = tx + dx, sy = ty + dy;
                    if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                    int v = q[sy * W + sx]; n++;
                    int r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
                    if (r >= 128 && g >= 98 && b <= 64 && r - g >= 18 && g - b >= 42) go++;
                }
            int goldPct = n > 0 ? 100 * go / n : 0;
            px2 = tx; py2 = ty;
            Log("   TEAM BASE anchors: Deploy(green) y=" + gY + " run " + gN + ", Return(maroon) y=" + mY + " run " + mN +
                ", pitch " + pitch + " -> Deploy As Drone at (" + tx + "," + ty + ")  gold " + goldPct + "%" +
                (goldPct < 35 ? " (low, but the panel geometry puts it there)" : ""));
            return true;
        }
        catch { return false; }
    }
    static bool HudMaroon(int p) { int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255; return r > 88 && r < 160 && g > 38 && g < 88 && b > 38 && b < 90 && r > g + 28 && r > b + 26; }
    static bool HudGold(int p) { int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255; return r > 128 && g > 98 && g < 178 && b < 108 && r > g + 6 && g > b + 16; }
    static bool HudGreen(int p) { int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255; return g > r + 14 && g > b + 24 && g > 88; }
    // expo/acceleration curve on a -1..1 stick value: a=0 linear, a=1 full cubic. Keeps the sign and
    // the endpoints, but a partial deflection moves the line LESS - so the harder you push, the faster
    // it goes (dial "pitch accel"/"tilt accel").
    static float Shape(float x, float a)
    {
        if (a < 0f) a = 0f; if (a > 1f) a = 1f;
        return x * ((1f - a) + a * x * x);
    }
    // LOW-END boost: multiplies small stick inputs by (1+f) while leaving a FULL push unchanged
    // (endpoints and sign preserved). This is what makes fine movements do more without touching the
    // hard-push feel. Smooth, so no kink at the centre.
    static float FineGain(float x, float f)
    {
        if (f <= 0f) return x;
        float a = x < 0f ? -x : x;
        return x * (1f + f) / (1f + f * a);
    }
    // smoothstep 0..1 between lo and hi - a soft S-curve so nothing snaps on/off
    static float Smooth01(float x, float lo, float hi)
    {
        if (hi <= lo) return x >= hi ? 1f : 0f;
        float t = (x - lo) / (hi - lo);
        if (t < 0f) t = 0f; if (t > 1f) t = 1f;
        return t * t * (3f - 2f * t);
    }
    static int HudRun(int[] px, int W, int y, int x0, int x1, Func<int, bool> f, out int centre)
    {
        int best = 0, bc = -1, cur = 0, cs = -1, row = y * W;
        for (int x = x0; x < x1; x++)
        {
            if (f(px[row + x])) { if (cur == 0) cs = x; cur++; if (cur > best) { best = cur; bc = cs + cur / 2; } }
            else cur = 0;
        }
        centre = bc; return best;
    }

    // The MAP's "point lock" screen: a POINT got selected and it says to press Deploy to lock your
    // choice, with a green Deploy and a maroon Return. Seeing this where the TEAM BASE panel should
    // be means the base click MISSED and hit a map POINT - a misclick. We must back out with Return
    // and never press Deploy here (that would lock a spawn point).
    bool MapPointLockUp()
    {
        try
        {
            // PIXEL truth first: if the green/maroon/gold button stack is on screen, we ARE on the
            // TEAM BASE panel - never call it a misclick (OCR is blind on the bright map).
            if (BasePanelVisual()) return false;
            // The TEAM BASE panel prints the SAME "Press Deploy button to lock in your choice" text,
            // so the phrase alone is NOT enough - only treat it as a misclick when the TEAM BASE
            // markers (header / Deploy As Drone / WARHEAD / the warhead cells) are absent.
            List<string[]> ws = OcrWords();
            List<string[]> ww = OcrWordsWhiten();
            bool basePanel = PhraseIn("TEAM BASE", ws) || PhraseIn("DEPLOY AS DRONE", ws) || PhraseIn("WARHEAD", ws)
                          || PhraseIn("TEAM BASE", ww) || PhraseIn("DEPLOY AS DRONE", ww) || PhraseIn("WARHEAD", ww)
                          || PhraseIn("Standard Frag", ws) || PhraseIn("PG-7VS", ws) || PhraseIn("TBG-7", ws)
                          || PhraseIn("Standard Frag", ww) || PhraseIn("PG-7VS", ww) || PhraseIn("TBG-7", ww);
            if (basePanel) return false;
            // COUNT the POINT labels: the normal map shows SEVERAL (POINT A..F) -> we are on the map
            // with the Base, fine. EXACTLY ONE POINT means a point got selected - the bad lock screen.
            int pc = FindPhraseAll("POINT", null, ws).Count;
            int pc2 = FindPhraseAll("POINT", null, ww).Count;
            if (pc2 > pc) pc = pc2;
            if (pc == 1) return true;
            if (pc == 0)
            {
                bool phrase = PhraseIn("lock in your choice", ws) || PhraseIn("Press Deploy button", ws)
                           || PhraseIn("lock in your choice", ww) || PhraseIn("Press Deploy button", ww);
                return phrase;
            }
            return false;
        }
        catch { }
        return false;
    }

    // The TEAM BASE / map panels have a red "Return" button. When the flow cannot find what it
    // expects, pressing it backs out of the panel instead of leaving the run stuck.
    // Wrong loadout: return out of the panel, wait for the LOADOUT screen, open it so the drone +
    // payload can be re-picked. A second is NOT enough for the loadout screen to actually load, so we
    // wait for the LOADOUT tab to appear and then give it another moment before clicking it.
    void BackOutToLoadout(int g)
    {
        try
        {
            // LOOP BREAKER. Each wrong payload used to: Return -> open LOADOUT -> (wait for a
            // SELECT DRONE panel that the weapons tab does not have) -> Return -> DEPLOY -> map ->
            // base -> wrong payload -> back here. That is an endless ~30s cycle - the "misclicked
            // and got stuck here for a bit". After a few attempts, stop instead of spinning.
            _autoResets++;
            if (_autoResets > 3)
            {
                Log("   wrong payload " + _autoResets + " times in a row - STOPPING rather than looping");
                AddBlackError("0x13", "payload could not be selected - stopping");
                StopAuto();
                return;
            }
            Log("   backing out with Return (attempt " + _autoResets + "/3)");
            ClickRedReturn();
            Thread.Sleep(400);
            int spent = 0;
            while (Alive(g) && spent < 2500 && !PhraseOnScreen("LOADOUT"))
            { InvalidateOcr(); Thread.Sleep(200); spent += 200; }
            // Do NOT open the LOADOUT tab to hunt for a drone selector: on these maps it just shows
            // the WEAPONS list and the flow sat there. Backing out is enough - the resume-aware AUTO
            // re-plans from the DEPLOY tab.
            Thread.Sleep(500);
        }
        catch { }
    }

    // The TEAM BASE panel stacks Deploy / Return / Deploy As Drone at the SAME x, one button apart
    // (~52px). "Deploy As Drone" is gold-on-tan and the OCR often misses it, so if the phrase is not
    // found we click just BELOW Return (or two rows below Deploy) - a known position, not a guess.
    bool ClickDeployAsDrone(int g)
    {
        try
        {
            // Only act on a CONFIRMED TEAM BASE panel - never guess on another screen.
            // CORROBORATED. This guard is what let the gold hunt run on the MAP screen: the map
            // can satisfy the bare phrase, so it went looking for the gold Deploy As Drone button
            // and found the OBJECTIVES instead - their reward amounts (150$ / 210$ / 100$) are
            // drawn in the SAME GOLD. That is the "it keeps clicking Get 14 headshots with AS Val".
            bool onBase = TeamBasePhraseUp(OcrWords());
            if (!onBase) { Log("   Deploy As Drone: not on TEAM BASE - not clicking blindly"); return false; }

            // PRIMARY: the panel's own two OPAQUE bars (green Deploy + maroon Return) give the button
            // pitch; one pitch below Return is Deploy As Drone, verified by its gold fill. No OCR.
            int pxx, pyy;
            if (PanelDeployAsDrone(out pxx, out pyy))
            {
                ClickPrimaryLogged(pxx, pyy, "Deploy As Drone");
                return true;
            }

            List<string[]> ws = OcrWords();
            List<string[]> ww = OcrWordsWhiten();
            List<Hit> hd = FindPhraseAll("Deploy", null, ws); if (hd.Count == 0) hd = FindPhraseAll("Deploy", null, ww);
            List<Hit> hdr = FindPhraseAll("Drone", null, ws); if (hdr.Count == 0) hdr = FindPhraseAll("Drone", null, ww);

            // BEST - decide by the BUTTON FILL HEX, not the name. The TEAM BASE buttons have exact,
            // saturated fills (measured off a real frame):
            //   Deploy          #4B7D33  green   (g-r >= 30, g-b >= 55)
            //   Return          #653C39  maroon
            //   Deploy As Drone #8D7136  gold    (r >= 128, g >= 98, b <= 64)
            // The map terrain around them is muddy tan (#645743..#74634A, b 65-75, r ~100), so the
            // gold test separates the real button from grass/fields where colour-names alone could
            // not. There are TWO "Deploy" labels on this panel - only one has a GOLD fill.
            foreach (Hit d in hd)
            {
                if (ButtonKindAt(d.X, d.Y) == 3)
                {
                    int cx2 = d.X + 45, cy2 = d.Y + 10;
                    Log("   Deploy As Drone (gold fill 8D7136) at (" + cx2 + "," + cy2 + ") - clicking");
                    ClickPrimaryLogged(cx2, cy2, "Deploy As Drone");
                    return true;
                }
            }

            // PRIMARY - the "Deploy + Drone" ROW PAIR. Real OCR returns the label as two tokens
            // ("Deploy" ... "Drone") because the "As" is dropped, so a phrase search for "Deploy As
            // Drone" never matches. A "Deploy" with a "Drone" to its RIGHT on the SAME row is
            // unambiguous - the green "Deploy" button ABOVE has no "Drone" beside it - so click
            // between them. (Verified on a real frame: Deploy(667,623) + Drone(759,631) -> (737,635).)
            foreach (Hit d in hd)
                foreach (Hit r in hdr)
                {
                    if (Math.Abs(r.Y - d.Y) > 30) continue;
                    int dx = r.X - d.X;
                    if (dx <= 0 || dx > 400) continue;
                    int px2 = (d.X + r.X + 25) / 2;
                    int py2 = (d.Y + r.Y) / 2 + 8;
                    Log("   Deploy As Drone (Deploy+Drone row) at (" + px2 + "," + py2 + ") - clicking");
                    ClickPrimaryLogged(px2, py2, "Deploy As Drone");
                    return true;
                }

            if (ClickPhrasePersistent("Deploy As Drone", 2500, g)) return true;

            // FALLBACK - the row below the maroon Return (only when Return was actually found; the
            // first "Deploy" token alone is the GREEN button, which is what got clicked by mistake).
            List<Hit> hr = FindPhraseAll("Return", null, ws); if (hr.Count == 0) hr = FindPhraseAll("Return", null, ww);
            if (hr.Count == 0) { Log("   Deploy As Drone: no Deploy+Drone row and no Return - not clicking"); return false; }
            Hit below = default(Hit); bool hasBelow = false;
            foreach (Hit h in hd) if (h.Y > hr[0].Y) { below = h; hasBelow = true; break; }
            int x = hasBelow ? below.X + 40 : hr[0].X;
            int y = hasBelow ? below.Y + 8 : hr[0].Y + 52;
            Log("   Deploy As Drone not readable - clicking below the buttons at (" + x + "," + y + ")");
            ClickPrimaryLogged(x, y, "Deploy As Drone");
            return true;
        }
        catch { return false; }
    }

    bool ClickRedReturn()
    {
        try
        {
            // CORROBORATE FIRST. This used to fire on the bare word "Return" from anywhere on
            // screen, so a stray tooltip, objective line or label during a LOADING transition
            // satisfied it and the flow pressed back out of the screen it was waiting for. Only
            // screens that genuinely carry a Return button are allowed to satisfy it.
            bool panelish = PhraseOnScreen("WARHEAD") || PhraseOnScreen("DEPLOY AS DRONE")
                         || PhraseOnScreen("SELECT DRONE") || PhraseOnScreen("POINT")
                         || PhraseOnScreen("TEAM BASE");   // NOT "LOADOUT": that is a permanent nav button
            if (!panelish)
            {
                Log("   saw 'Return' but no panel is up - NOT pressing back (a stray word during a transition)");
                return false;
            }
            List<Hit> h = FindPhraseAll("Return", null, OcrWords());
            if (h.Count == 0) h = FindPhraseAll("Return", null, OcrWordsWhiten());
            if (h.Count == 0) h = FindPhraseAll("Retum", null, OcrWords());
            if (h.Count == 0) return false;
            // THE PANEL REGION ONLY. "Return" was matched by the fuzzy phrase test against totally
            // unrelated UI elsewhere on screen - measured: the OCR box for FEATURED on the LOADOUT
            // screen is 1413,235 59x9, whose centre is exactly (1443,239), and the log shows
            // "click Return (1443,239)". So pressing AUTO from the loadout page clicked FEATURED.
            // The Return button only ever lives in the centred panel, so anything outside that
            // region is not it, whatever the text similarity says.
            int SWp = Screen.PrimaryScreen.Bounds.Width, SHp = Screen.PrimaryScreen.Bounds.Height;
            List<Hit> near = new List<Hit>();
            foreach (Hit hh in h)
                if (hh.X > SWp * 25 / 100 && hh.X < SWp * 78 / 100 && hh.Y > SHp * 28 / 100 && hh.Y < SHp * 82 / 100) near.Add(hh);
            if (near.Count == 0)
            {
                Log("   'Return' matched at (" + h[0].X + "," + h[0].Y + ") but that is nowhere near the panel - NOT clicking (this is what used to hit FEATURED)");
                return false;
            }
            h = near;
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
        if (TeamBasePhraseUp(ws)) return "team base";
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

        // If the panel is showing the OTHER drone's warheads the slot positions mean nothing, so
        // clicking would equip a wrong-by-position bomb (that is the "default stays / wrong bomb"
        // case). Refuse and let the flow go back to LOADOUT to fix the drone first.
        if (!BasePanelIsDrone(_drone))
        {
            Log("   TEAM BASE shows the other drone's warheads - not clicking (the loadout drone is wrong)");
            return false;
        }

        // Let the panel finish animating in. At 250 ms the green/maroon bars are sometimes still
        // fading and the solid-run test fails, which used to be read as "wrong loadout" and cost a
        // whole LOADOUT -> DEPLOY -> Base -> panel lap.
        Thread.Sleep(600);

        // NO GRID NUDGING ON RETRY. Sliding the whole grid up/down was aimed at a grid that was a
        // few px off, but because the grid is re-MEASURED on every attempt the nudge only ever moved
        // the target OFF the correct cell - it is exactly what made it "press the right bomb, then
        // misclick" onto a neighbouring warhead. Retry the SAME measured cell instead.
        for (int t = 1; t <= 4 && Alive(g); t++)
        {
            _whRowNudge = 0;
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
            // PROOF of the right selection = the green box is ON OUR SLOT. That is the definitive
            // indicator. The old test OR-ed in "the cell changed colour and looks greenish", which
            // fires over green terrain - and then accepted "label unread" as a PASS, so it reported
            // success with the WRONG warhead equipped and deployed anyway. Never again: unread is
            // NOT a pass, and a greenish cell is not proof.
            bool green = (cur2 == slot);
            if (!green && cur2 < 0)
            {
                // The green box could not be detected at all - fall back to the cell's own label,
                // and only accept a MATCH. (Not a blank read.)
                string lab = WarheadCellLabel(cells[slot].X, cells[slot].Y);
                if (lab != "" && SimPct(lab, _bomb) >= 60)
                {
                    Log("   " + _bomb + " confirmed by the cell label \"" + lab + "\" (green box not detected)");
                    return true;
                }
            }
            if (green)
            {
                Log("   " + _bomb + " confirmed (green box moved onto slot " + slot + ")");
                return true;
            }
            Log("   " + _bomb + " not confirmed (green slot now=" + cur2 + " want " + slot + " diff=" + diff + ") - retrying");
        }
        Log("   " + _bomb + " is NOT green - leaving the flow here so we do not deploy the wrong loadout");
        return false;
    }

    // FINAL PAYLOAD CHECK, run immediately before deploying. Re-measures the panel and asks: is the
    // warhead we asked for the GREEN (selected) one right now? If it is not, or the green box cannot
    // be found, we must NOT deploy - a wrong payload used to be launched on a "label unread" pass.
    bool WantedWarheadGreen()
    {
        try
        {
            if (_bomb == "(none)" || !_stepBomb) return true;      // nothing requested -> nothing to check
            int idx = System.Array.IndexOf(BombsFor(_drone), _bomb);
            if (idx <= 0) { Log("   payload check: " + _bomb + " is not a " + _drone + " warhead"); return false; }
            int slot = idx - 1;
            int count = BombsFor(_drone).Length - 1;
            if (!BasePanelIsDrone(_drone)) { Log("   payload check: the panel is not the " + _drone + " loadout"); return false; }
            List<Point> cells = FindWarheadCells(count);
            if (cells.Count <= slot) { Log("   payload check: only " + cells.Count + " cells, need slot " + slot); return false; }
            int W, H; int[] px = Grab(out W, out H);
            int cur = GreenSlot(px, W, H, cells, count);
            // A readable cell label that names a DIFFERENT warhead is a hard veto, whatever the
            // colour says - this is the "misclicked the bomb but deployed anyway" guard.
            string lab = WarheadCellLabel(cells[slot].X, cells[slot].Y);
            if (lab != "" && SimPct(lab, _bomb) < 60)
            {
                bool namesOurs = false;
                foreach (string b in BombsFor(_drone))
                {
                    if (b == "(none)" || b == _bomb) continue;
                    if (SimPct(lab, b) >= 70) { namesOurs = false; Log("   payload check: our cell reads \"" + lab + "\" = " + b + ", not " + _bomb); return false; }
                }
                if (!namesOurs) { Log("   payload check: our cell reads \"" + lab + "\" which is not " + _bomb); return false; }
            }
            if (cur == slot) return true;
            // Green undetectable -> accept ONLY a positive label match for OUR bomb in our slot.
            // NOTE: there was briefly a blanket "green AND label unreadable -> trust the click"
            // escape here. It deployed Standard Frag instead of the chosen warhead (the panel even
            // showed the Deploy As Drone button padlocked). Verification must never pass on the
            // ABSENCE of evidence - only on positive evidence.
            if (cur < 0)
            {
                if (lab != "" && SimPct(lab, _bomb) >= 60) return true;
            }
            Log("   payload check: green slot is " + (cur < 0 ? "not detected" : cur + " (" + BombsFor(_drone)[cur + 1] + ")") +
                ", want " + slot + " (" + _bomb + ")");
            return false;
        }
        catch { return false; }
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

        int ax = 0, ay = 0;
        // Use the PIXEL anchor (the gold button, derived from the green and maroon bars) whenever
        // the panel is really up. The OCR phrase centroid jitters by up to 60 px between reads and
        // the ENTIRE grid is placed relative to it, so the cells slid out from under the cursor and
        // it clicked the WRONG warhead - which is exactly what was seen. Button geometry does not
        // jitter. The OCR anchor stays as the fallback.
        // THREE TRIES. The anchor is a pixel test on solid button bars, so it should not be flaky
        // while the panel is really up - but it is measured immediately after a click, and between
        // one read and the next the panel may still be fading or the pointer still hovering a
        // button. Losing it used to drop straight through to the colour hunt, find 0 cells, and
        // back the entire flow out as a "wrong loadout".
        bool haveAnchor = false;
        for (int at = 0; at < 3 && !haveAnchor; at++)
        {
            if (at > 0) { MoveToDeployAsDrone(); InvalidateGrab(); Thread.Sleep(200); }
            haveAnchor = PanelDeployAsDrone(out ax, out ay);
            if (!haveAnchor) haveAnchor = WarheadAnchor(out ax, out ay);
        }
        if (!haveAnchor)
        {
            Log("   no 'Deploy As Drone' anchor on screen - falling back to the colour hunt");
            return FindWarheadCellsByColour(count);
        }

        int colStep = (int)(106 * sc), rowStep = (int)(52 * sc);

        // Auto-fit BOTH axes. The cells are dark boxes (the selected one is green), so slide the
        // whole grid left/right and up/down and keep the position that lands on the most
        // cell-coloured patches. This absorbs whatever the panel spacing/offset really is at this
        // resolution. The HORIZONTAL fit matters because the cards are not centred exactly under
        // the "Deploy As Drone" button - a fixed column origin clicked ~70px left of the real
        // cells, nothing turned green, and the DEFAULT warhead stayed equipped.
        int W, H; int[] px = Grab(out W, out H);
        int baseOff = 85 + _whRowNudge;
        int bestOff = baseOff, bestDx = -colStep, bestHits = -1;
        int bestCS = colStep, bestRS = rowStep, bestScore = int.MinValue;
        // Auto-fit the SPACING as well as the offset. The card pitch is NOT always 106x52 - at this
        // resolution the real cells sit ~86x46 apart (measured off a live TEAM BASE shot). With a
        // hardcoded step the fitted grid drifts ~20px per column, so the click for column 3 landed
        // between cards, nothing turned green, and the DEFAULT warhead (Standard Frag) stayed
        // equipped - which is exactly what "it skipped the bomb" looked like.
        for (int dx = -150; dx <= 70; dx += 6)
        {
            for (int off = 15; off <= 150; off += 5)
            {
                for (int cs = (int)(78 * sc); cs <= (int)(126 * sc); cs += 2)
                {
                    for (int rs = (int)(40 * sc); rs <= (int)(60 * sc); rs += 2)
                    {
                        int hits = 0;
                        for (int r = 0; r < 2; r++)
                            for (int c = 0; c < 3; c++)
                            {
                                if (r * 3 + c >= count) continue;
                                int cx = ax + dx + c * cs;
                                int cy = ay + (int)(off * sc) + r * rs;
                                if (LooksLikeCell(px, W, H, cx, cy)) hits++;
                            }
                        // Most cells wins; between equal fits prefer the spacing closest to the
                        // nominal 106x52 and the offset closest to the sane default, so a bogus
                        // spacing that happens to clip several dark patches does not win.
                        int score = hits * 1000
                            - Math.Abs(cs - (int)(106 * sc)) - Math.Abs(rs - (int)(52 * sc))
                            - Math.Abs(off - baseOff) / 2;
                        if (score > bestScore)
                        { bestScore = score; bestHits = hits; bestOff = off; bestDx = dx; bestCS = cs; bestRS = rs; }
                    }
                }
            }
        }
        colStep = bestCS; rowStep = bestRS;
        int col0 = ax + bestDx;
        int row0 = ay + (int)(bestOff * sc);

        List<Point> grid = new List<Point>();
        for (int r = 0; grid.Count < count && r < 8; r++)
            for (int c = 0; c < 3 && grid.Count < count; c++)
                grid.Add(new Point(col0 + c * colStep, row0 + r * rowStep));
        Log("   warhead grid under Deploy As Drone (" + ax + "," + ay + "): fitted dx " + bestDx +
            " offset " + bestOff + " (cell hits " + bestHits + "/" + count + "), col0 " + col0 +
            ", step " + colStep + "x" + rowStep + (bestHits < count ? "  (only " + bestHits + " of " + count + " cells matched - the panel may be mid-scroll)" : ""));
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
        // The button sits INSIDE the panel, which is vertically centred. A hit up in the top strip
        // is nav-bar or objective text ("DEPLOY", "...with AS Val") that the loose fallbacks above
        // can match - and anchoring there fits the warhead grid to empty map and clicks nothing.
        int Hs = Screen.PrimaryScreen.Bounds.Height;
        int best = -1;
        for (int i = 0; i < h.Count; i++)
        {
            if (h[i].Y < Hs * 20 / 100) continue;
            if (best < 0 || h[i].Y > h[best].Y) best = i;   // lowest = the button
        }
        if (best < 0) return false;
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
        // Wide, dense sampling. A sparse 3x3 missed the green when the ICON and the label text sat
        // on the sampled points (the selected cell read "none" while clearly green on screen, which
        // then hard-vetoed the deploy). Scan the whole cell face and take the fraction.
        int green = 0, n = 0;
        for (int dy = -22; dy <= 22; dy += 4)
            for (int dx = -44; dx <= 44; dx += 4)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                int v = px[y * W + x];
                int b = v & 0xFF, g = (v >> 8) & 0xFF, r = (v >> 16) & 0xFF;
                if ((g - Math.Max(r, b)) >= 22 && g >= 80) green++;
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
        // Loosened: a real green cell scores ~0.6+ here, while the runner-up is background. 0.55/0.25
        // was too strict and reported "none" on a genuinely selected cell.
        if (bi >= 0 && best >= 0.30f && (best - second) >= 0.10f) return bi;
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
    static int[] _grabBuf = null;                     // persistent full-screen pixel buffer (reused)
    static Bitmap _grabBmp = null;                     // persistent capture bitmap (reused)
    static Graphics _grabG = null;                     // its Graphics (reused)
    static int _grabW = 0, _grabH = 0;
    static DateTime _grabTime = DateTime.MinValue;

    static int[] Grab(out int w, out int h)
    {
        // the blob search and the loading-screen check both grab the whole screen and
        // usually run back to back - reuse a capture that is at most 250ms old
        if (_grabCache != null && (DateTime.Now - _grabTime).TotalMilliseconds < 45)
        {
            w = _grabW; h = _grabH;
            return _grabCache;
        }

        Rectangle b = Screen.PrimaryScreen.Bounds;
        w = b.Width; h = b.Height;
        // ONE persistent buffer + bitmap, refilled each grab. Allocating an 8.3MB int[] (and a bitmap)
        // on EVERY capture put ~180MB/s of large-object-heap garbage through the GC and eventually
        // threw OutOfMemory - which WinForms paints as the red-X-on-white screen. Never allocate the
        // whole-screen capture repeatedly; reuse it. (Valid only until the next Grab - every caller
        // uses it immediately.)
        if (_grabBuf == null || _grabBuf.Length != w * h || _grabBmp == null || _grabBmp.Width != w || _grabBmp.Height != h)
        {
            if (_grabG != null) { try { _grabG.Dispose(); } catch { } _grabG = null; }
            if (_grabBmp != null) { try { _grabBmp.Dispose(); } catch { } _grabBmp = null; }
            _grabBuf = new int[w * h];
            _grabBmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _grabG = Graphics.FromImage(_grabBmp);
        }
        _grabG.CopyFromScreen(b.X, b.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        System.Drawing.Imaging.BitmapData bd = _grabBmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(bd.Scan0, _grabBuf, 0, _grabBuf.Length);
        _grabBmp.UnlockBits(bd);
        _grabCache = _grabBuf; _grabW = w; _grabH = h; _grabTime = DateTime.Now;
        return _grabBuf;
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
        // A few px of jitter so two calls never land on the exact same pixel: the game ignores the
        // wheel unless it has seen real pointer movement first.
        int x = cx + sx * (40 + (step * 37) % 160) + _rng.Next(-6, 7);
        int y = cy + sy * (30 + (step * 23) % 120) + _rng.Next(-6, 7);
        // NEVER park on our own panel - the wheel goes to the window under the cursor, so a scroll
        // aimed at the game would be swallowed by us. The old nudge could still land inside it;
        // try real quadrant points instead.
        if (Bounds.Contains(x, y))
        {
            int[] px = { pb.Width / 4, pb.Width * 3 / 4, pb.Width / 4, pb.Width * 3 / 4 };
            int[] py = { pb.Height * 3 / 4, pb.Height * 3 / 4, pb.Height / 4, pb.Height / 4 };
            for (int i = 0; i < px.Length; i++)
                if (!Bounds.Contains(px[i], py[i])) { x = px[i]; y = py[i]; break; }
        }
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

    // Measured on the real map (1920x1080, before/after screen diff over 50%): wheel data +120 is
    // wheel-up and the game reads it as zoom OUT. The old labels were the other way round, so
    // "zooming in (z/10)" was actually pulling the map back. Signs are honest now.
    static void ScrollOut(int notches)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = IN_MOUSE;
        inp[0].U.mi.data = (uint)(120 * notches);
        inp[0].U.mi.flags = MV_WHEEL;
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }

    // The map prints the Base label in RED while every POINT label is white, so when OCR cannot
    // read "Base" the red text is still a unique marker - find it from the pixels. Solid red blobs
    // (the base flag / red terrain) are rejected by the height test, which only keeps runs the size
    // of a text label.
    bool FindRedMapLabel(out int bx, out int by)
    {
        bx = -1; by = -1;
        try
        {
            int W, H; int[] q = Grab(out W, out H);
            if (q == null) return false;
            int y0 = H * 12 / 100, y1 = H * 92 / 100;
            // How SOLID is this row's red? Red TEXT has gaps between the letters (solidity ~0.3-0.6);
            // the solid red flag and the half-red CAPTURING pin are ~1.0. Only text-like rows count,
            // so the pin and the flag are ignored and cannot be mistaken for the label. (A map can
            // show a red capture pin AND the red Base label at once - that was the confusion.)
            bool[] textRow = new bool[H];
            int[] rowN = new int[H], rowMinX = new int[H], rowMaxX = new int[H];
            for (int y = y0; y < y1; y++)
            {
                int cnt = 0, mn = W, mx = -1;
                for (int x = 4; x < W - 4; x += 2)
                {
                    int v = q[y * W + x];
                    int r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
                    // The BASE red is the BRIGHT one. Measured on a real map (1919x1078): the base
                    // marker is #ED2224 (R 237, 3131 px) while a "full/contested" POINT pin is
                    // #D51B1E (R 213, 2308 px). Requiring R >= 224 keeps the base and drops the
                    // pin, so a red capture pin can never be mistaken for the Base.
                    if (r >= 224 && r - g >= 55 && r - b >= 50) { cnt++; if (x < mn) mn = x; if (x > mx) mx = x; }
                }
                int wid = mx >= 0 ? (mx - mn) / 2 + 1 : 0;
                float solidity = wid > 0 ? (float)cnt / wid : 1f;
                rowN[y] = cnt; rowMinX[y] = mn; rowMaxX[y] = mx;
                textRow[y] = (cnt >= 3 && solidity <= 0.72f);
            }
            int bestCnt = 0, bestX = -1, bestY = -1;
            int ry = y0;
            while (ry < y1)
            {
                if (!textRow[ry]) { ry++; continue; }
                int ys = ry, ye = ry, cnt = 0, mnx = W, mxx = -1;
                while (ry < y1 && textRow[ry])
                {
                    cnt += rowN[ry];
                    if (rowMinX[ry] < mnx) mnx = rowMinX[ry];
                    if (rowMaxX[ry] > mxx) mxx = rowMaxX[ry];
                    ye = ry; ry++;
                }
                int hgt = ye - ys + 1, wid = mxx - mnx + 1;
                if (hgt <= 45 && wid >= 24 && wid <= 340 && cnt > bestCnt)
                { bestCnt = cnt; bestX = (mnx + mxx) / 2; bestY = (ys + ye) / 2; }
            }
            if (bestCnt < 25) return false;
            bx = bestX; by = bestY;
            return true;
        }
        catch { }
        return false;
    }

    // wheel data -120 is wheel-down = zoom IN (measured, see ScrollOut).
    static void ScrollIn(int notches)
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
            int SW = Screen.PrimaryScreen.Bounds.Width, SH = Screen.PrimaryScreen.Bounds.Height;
            // The blob is searched for across the WHOLE screen, so anything else that colour -
            // a banner, an objective, a team icon that is already selected - could win and the
            // click would land on nothing (which is how a misclick showed up as pressing
            // something unrelated). The team cards live in the middle, so reject anything
            // outside that band and log where it was found.
            if (FindBlob(delegate (int rr, int gg, int bb)
            {
                return blue ? (bb > 110 && bb > rr + 40 && bb > gg + 40)
                            : (rr > 110 && rr > gg + 50 && rr > bb + 50);
            }, 200, out tx, out ty)
                && tx > SW * 15 / 100 && tx < SW * 85 / 100
                && ty > SH * 15 / 100 && ty < SH * 85 / 100)
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
            // RECONNECT COOLDOWN: just relaunched, nothing here is a drone yet. Wrap-safe compare.
            if (unchecked(Environment.TickCount - _hudCooldownUntil) < 0) return;
            if (!RobloxFocused()) return;    // tabbed away - do not read another app's screen
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
    int _hudCooldownUntil = 0;   // no drone-view scan until this TickCount (set on reconnect)
    int _autoResets = 0;         // wrong-payload back-outs in a row (loop breaker)
    string _detDbg = "";
    bool DroneViewOnScreen()
    {
        // Big, high-contrast OSD text is far easier for the OCR than the small top-right badge:
        //  * the game's own FLIGHT mm:ss clock (top-left) - only exists in the drone view
        //  * the top-right RC LIVE / LINK LIVE badge
        // Any one of them means we are in a drone. The loadout / menus have neither.
        // A MENU is never a drone view. Our own name (printed on every menu) and PLAYERS are menu-only
        // tells, so if either is on screen refuse immediately - even if some OSD-ish word also matched.
        // That stops the HUD false-flagging on the profile strip / team-select / map panels.
        string menuWhy;
        if (MenuOnScreen(out menuWhy)) { _detDbg = "menu on screen: " + menuWhy; return false; }
        bool flight = ReadFlightSecs() >= 0;
        bool linked = !flight && CornerLinked();
        // White OSD text over a bright/hazy sky is low-contrast and the OCR often drops the FLY
        // clock and the LINK LIVE badge. MOUNTED (bottom-right, drone-only) and AGL (top-centre)
        // survive far more often, so use them as a third tell.
        bool kw = !flight && !linked && DroneKeyword();
        _detDbg = "flightClock=" + (flight ? "yes" : "no") + "  rcL live=" + (linked ? "yes" : "no") + "  kw=" + (kw ? "yes" : "no");
        return flight || linked || kw;
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
            OverlayHub.I.SetHud(false, "", "", "", 0f, 0f, false, 0f, 0f);
            if (_hudForm != null) StopHud();
            return;
        }
        long now = Environment.TickCount;
        if (_hudPrevTick == 0) { _hudPrevTick = now; return; }
        float dt = (now - _hudPrevTick) / 1000f;
        _hudPrevTick = now;
        if (dt <= 0f) return;
        if (dt > 0.1f) dt = 0.1f;

        // ROLL = the RIGHT stick X ONLY. The LEFT stick X is YAW (turning) and must NOT tilt the
        // ladder - moving the left stick right/left should not affect the horizon at all.
        float sx = _padRx;
        float sy = _padRy;
        // Prefer the stick values the joystick app feeds us (it reads the real controller) when our
        // own XInput read is missing or weaker - that was the "stick reads 0" problem.
        float appLx, appLy, appRx, appRy;
        if (OverlayHub.I.AppPad(out appLx, out appLy, out appRx, out appRy))
        {
            if (Math.Abs(appRx) > Math.Abs(sx)) sx = appRx;
            if (Math.Abs(appRy) > Math.Abs(sy)) sy = appRy;
        }
        if (sx > -0.05f && sx < 0.05f) sx = 0f;
        if (sy > -0.05f && sy < 0.05f) sy = 0f;

        // The DETECTED horizon (gradient detector) is the reference: the ladder SMOOTHLY TRAVELS TO IT,
        // so it reads flat when the ground line is flat and diagonal when the ground line tilts. When
        // there is no confident fix we fall back to integrating the controller so it still moves.
        // --- Complementary filter, exactly like a real drone IMU ---
        // GYRO = the controller: fast, integrates the stick rate, and because it is ACRO it HOLDS.
        // ACCELERATION (expo) - the harder you push, the faster it moves. Tuned separately for pitch
        // (right stick Y, up/down) and tilt (right stick X, bank) via their own dials.
        float syS = Shape(FineGain(sy, _hudFineP), _hudAccPitch);
        // Tilt: NO fine-gain boost (it made the bank accelerate in as you pushed) - just the expo dial,
        // so the roll stays a steady acro rate and HOLDS the bank when you centre.
        float sxS = Shape(sx, _hudAccRoll);
        _lastSxS = sxS; _lastSyS = syS;                   // for the physics log
        bool det = _hudDetValid && (now - _hudDetAt) < 2000;
        // ---- PROPER FUSION: scalar Kalman/alpha-beta per axis, in ANGLE space (see HudFuse) ----
        // Stick = control input, vision = measurement, coast when the vision drops out.
        // The old second (pixel-space) fusion, and the "bad lift" knob that only it used,
        // are gone: one estimator, one set of numbers, and no dial that silently does nothing.
        HudFuse(dt, sxS, syS, now);

        // The LEFT stick (throttle) is a small, BOUNDED proportional nudge, NOT integrated. Adding
        // it to the rate meant holding throttle walked the horizon clean off the screen.
        float leftPitch = -_padLy * _hudLeftPx;
        // RIGHT stick Y as a DIRECT horizon lift: push FORWARD -> the line RAISES (smaller px = up),
        // pull BACK -> it drops. Bounded (not integrated) so holding the stick holds the offset and
        // releasing returns it - the pilot's pitch input visibly drives the horizon.
        // SMOOTHED so the line glides to the new height instead of snapping when the stick moves.
        // EXPO (acceleration): more % input = faster travel. A light touch barely moves the line, a full
        // stick sends it at full speed - a quadratic curve on the stick magnitude (with a linear floor
        // so small inputs still do something). The glide below turns "further target" into "faster move".
        float stickPitchTarget = -syS * _hudPitStick;
        float pv = 1f - (float)Math.Pow(0.5, dt / 0.22f);      // ~0.22s glide
        _hudPitStickSm += (stickPitchTarget - _hudPitStickSm) * pv;
        float stickPitch = _hudPitStickSm;
        // RIGHT stick X as a DIRECT bank, the same way the pitch gets a direct lift - without it the
        // roll only had the slow gyro rate, so fine tilt input lagged (why pitch felt right and tilt
        // did not). Bounded + glided, so holding the stick holds the bank and releasing returns it.
        float stickRollTarget = -sxS * _hudRollStick;
        _hudRollStickSm += (stickRollTarget - _hudRollStickSm) * pv;
        float stickRoll = _hudRollStickSm;

        if (_hudFRoll > 180f) _hudFRoll = 180f;
        if (_hudFRoll < -180f) _hudFRoll = -180f;
        if (_hudFPitch > 1600f) _hudFPitch = 1600f;  // lots of travel so the horizon can leave the frame
        if (_hudFPitch < -1600f) _hudFPitch = -1600f; // (pointed at the ground -> it runs off the top)

        _hudRoll = _hudFRoll + _hudRollOff + stickRoll;     // + manual roll offset + direct stick bank
        // low-quality LIFT (up) and clutter DROP (down) - both in px, off the dials
        float treeDrop = _hudClutter * _hudTreeDrop * _hudDpp;   // trees/structures -> push the line DOWN
        _hudPitch = _hudFPitch + leftPitch + stickPitch + _hudPitOff + treeDrop;
        if (_physLog) PhysLogLine();                       // 50 Hz: fast stick work included

        // Simulated FPV pack voltage. It starts at half, rises with throttle (left stick Y up), and
        // a spring + ripple gives the sag/bounce of a real pack under load. 4S range 13.2V..16.8V.
        // LEFT STICK is the throttle, and now BOTH ends are used: up = full pack, DOWN = sagged to 12V.
        float thr = _padLy;                                  // signed: -1 (down) .. +1 (up)
        float vTarget = 0.5f + 0.5f * thr;                   // down -> 0 (12.0V), up -> 1 (16.8V)
        _hudVVel += ((vTarget - _hudVBat) * 26f - _hudVVel * 7f) * dt;   // underdamped -> it bounces
        _hudVBat += _hudVVel * dt;
        if (_hudVBat < 0.02f) { _hudVBat = 0.02f; _hudVVel = 0f; }       // reach ~12.0V at the bottom
        if (_hudVBat > 1f) { _hudVBat = 1f; _hudVVel = 0f; }
        // ripple is biggest at the ENDS of the stick (like the sag/bounce you liked at full throttle),
        // so the reading is jumpy both when held up and when held down
        float ripple = 0.018f * (float)Math.Sin(now * 0.021) * (0.30f + 0.70f * Math.Abs(thr));
        float chg = _hudVBat + ripple;
        if (chg < 0.02f) chg = 0.02f;
        if (chg > 1f) chg = 1f;
        _hudV1 = 12.0f + chg * 8.0f;             // 12.0 .. 20.0V - wide range so it visibly jumps
        _hudV2 = 12.0f + (chg * 0.992f) * 8.0f;  // second pack reads a hair lower

        string alt = _hudAgl != "" ? _hudAgl + " m" : (_hudAlt != "" ? _hudAlt + " m" : "");
        OverlayHub.I.SetHud(true, _hudHdg, _hudSpd != "" ? _hudSpd + " m/s" : "", alt, _hudRoll, _hudPitch, _hudStyleUav, _hudV1, _hudV2);
        OverlayHub.I.SetDials(_hudDpp, _hudShear, _hudLen, _hudRungTilt, _hudMaxTilt, _hudSpreadAmt, _hudSpreadAccel);

        if (_hudForm == null) EnsureHud();          // UI thread - safe to create here
        if (_hudForm != null)
        {
            _hudForm.Home = _hudHome;
            _hudForm.Spd = _hudSpd;
            _hudForm.Agl = alt;
            _hudForm.Hdg = _hudHdg;
            _hudForm.Roll = _hudRoll;
            _hudForm.PitchPx = _hudPitch;
            _hudForm.Dpp = _hudDpp; _hudForm.Shear = _hudShear; _hudForm.Len = _hudLen; _hudForm.RungTilt = _hudRungTilt; _hudForm.MaxTilt = _hudMaxTilt; _hudForm.SpreadAmt = _hudSpreadAmt; _hudForm.SpreadAccel = _hudSpreadAccel;
            _hudForm.Uav = _hudStyleUav;
            _hudForm.V1 = _hudV1; _hudForm.V2 = _hudV2;
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
        // COMBINE every connected XInput pad instead of using only the first one. The joystick app
        // creates a VIRTUAL pad that it centres while you fly FPV (pause_on_fpv), so if the tool
        // read that pad the sticks looked dead - no roll/pitch reached the HUD and the ladder never
        // tilted. Taking the largest |value| across all pads means it always follows the pad you
        // are actually moving, whichever XInput index it lands on.
        // Use ints for the running max: Math.Abs(short) THROWS on short.MinValue (-32768), which
        // some pads report at full deflection - that was the "negating the minimum value of a
        // two's complement number" crash dialog. int Math.Abs cannot overflow here.
        ushort buttons = 0; bool conn = false;
        int lx = 0, ly = 0, rx = 0, ry = 0;
        for (int i = 0; i < 4; i++)
        {
            XINPUT_STATE st;
            if (TryPad(i, out st))
            {
                conn = true;
                buttons |= st.Gamepad.wButtons;
                if (Math.Abs((int)st.Gamepad.lx) > Math.Abs(lx)) lx = st.Gamepad.lx;
                if (Math.Abs((int)st.Gamepad.ly) > Math.Abs(ly)) ly = st.Gamepad.ly;
                if (Math.Abs((int)st.Gamepad.rx) > Math.Abs(rx)) rx = st.Gamepad.rx;
                if (Math.Abs((int)st.Gamepad.ry) > Math.Abs(ry)) ry = st.Gamepad.ry;
            }
        }
        if (conn)
        {
            _padLx = NormStick((short)lx); _padLy = NormStick((short)ly);
            _padRx = NormStick((short)rx); _padRy = NormStick((short)ry);
            try { OverlayHub.I.SetPad(_padLx, _padLy, _padRx, _padRy); } catch { }
        }

        if (!conn)
        {
            lblPadStatus.Text = "no controller";
            _padRejoinWasDown = _padAutoWasDown = _padSwapWasDown = false;
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

        if (_padSwapOn)
        {
            ushort m = PAD.ContainsKey(_padSwap) ? PAD[_padSwap] : (ushort)0;
            bool d = m != 0 && (buttons & m) != 0;
            if (d && !_padSwapWasDown)
            {
                Log("controller " + _padSwap + " pressed -> swap setup");
                SwapFav();
            }
            _padSwapWasDown = d;
        }
    }

    // ================= hotkeys =================
    void RegisterHotkeys()
    {
        UnregisterHotKey(Handle, HK_REJOIN);
        UnregisterHotKey(Handle, HK_AUTO);
        UnregisterHotKey(Handle, HK_NIGHT);
        UnregisterHotKey(Handle, HK_STOP);
        UnregisterHotKey(Handle, HK_INSTANT);
        UnregisterHotKey(Handle, HK_SWAP);
        UnregisterHotKey(Handle, HK_REF);
        // F7 = SWAP the saved MAVIC / FPV setup
        if (!RegisterHotKey(Handle, HK_SWAP, 0, 0x76))
            Log("WARNING: could not register F7 for setup swap - another app already has it.");
        // F6 = STOP, so the flight stick can stop the flow (hotkey fires from any focus)
        if (!RegisterHotKey(Handle, HK_STOP, 0, 0x75))
            Log("WARNING: could not register F6 for STOP - another app already has it.");
        // F9 = INSTANT RECONNECT - straight relaunch into the same server, NO "LAND NOW" text.
        if (!RegisterHotKey(Handle, HK_INSTANT, 0, 0x78))
            Log("WARNING: could not register F9 for instant reconnect - another app already has it.");
        if (!RegisterHotKey(Handle, HK_REJOIN, 0, _hkRejoinKey))
            Log("WARNING: could not register " + ((Keys)_hkRejoinKey) + " - another app already has it. Use 'Set key' to choose another.");
        if (!RegisterHotKey(Handle, HK_AUTO, 0, _hkAutoKey))
            Log("WARNING: could not register F5 - another app already has it.");
        // F4 = CAPTURE REF - grab the current view as a labelled hudref photo (good-*.png + .hzn).
        if (!RegisterHotKey(Handle, HK_REF, 0, 0x73))
            Log("WARNING: could not register F4 for capture ref - another app already has it.");
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
            else if (id == HK_REF) { Log("hotkey F4 -> capture ref"); SaveRef(); }
            else if (id == HK_INSTANT) { Log("hotkey F9 -> instant reconnect (no LAND NOW)"); Rejoin("hotkey instant", false); }
            else if (id == HK_SWAP) { Log("hotkey F7 -> swap setup"); SwapFav(); }
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
        Log("ready.  " + ((Keys)_hkRejoinKey) + " = rejoin (LAND NOW),  F9 = instant reconnect (no LAND NOW),  F7 = swap MAVIC/FPV setup,  F5 = AUTO RUN,  " + ((Keys)_hkNightKey) + " = night vision.");
        UpdateFavLabel();
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
        UnregisterHotKey(Handle, HK_INSTANT);
        UnregisterHotKey(Handle, HK_SWAP);
        UnregisterHotKey(Handle, HK_REF);
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

    // ---- favourite setups ----
    void UpdateFavLabel()
    {
        if (lblActiveFav == null) return;
        lblActiveFav.Text = "active: " + _activeFav + "  (MAVIC: " + _favMDrone + "/" + _favMBomb +
                            "   FPV: " + _favFDrone + "/" + _favFBomb + ")";
    }

    void SaveFav(string which)
    {
        if (which == "MAVIC") { _favMTeam = _team; _favMDrone = _drone; _favMBomb = _bomb; _favMAsDrone = _asDrone; }
        else { _favFTeam = _team; _favFDrone = _drone; _favFBomb = _bomb; _favFAsDrone = _asDrone; }
        _activeFav = which;
        UpdateFavLabel();
        SaveCfg();
        Log("saved " + which + " setup   team " + _team + " | drone " + _drone + " | bomb " + _bomb +
            " | as drone " + _asDrone);
    }

    void SwapFav() { ApplyFav(_activeFav == "MAVIC" ? "FPV" : "MAVIC"); }

    void ApplyFav(string which)
    {
        if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { ApplyFav(which); }); return; }
        _activeFav = which;
        if (which == "MAVIC") { _team = _favMTeam; _drone = _favMDrone; _bomb = _favMBomb; _asDrone = _favMAsDrone; }
        else { _team = _favFTeam; _drone = _favFDrone; _bomb = _favFBomb; _asDrone = _favFAsDrone; }
        _hudStyleUav = _drone == "MAVIC";
        // sync the dropdowns so the panel shows what is actually live
        if (cmbTeam != null && cmbTeam.Items.Contains(_team)) cmbTeam.SelectedItem = _team;
        if (cmbDrone != null && cmbDrone.Items.Contains(_drone)) cmbDrone.SelectedItem = _drone;
        FillBombs();
        if (cmbBomb != null && cmbBomb.Items.Contains(_bomb)) cmbBomb.SelectedItem = _bomb;
        if (chkAsDrone != null) chkAsDrone.Checked = _asDrone;
        if (_hudForm != null) _hudForm.Uav = _hudStyleUav;
        OverlayHub.I.SetMission(_team, _drone, _bomb);
        UpdateFavLabel();
        Log("setup -> " + which + "   team " + _team + " | drone " + _drone + " | bomb " + _bomb);
        AddBlackLine("loadout profile: " + which, "OK");
        SaveCfg();
        // NOTE: deliberately NOT AutoRun() - swapping only changes the selection. But do make sure
        // the GAME is actually on the chosen drone if the LOADOUT screen happens to be open.
        SyncDroneOnMenu(which);
    }

    // Swapping only changed our panel before, so the game could still be on the other drone. If the
    // LOADOUT screen is up, click its chevron to match - same verified logic as the flow's step 3.
    // Never runs AUTO and never clicks blind (an unreadable loadout is left untouched).
    void SyncDroneOnMenu(string which)
    {
        if (_running) { Log("swap: AUTO is running - it will use " + _drone); return; }
        if (!(_drone == "MAVIC" || _drone == "FPV")) return;
        int g = _autoGen;
        Thread t = new Thread(delegate ()
        {
            try
            {
                Thread.Sleep(200);
                InvalidateOcr();
                string st = ScreenName();

                // TEAM BASE: the warhead cards belong to whichever drone is equipped. If they are
                // the OTHER drone's, the only fix is Return -> map -> LOADOUT, so bail out to there.
                if (st == "team base")
                {
                    if (BasePanelIsDrone(_drone))
                    {
                        Log("swap: TEAM BASE already shows the " + _drone + " warheads");
                        return;
                    }
                    Log("swap: TEAM BASE has the other drone's warheads - clicking Return, then LOADOUT");
                    ClickRedReturn();
                    Thread.Sleep(900);
                    InvalidateOcr();
                    st = ScreenName();
                }

                if (st != "loadout" && (st == "map" || st == "lobby"))
                {
                    Log("swap: opening LOADOUT to set the drone (" + st + ")");
                    ClickPhraseVerified("LOADOUT", g);
                    WaitPhrase("SELECT DRONE", 6000, g);
                    InvalidateOcr();
                    st = ScreenName();
                }
                if (st != "loadout")
                {
                    Log("swap: " + which + " set - the drone can't be changed from here (" + st + "); run AUTO to apply it");
                    return;
                }
                Log("swap: checking the LOADOUT drone - want " + _drone + "...");
                if (DroneIs(_drone)) { Log("   " + _drone + " already selected"); return; }
                if (DroneIs("MAVIC") || DroneIs("FPV"))
                {
                    bool wantMavic = _drone == "MAVIC";
                    ClickDroneArrow(wantMavic, g);       // right = MAVIC, left = FPV
                    Thread.Sleep(350);
                    InvalidateOcr();
                    Log(DroneIs(_drone) ? "   " + _drone + " selected" : "   could not confirm " + _drone);
                }
                else Log("   could not read the current drone - leaving the loadout untouched");
                if (PhraseOnScreen("Return")) ClickRedReturn();
            }
            catch (Exception ex) { Log("swap drone check failed: " + ex.Message); }
        });
        t.IsBackground = true;
        t.Start();
    }

    // Which drone's warhead cards are on the TEAM BASE panel? The two sets use distinct words -
    // MAVIC has RGO/M67 racks, FPV has PG-7/TBG/Light Rocket/Standard Frag - so seeing only the
    // other set means the panel belongs to the wrong drone. Ambiguous -> true (never force Return).
    bool BasePanelIsDrone(string drone)
    {
        bool mav = PhraseOnScreen("RGO") || PhraseOnScreen("M67");
        bool fpv = PhraseOnScreen("PG-7") || PhraseOnScreen("TBG") || PhraseOnScreen("Light Rocket") || PhraseOnScreen("Standard Frag");
        if (mav && !fpv) return drone == "MAVIC";
        if (fpv && !mav) return drone == "FPV";
        return true;
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
            // A match floor below 70 makes words that are NOT on screen match anyway: at 50%
            // "cash"==="base", "FEATURED"==="return" and "CHANGETEAM"==="TEAMBASE". That one
            // number caused the Return click on FEATURED, the phantom TEAM BASE panel and the
            // Base click on "500 Cash". Clamp it so it can never be poisoned again.
            else if (k == "matchPct") { _matchPct = int.Parse(v); if (_matchPct < 70) _matchPct = 70; }
            else if (k == "userName") _userName = v;
            else if (k == "favMTeam") _favMTeam = v;
            else if (k == "favMDrone") _favMDrone = v;
            else if (k == "favMBomb") _favMBomb = v;
            else if (k == "favMAsDrone") _favMAsDrone = v == "1";
            else if (k == "favFTeam") _favFTeam = v;
            else if (k == "favFDrone") _favFDrone = v;
            else if (k == "favFBomb") _favFBomb = v;
            else if (k == "favFAsDrone") _favFAsDrone = v == "1";
            else if (k == "activeFav") _activeFav = v;
            else if (k == "padSwap") _padSwap = v;
            else if (k == "padSwapOn") _padSwapOn = v == "1";
                else if (k == "hoverMs") { _hoverBase = int.Parse(v); _hoverMs = _hoverBase; }
                else if (k == "autoAfterRejoin") _autoAfterRejoin = v == "1";
                else if (k == "autoAfterRejoinMs") _autoAfterRejoinMs = int.Parse(v);
                else if (k == "preRejoinMs") _preRejoinMs = int.Parse(v);
                else if (k == "watchHome") _watchHome = v == "1";
                else if (k == "hudAutoDetect") _hudAutoDetect = v == "1";
            else if (k == "hud") _hudOn = v == "1";
            else if (k == "hudPitch") _hudPitchRate = ParseF(v);
            else if (k == "hudRoll") _hudRollRate = ParseF(v);
            else if (k == "hudBias") _hudBias = ParseF(v);
            else if (k == "hudAccel") _hudAccelTau = ParseF(v);
            else if (k == "hudDpp") _hudDpp = ParseF(v);
            else if (k == "hudShear") _hudShear = ParseF(v);
            else if (k == "hudLen") _hudLen = ParseF(v);
            else if (k == "hudRungTilt") _hudRungTilt = ParseF(v);
            else if (k == "hudMaxTilt") _hudMaxTilt = ParseF(v);
            else if (k == "hudSpread") _hudSpreadAmt = ParseF(v);
            else if (k == "hudSpreadAccel") _hudSpreadAccel = ParseF(v);
            else if (k == "hudTreeDrop") _hudTreeDrop = ParseF(v);
            else if (k == "hudMinSpread") _hudMinSpread = ParseF(v);
            else if (k == "hudRollOff") _hudRollOff = ParseF(v);
            else if (k == "hudPitOff") _hudPitOff = ParseF(v);
            else if (k == "hudThr") _hudLeftPx = ParseF(v);
            else if (k == "hudPStick") _hudPitStick = ParseF(v);
            else if (k == "hudTexW") _hudTexW = ParseF(v);
            else if (k == "hudAccPitch") _hudAccPitch = ParseF(v);
            else if (k == "hudAccRoll") _hudAccRoll = ParseF(v);
            else if (k == "hudFineP") _hudFineP = ParseF(v);
            else if (k == "hudRollStick") _hudRollStick = ParseF(v);
            else if (k == "hudCap") _hudCapturable = v == "1";
            else if (k == "hudFov") _hudFovY = ParseF(v);
            else if (k == "hudManeuver") _hudManeuver = ParseF(v);
            else if (k == "hudRef") _refDist = ParseF(v) / 100f;
            else if (k == "hudAxisW") _hudAxisW = ParseF(v);
            else if (k == "hudMsTau") _hudMsTau = ParseF(v);
            else if (k == "hudSkyMin") _hudSkyMin = ParseF(v) / 100f;
            else if (k == "hudSkyTex") { _hudSkyTex = ParseF(v); if (_hudSkyTex < 0f) _hudSkyTex = 0f; if (_hudSkyTex > 60f) _hudSkyTex = 60f; }
            else if (k == "physLog") _physLog = v != "0";
            else if (k == "hudSettleMs") { _hudSettleMs = (int)ParseF(v); if (_hudSettleMs < 0) _hudSettleMs = 0; if (_hudSettleMs > 60000) _hudSettleMs = 60000; }
            else if (k == "hudColdMs") { _hudColdMs = (int)ParseF(v); if (_hudColdMs < 0) _hudColdMs = 0; if (_hudColdMs > 60000) _hudColdMs = 60000; }
            else if (k == "uav") _hudStyleUav = v == "1";
            else if (k == "night") _nightVision = v == "1";
            else if (k == "nightKey") { try { _hkNightKey = (uint)int.Parse(v); } catch { } }
                else if (k == "blackScreen") _blackScreen = v == "1";
                else if (k == "lang") _lang = v;
            }
            _clickVk = ParseVk(_clickKeyText);
        }
        catch { }

        // SAFETY: the joystick TRIGGER reports as Y (and R3), so a STOP bound to Y fires on every
        // shot / bomb and aborts AUTO the moment you try to do anything. Refuse that binding no
        // matter what settings.ini says - it is never what you want. (Same trap that made STOP
        // fire on every bomb before.)
        if (_padStop == "Y") { _padStop = "off"; _padStopOn = false; }
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
                "userName=" + _userName,
                "favMTeam=" + _favMTeam, "favMDrone=" + _favMDrone, "favMBomb=" + _favMBomb,
                "favMAsDrone=" + (_favMAsDrone ? "1" : "0"),
                "favFTeam=" + _favFTeam, "favFDrone=" + _favFDrone, "favFBomb=" + _favFBomb,
                "favFAsDrone=" + (_favFAsDrone ? "1" : "0"),
                "activeFav=" + _activeFav,
                "padSwap=" + _padSwap, "padSwapOn=" + (_padSwapOn ? "1" : "0"),
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
            "hudBias=" + _hudBias.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudAccel=" + _hudAccelTau.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudDpp=" + _hudDpp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudShear=" + _hudShear.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudLen=" + _hudLen.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudRungTilt=" + _hudRungTilt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudMaxTilt=" + _hudMaxTilt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudSpread=" + _hudSpreadAmt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudSpreadAccel=" + _hudSpreadAccel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudTreeDrop=" + _hudTreeDrop.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudMinSpread=" + _hudMinSpread.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudFov=" + _hudFovY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudManeuver=" + _hudManeuver.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudRef=" + (_refDist * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudAxisW=" + _hudAxisW.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudMsTau=" + _hudMsTau.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudSkyMin=" + (_hudSkyMin * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudSkyTex=" + _hudSkyTex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "physLog=" + (_physLog ? "1" : "0"),
            "hudSettleMs=" + _hudSettleMs, "hudColdMs=" + _hudColdMs,
            "hudRollOff=" + _hudRollOff.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudPitOff=" + _hudPitOff.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudThr=" + _hudLeftPx.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudPStick=" + _hudPitStick.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudTexW=" + _hudTexW.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudAccPitch=" + _hudAccPitch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudAccRoll=" + _hudAccRoll.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudFineP=" + _hudFineP.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "hudRollStick=" + _hudRollStick.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        public float V1 = 0f, V2 = 0f;      // simulated pack voltages for the battery row
        public int Secs = 0;   // flight seconds - drives the draining battery readout
        public float Roll = 0f, PitchPx = 0f;
        public float Dpp = 8f, Shear = 0.9f, Len = 1f, RungTilt = 0f, MaxTilt = 30f, SpreadAmt = 2.2f, SpreadAccel = 0f;   // ladder geometry dials
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
        readonly SolidBrush _mw = new SolidBrush(Color.FromArgb(238, 255, 255, 255));   // MAVIC (allocated ONCE)
        readonly SolidBrush _mdim = new SolidBrush(Color.FromArgb(205, 235, 235, 235));
        readonly SolidBrush _mrec = new SolidBrush(Color.FromArgb(230, 226, 32, 32));
        readonly Pen _mgrid = new Pen(Color.FromArgb(58, 255, 255, 255), 1);
        readonly Pen _mthin = new Pen(Color.FromArgb(150, 255, 255, 255), 1);
        readonly Pen _mrecTrack = new Pen(Color.FromArgb(80, 226, 32, 32), 3);
        readonly Pen _mrecArc = new Pen(Color.FromArgb(245, 226, 32, 32), 4);
        // The form is recreated on every HUD on/off cycle, so its GDI objects MUST be disposed or
        // they leak (~13 per cycle) - which also ends in OutOfMemory / the red-X screen.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _f.Dispose(); _fb.Dispose(); _fm.Dispose(); _fs.Dispose(); } catch { }
                try { _g.Dispose(); _gb.Dispose(); _mw.Dispose(); _mdim.Dispose(); _mrec.Dispose(); } catch { }
                try { _p.Dispose(); _pt.Dispose(); _mgrid.Dispose(); _mthin.Dispose(); _mrecTrack.Dispose(); _mrecArc.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

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
          try
          {
            // These are readonly FIELDS, never per-frame allocations. Creating 6 pens/brushes every
            // paint (50fps) exhausted the GDI handle table within a couple of minutes; WinForms then
            // threw OutOfMemory and painted the red-X-on-white screen. NEVER allocate GDI in OnPaint.
            SolidBrush white = _mw, dim = _mdim;
            SolidBrush recB = _mrec;
            Pen grid = _mgrid, thin = _mthin;

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
            SolidBrush batB = low ? _mrec : white;   // reuse the field - was allocating a brush every frame
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

            // record button - iOS-style recording: a ring FILLS around while the red dot TIGHTENS in,
            // looping about every 2.6s, like the iPad/iPhone screen-recording indicator.
            float rcx = W - 96, rcy = H / 2 - 3;
            float ph = (Environment.TickCount % 2600) / 2600f;         // 0..1 over ~2.6s
            float ease = ph * ph * (3f - 2f * ph);                     // smooth the sweep
            _mrecTrack.StartCap = _mrecTrack.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            _mrecArc.StartCap = _mrecArc.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawEllipse(_mrecTrack, rcx - 30, rcy - 30, 60, 60);                     // track
            float sweep = ease * 359.5f;   // NOT 360: DrawArc with a full-circle sweep throws in GDI+
            if (sweep > 0.5f) g.DrawArc(_mrecArc, rcx - 30, rcy - 30, 60, 60, -90f, sweep);   // fill
            float dotR = 18f - 8f * ease;                              // the dot TIGHTENS in
            g.FillEllipse(recB, rcx - dotR, rcy - dotR, dotR * 2, dotR * 2);
            g.DrawString("REC", _fs, white, W - 128, H / 2 - 58);
          }
          catch { }   // a draw error must never bubble up - WinForms turns that into the red-X screen
        }

        protected override void OnPaint(PaintEventArgs e)
        {
          try
          {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int W = Width, H = Height, cx = W / 2, cy = H / 2;
            float rad = Roll * (float)Math.PI / 180f;
            if (float.IsNaN(rad) || float.IsInfinity(rad)) rad = 0f;   // never draw with NaN (GDI+ throws)

            if (Uav) { DrawMavic(g, W, H); base.OnPaint(e); return; }   // DJI-Fly style

            // artificial horizon + pitch ladder. Each rung is ROTATED by the roll so it stays PARALLEL
            // TO THE REAL HORIZON (tilted like the ground line), like a gyro/instrument horizon. Pitch
            // slides the whole ladder up/down.
            float tilt = Math.Min(1f, Math.Abs(PitchPx) / 900f + Math.Abs(Roll) / 180f);
            float tS = tilt * ((1f - SpreadAccel) + SpreadAccel * tilt * tilt);   // expo: fan builds slower then faster
            float spread = 1f + SpreadAmt * tS;
            // Rungs follow the bank ("rung tilt"), but the angle is CAPPED at "max tilt" so they can
            // never swing way out. The SAME capped angle drives the staircase, so the rungs stay level
            // with the slide instead of fighting it.
            float rt = rad * RungTilt;
            float maxR = MaxTilt * (float)Math.PI / 180f;
            if (rt > maxR) rt = maxR; if (rt < -maxR) rt = -maxR;
            float dxr = (float)Math.Cos(rt), dyr = (float)Math.Sin(rt);
            for (int d = -90; d <= 90; d += 10)
            {
                float yy = PitchPx + d * Dpp * spread;       // dpp = px per degree, fanned by the tilt
                float dist = Math.Abs(yy);
                float af = dist <= 300f ? 1f : 1f - (dist - 300f) / 440f;   // fade 300px -> 740px
                if (af <= 0.02f) continue;
                float half = (d == 0 ? 150f : 70f) * Len * (1f + 0.4f * tilt);
                int aMain = (int)(230 * af), aThin = (int)(165 * af), aTxt = (int)(235 * af);
                Pen pen = d == 0
                    ? new Pen(Color.FromArgb(aMain, 255, 255, 255), 2)
                    : new Pen(Color.FromArgb(aThin, 230, 230, 230), 1);
                float ay = cy + yy;
                // BOUND the bank term. tan(bank) is UNBOUNDED: at a real 75-85 deg bank it sent the
                // rungs thousands of px sideways and the whole ladder looked like it dropped off
                // ("it was fine until I banked then it went down"). Cap the angle used for the
                // shear, and cap the resulting offset to the frame.
                float shAng = rad; float shMax = 55f * (float)Math.PI / 180f;
                if (shAng > shMax) shAng = shMax; if (shAng < -shMax) shAng = -shMax;
                float shOff = (float)Math.Tan(shAng) * yy * Shear;
                float shLim = cx * 1.6f;
                if (shOff > shLim) shOff = shLim; if (shOff < -shLim) shOff = -shLim;
                float cxx = cx - shOff;                               // rail offset = the STAIRCASE (bank)
                PointF a = new PointF(cxx - half * dxr, ay - half * dyr);
                PointF b = new PointF(cxx + half * dxr, ay + half * dyr);
                g.DrawLine(pen, a, b);
                if (d == 0)
                {
                    g.DrawLine(pen, a, new PointF(a.X, a.Y + 12));   // end caps
                    g.DrawLine(pen, b, new PointF(b.X, b.Y + 12));
                }
                else
                {
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(aTxt, 255, 255, 255)))
                        g.DrawString((d > 0 ? "+" : "") + d, _f, lb, b.X + 6, b.Y + 5);
                }
                pen.Dispose();
            }

            // bank indicator: fixed tick at the top, marker slides sideways with the roll (never rotates)
            g.DrawLine(_pt, cx, 26, cx, 40);
            g.FillEllipse(_g, cx + Roll * 1.2f - 5, 34 - 5, 10, 10);

            // centre reticle
            g.DrawLine(_p, cx - 42, cy, cx - 12, cy);
            g.DrawLine(_p, cx + 12, cy, cx + 42, cy);
            g.DrawLine(_p, cx, cy - 42, cx, cy - 12);
            g.DrawLine(_p, cx, cy + 12, cx, cy + 42);
            g.DrawEllipse(_pt, cx - 3, cy - 3, 6, 6);

            // No compass tape any more - the pitch ladder's roll (the whole ladder spins about the
            // centre from the right-stick X) is the attitude cue now.

            // Battery row (with a real FPV look): two packs, each a pack icon + a 2-decimal voltage
            DrawBatt(g, 40, 74, V1);
            DrawBatt(g, 40, 108, V2);

            DrawLadder(g, W, cy, true, Spd);                       // speed on the left
            DrawLadder(g, W, cy, false, Alt != "" ? Alt : Agl);    // ALT on the right
            // no centre HOME, no fly timer / style text - the game already prints those
          }
          catch { }   // a paint error must never bubble up - WinForms turns that into the red-X screen
            base.OnPaint(e);
        }

        // A single pack: vertical battery icon (terminal nub + bar) then "15.02V", like the OSD.
        void DrawBatt(Graphics g, int x, int y, float volts)
        {
            int bw = 16, bh = 24;
            g.DrawRectangle(_p, x, y, bw, bh);
            g.FillRectangle(_g, x + bw / 2 - 4, y - 4, 8, 4);          // terminal nub
            float frac = (volts - 12.0f) / 8.0f;                        // 12.0..20.0V
            if (frac < 0.06f) frac = 0.06f;
            if (frac > 1f) frac = 1f;
            int inner = bh - 6;
            int fh = (int)(inner * frac);
            SolidBrush fill = frac <= 0.16f ? new SolidBrush(Color.FromArgb(235, 235, 60, 60)) : _g;
            g.FillRectangle(fill, x + 3, y + 3 + (inner - fh), bw - 6, fh);
            if (fill != _g) fill.Dispose();
            g.DrawString(volts.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "V",
                         _fb, _g, x + bw + 8, y + 2);
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
            // Normally hidden from capture so the OCR's screen grabs do not see the HUD. Set
            // "hudCap=1" in settings.ini to make it capturable (for comparing against the OBS overlay).
            if (!_hudCapturable) ExcludeFromCapture(_hudForm.Handle);
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
            _rfHomeText = "----";
            _homeLastM = -1f; _homeLastText = null; _homeLastAt = 0;
            _hudSmSeeded = false;   // re-seed the smoothed horizon for this flight
            _hudPitchVel = 0f; _hudRollVel = 0f; _hudPitStickSm = 0f; _hudRollStickSm = 0f; _hudSkySm = 1f;   // start each flight clean
            _hudW = 0f; _hudSRoll = 0f; _hudSPitch = 0f; _hudSmSeeded = false; _hudClutter = 0f;           // reset the weighted average
            _kfSeeded = false; _kfRollV = 0f; _kfPitV = 0f; _kfRollOb = 0f; _kfPitOb = 0f; _kfMeasAt = 0;                 // reset the estimator
            _kfRollGain = 1f; _kfPitGain = 1f; _kfRollP = 25f; _kfPitP = 25f; _kfRollZAt = 0; _kfPitZAt = 0;
            _kfSmSeeded = false; _kfSmRoll = 0f; _kfSmPit = 0f;
            _hudSolid = 0; _hugSm = 0f; _hugDisR = 0; _hugDisP = 0; _hugSignR = 0; _hugSignP = 0; _hugLogged = false;
            OverlayHub.I.SetFlight(true, 0.18f, 0);

            Thread t = new Thread(delegate ()
            {
                // The watch only starts once we ARE in the drone (the flow's Deploy As Drone, or
                // the opt-in screen detect), so show immediately - requiring LINK here meant a
                // flaky corner read kept the whole HUD hidden.
                bool shown = true;
                _hudLocked = false;    // re-lock the horizon each time the feed comes up
                _hudNeedLock = true;   // start hunting for the spawn horizon immediately
                _hudSettling = true;   // hold the ladder still while the spawn pose settles
                _hudSettleUntil = Environment.TickCount + _hudSettleMs;
                _seedN = 0; _hudSettleExt = 0;
                _hudColdUntil = Environment.TickCount + _hudColdMs;   // first 10s = plain median of the image
                _hudColdLogged = false;
                if (_physLog) PhysLogReset("deploy as drone");
                Log("   cold start: using the plain median horizon for the first " + (_hudColdMs / 1000) + "s while a solid line builds");
                Log("   spawn settle: holding the horizon for " + (_hudSettleMs / 1000) + "s and averaging what we see");
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
                long lastDet = 0;      // horizon measurement cadence (faster than the slow path)
                int outHits = 0;   // consecutive MENU reads - two in a row back the HUD down
                bool unfocusedLogged = false;   // one-time "game not focused" note

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

                    // ---- screen classification, ~1x/s (was 1.5s: menus/joins are short, so check
                    // more often - a clean menu now drops the HUD on the first read) ----
                    if (Environment.TickCount - lastOcr >= 1000)
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
                        // If the game is NOT the foreground window the grab is somebody else's app.
                        // Classify NOTHING and hold the last state - tabbing out to another monitor
                        // used to feed the OCR a browser/terminal whose words looked like a menu and
                        // dropped the HUD.
                        if (!RobloxFocused())
                        {
                            if (!unfocusedLogged) { Log("   RF: game not focused - holding the HUD state"); unfocusedLogged = true; }
                        }
                        else
                        {
                            unfocusedLogged = false;
                            int fs = ReadFlightSecs();   // FLIGHT (MAVIC) / FLY (FPV) clock
                            bool corner = CornerLinked();       // top-right "LINK LIVE" / "RC LIVE"
                            bool droneEv = fs >= 0 || corner || DroneKeyword();
                            // "not flying" proof. Menu words (incl. the profile strip "Cash" and our
                            // own name), plus only the unambiguous screen names. We must NOT use the
                            // whole ScreenName(): its "map" case matches the bare word POINT, and the
                            // MAVIC OSD prints "Return to a supply point".
                            string why; bool menuWeak = false;
                            string sn = ScreenName(OcrWords());
                            bool menuEv = MenuOnScreen(out why, out menuWeak)
                                || sn == "team select" || sn == "lobby" || sn == "loadout"
                                || sn == "team base" || sn == "loading";
                            // the map counts as a menu - but ONLY when there is no drone OSD text on
                            // screen, so the MAVIC's "Return to a supply point" cannot trigger it
                            if (!menuEv && sn == "map" && !droneEv) { menuEv = true; why = "map"; menuWeak = true; }
                            if (!menuEv && sn != "unknown" && sn != "map") { menuEv = true; why = "screen " + sn; }
                            if (fs >= 0) { _flightOcrBase = fs; _flightOcrAt = Environment.TickCount; }

                            // BIAS TO THE DRONE. The OSD is a handful of words; every menu carries at
                            // least one of the tells above. So if NO menu is on screen we assume we
                            // are flying (the user's "9/10" rule) and show the HUD - we do not wait
                            // for a positive drone read, and an unreadable frame keeps the previous
                            // state instead of dropping it. Two agreeing menu reads bring it down.
                            if (menuEv) outHits++; else outHits = 0;
                            if (!menuEv && !shown)
                            {
                                shown = true;
                                Log("   RF feed: overlay on" + (droneEv ? " (drone OSD)" : " (no menu detected)"));
                            }
                            // A CLEAN menu (no drone text anywhere on screen) drops the HUD on the
                            // FIRST read, so short screens like "Joining server" and team select do
                            // not keep it up for the debounce. If the frame also shows drone text
                            // (contradictory), take two reads so one bad OCR frame cannot blink it.
                            int needOut = droneEv ? 2 : 1;
                            // A WEAK-only menu (objective panel / SQUAD box / map) flashes on screen
                            // mid-flight when you press a button, so require several consecutive reads
                            // before dropping the HUD - a transient panel must not kill the overlay.
                            if (menuEv && menuWeak) needOut = Math.Max(needOut, 10);   // a map flash must not drop it
                            if (shown && outHits >= needOut)
                            {
                                shown = false;
                                Log("   menu on screen - overlay off (" + (why == "" ? "screen name" : why) + ")");
                                // tell the OBS terminal so it visibly drops back to the CLI instead
                                // of freezing on the last HUD frame
                                AddBlackLine("returning to command line - menu detected", "OK");
                            }
                            if (shown && droneEv && !menuEv)
                            {
                                ReadHudTop();             // heading + AGL
                                DetectHorizon();          // bank the artificial horizon
                                int st = DroneStyle();    // 1 MAVIC / 0 FPV / -1 - only a positive read may flip
                                if (st >= 0 && (st == 1) != _hudStyleUav)
                                {
                                    _hudStyleUav = (st == 1);
                                    Log("   drone on screen -> " + (_hudStyleUav ? "UAV (DJI)" : "FPV") + " HUD");
                                }
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
                    if (shown && Environment.TickCount - lastDet >= 45)
                    {
                        lastDet = Environment.TickCount;
                        DetectHorizon();
                    }
                    // SETTLE first: the drone spawns in odd poses (sometimes under the map), so one
                    // first frame is not trustworthy. While settling we only COLLECT samples; the
                    // lock then uses the MEDIAN of them.
                    if (_hudSettling)
                    {
                        if (Environment.TickCount >= _hudSettleUntil)
                        {
                            // A SOLID BASELINE wants several samples that AGREE. A median of one or
                            // two random frames (or of samples that disagreed wildly) is exactly the
                            // wobbly anchor that later drifts, so extend the window - twice at most -
                            // to gather more before committing.
                            float spread = 0f;
                            if (_seedN >= 3)
                            {
                                float[] sp = new float[_seedN]; Array.Copy(_seedP, sp, _seedN); Array.Sort(sp);
                                spread = sp[_seedN - 1] - sp[0];
                            }
                            if ((_seedN < 4 || spread > 160f) && _hudSettleExt < 1)
                            {
                                _hudSettleExt++;
                                _hudSettleUntil = Environment.TickCount + 1500;
                                Log("   spawn baseline not solid yet (" + _seedN + " samples, spread " +
                                    spread.ToString("0") + "px) - sampling a bit longer");
                            }
                            else
                            {
                                _hudSettling = false;
                                if (_seedN >= 3)
                                {
                                    float[] a = new float[_seedN]; Array.Copy(_seedR, a, _seedN); Array.Sort(a);
                                    float[] c = new float[_seedN]; Array.Copy(_seedP, c, _seedN); Array.Sort(c);
                                    _hudFRoll = a[_seedN / 2]; _hudFPitch = c[_seedN / 2];
                                    _hudLocked = true; _hudNeedLock = false;
                                    _kfSeeded = false;      // re-seed the estimator from the settled value
                                    Log("   horizon settled: roll " + _hudFRoll.ToString("0") + " deg, pitch " +
                                        _hudFPitch.ToString("0") + "px   (median of " + _seedN + " samples, spread " +
                                        spread.ToString("0") + "px)");
                                }
                                else Log("   settle done but only " + _seedN + " samples - locking from the next good frame");
                            }
                        }
                        else if (Environment.TickCount - lastDet >= 60)   // sample twice as fast - fewer seconds to a solid set
                        {
                            lastDet = Environment.TickCount;
                            DetectHorizon();
                            if (_hudDetValid && _hudDetConf >= 0.35f && _seedN < _seedR.Length)
                            { _seedR[_seedN] = _hudDetRoll; _seedP[_seedN] = _hudDetPitch; _seedN++; }
                            // COMMIT EARLY. Waiting out the whole window even when the samples already
                            // agree is what made the start feel slow (~15s). Once 5 samples line up
                            // within 90px, expire the window now - the next pass takes the lock from
                            // the median. The spread test keeps this from firing on a jumpy set.
                            if (_seedN >= 5)
                            {
                                float[] ep = new float[_seedN]; Array.Copy(_seedP, ep, _seedN); Array.Sort(ep);
                                if (ep[_seedN - 1] - ep[0] <= 90f) _hudSettleUntil = Environment.TickCount;
                            }
                        }
                    }
                    else if (!_hudLocked && (_hudNeedLock || shown) && Environment.TickCount >= lockArmedAt)
                    {
                        DetectHorizon();
                        if (_hudDetValid)
                        {
                            _hudLocked = true;
                            _hudNeedLock = false;
                            _hudFRoll = _hudDetRoll;
                            _hudFPitch = _hudDetPitch;
                            _kfSeeded = false;
                            Log("   horizon locked at spawn: roll " + _hudFRoll.ToString("0") +
                                " deg, pitch " + _hudFPitch.ToString("0") + "px");
                        }
                    }
                    _hudShown = shown;   // HudTick reads these
                    _hudSecs = secs;
                    }
                    catch (Exception ex) { Log("RF loop error: " + ex.Message); }
                    Thread.Sleep(45);   // loop fast so the horizon can re-measure ~20x/s
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
            OverlayHub.I.SetHud(false, "", "", "", 0f, 0f, false, 0f, 0f);
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
        try { if (DroneKeywordScan(OcrWords())) return true; } catch { }
        try { if (DroneKeywordScan(OcrWordsWhiten())) return true; } catch { }
        return false;
    }

    // POSITIONAL. The OSD prints "AGL" in the top-centre altitude readout and "MOUNTED" on the
    // payload line bottom-right. Requiring the word AND its place means a stray "AGL"/"MOUNTED"
    // anywhere else (menu text, OCR noise) can never be mistaken for the drone.
    bool DroneKeywordScan(List<string[]> ws)
    {
        int W = Screen.PrimaryScreen.Bounds.Width, H = Screen.PrimaryScreen.Bounds.Height;
        foreach (string[] w in ws)
        {
            int wx, wy;
            try { wx = int.Parse(w[0]); wy = int.Parse(w[1]); } catch { continue; }
            string n = Norm(w[4] ?? "");
            // FPV OSD: "AGL" sits in the TOP-CENTRE altitude readout, payload says "MOUNTED".
            if (n == "agl" && wy < H * 22 / 100 && wx > W * 28 / 100 && wx < W * 72 / 100) return true;
            if (n.IndexOf("mounted") >= 0 && wy > H * 82 / 100 && wx > W * 50 / 100) return true;
            // MAVIC OSD (measured off a real frame): "AGL 54.2m" is at the BOTTOM-LEFT, and the
            // bottom-right payload panel reads "PAYLOAD / 3 grenades ready / Amber ring: estimated
            // landing" - NOT "MOUNTED". Neither of the FPV tests above can ever match a MAVIC view,
            // which is exactly why the MAVIC HUD kept hiding while we were demonstrably flying it.
            if (n == "agl" && wy > H * 80 / 100 && wx < W * 25 / 100) return true;
            if (n.IndexOf("grenade") >= 0 && wy > H * 78 / 100 && wx > W * 50 / 100) return true;
            if (n.IndexOf("amber") >= 0 && wy > H * 78 / 100 && wx > W * 50 / 100) return true;
        }
        return false;
    }

    // Words that ONLY ever appear on the nav bar, the lobby, the loadout, the map or the panels -
    // NEVER on the drone OSD. Seeing any one of them is proof we are not flying, so the HUD comes
    // down immediately; the 30s timeout only covers the case where the OSD simply reads blank.
    // Is the screen a menu / loading screen? A frame like that has no horizon and must never be
    // fed to the estimator. Uses ONLY the cached OCR word list - calling a fresh OCR from the
    // detector thread would stall it, so a null cache just returns false and the frame is allowed.
    bool MenuishFrame()
    {
        try
        {
            List<string[]> ws = _ocrCache;
            if (ws == null) return false;
            foreach (string[] w in ws)
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                if (t.Length < 3) continue;
                // our own username is on every menu and never on the drone OSD
                if (_userName.Length >= 4 && Norm(t).IndexOf(Norm(_userName)) >= 0) return true;
                if (t.IndexOf("JOINING") >= 0 || t.IndexOf("CONNECTING") >= 0 ||
                    t.IndexOf("RESPAWN") >= 0 || t.IndexOf("SPECTAT") >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    // The team score bar runs across the very top: a saturated BLUE block on the left and a
    // saturated RED block on the right of the timer. It is on the soldier view and every menu,
    // but NOT on the drone OSD - so its presence means we are on foot and the HUD must not show.
    // Pure pixels, no OCR: fast and reliable even when nothing else is readable.
    bool ScoreBarOnScreen()
    {
        try
        {
            int W, H; int[] q = Grab(out W, out H);
            if (q == null) return false;
            int y1 = H * 5 / 100;
            for (int y = 0; y < y1; y++)
            {
                int blue = 0, red = 0; int blueX = -1, redX = -1;
                for (int x = W * 20 / 100; x < W * 80 / 100; x++)
                {
                    int v = q[y * W + x];
                    int r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
                    if (b >= 110 && b - r >= 40 && b - g >= 25) { blue++; if (blueX < 0) blueX = x; }
                    else if (r >= 110 && r - b >= 40 && r - g >= 35) { red++; if (redX < 0) redX = x; }
                }
                // both bars on the SAME row, blue to the LEFT of red
                if (blue >= 50 && red >= 50 && blueX >= 0 && redX > blueX) return true;
            }
        }
        catch { }
        return false;
    }

    bool MenuOnScreen() { string why; bool w; return MenuOnScreen(out why, out w); }

    bool MenuOnScreen(out string why) { bool w; return MenuOnScreen(out why, out w); }

    bool MenuOnScreen(out string why, out bool weak)
    {
        // "weak" tells are words the objective panel and the SQUAD box print, but which ALSO flash
        // on screen during flight (a button press pops the map/score board for a moment). A weak-only
        // match must NOT drop the HUD on one read - the caller debounces it.
        weak = false;
        // The team score bar (blue left | timer | red right) is printed across the top of the
        // SOLDIER / menu views and never on the drone OSD, so seeing it is decisive proof we are
        // not flying. This is the "deployed normally and the FPV HUD false-flagged" case - the
        // saturated blue+red pair is unmistakable and needs no OCR.
        if (ScoreBarOnScreen()) { why = "saw the team score bar"; weak = false; return true; }
        // Chosen from real OCR of every non-drone screen. DELIBERATELY EXCLUDED because they appear
        // on BOTH a menu and the drone OSD: POINT (map labels vs MAVIC "supply point"), RETURN
        // (TEAM BASE vs MAVIC "Return to a supply point"), BASE (map "Base" vs "TEAM BASE"), and the
        // warhead words FRAG/ROCKET/THERMO (TEAM BASE vs the FPV payload line).
        string[] keys = new string[] {
            // nav bar / lobby / loadout / settings
            "LOADOUT", "SETTINGS", "SHOP", "CHANGE TEAM", "SELECT DRONE", "DEPLOY",
            "AUDIO", "GRAPHICS", "BINDINGS", "VIEWMODEL", "ATTACHMENTS", "EQUIPMENT", "CUSTOMIZATION",
            // the profile strip - "Level NN  Cash $NNNN  <name>" - is on EVERY menu screen and on
            // NO drone OSD, so it alone is proof we are not flying
            "CASH",
            // TEAM BASE panel / base. "BASE" is safe to use as a tell: the map prints a "Base"
            // label and TEAM BASE contains it, and the drone OSD never prints the word at all.
            "TEAM BASE", "BASE", "DEPLOY AS DRONE", "WARHEAD",
            // team select / respawn / loading / crash (these screens have NO nav bar, which is why
            // the HUD used to linger on them)
            // NOTE: bare "JOIN" is deliberately NOT here - the TEAM BASE map prints a SQUAD box
            // reading "JOIN OR CREATE", and a substring match on "JOIN" then reported a menu the
            // instant after Deploy As Drone (it suppressed the HUD and re-ran the whole flow). The
            // loading screen is covered by "JOINING", and JOIN OR CREATE is a weak tell below.
            "PLAYERS", "JOINING", "SERVER", "CONNECTING", "RESPAWN", "SPECTAT", "DEPLOYING"
        };
        // WEAK tells - the objective panel / SQUAD box words. They appear in-flight too (a button
        // press flashes the map or scoreboard), so they only count after several consecutive reads.
        string[] weakKeys = new string[] {
            "CAPTURE", "CONTROL", "ELIMINATE", "TOP KILLS", "COMPLETED", "DEPLOY",
            "SQUAD", "GHILLE", "JOIN OR CREATE"
        };
        // Multi-word labels are matched as PHRASES, not single tokens - OCR hands back "TEAM" and
        // "BASE" separately, so the single-token scan above can never see "TEAM BASE".
        string[] phrases = new string[] {
            "TEAM BASE", "CHANGE TEAM", "SELECT DRONE", "DEPLOY AS DRONE", "TOP KILLS", "JOIN OR CREATE"
        };
        why = "";
        string weakWhy = "";
        List<string[]>[] sets = new List<string[]>[2];
        try { sets[0] = OcrWords(); } catch { }
        try { sets[1] = OcrWordsWhiten(); } catch { }
        foreach (List<string[]> set in sets)
        {
            if (set == null) continue;
            foreach (string[] w in set)
            {
                string t = (w[4] ?? "").ToUpperInvariant();
                for (int i = 0; i < keys.Length; i++)
                    if (t.IndexOf(keys[i]) >= 0) { why = "saw \"" + keys[i] + "\""; weak = false; return true; }
                for (int i = 0; i < weakKeys.Length; i++)
                    if (t.IndexOf(weakKeys[i]) >= 0) { if (weakWhy == "") weakWhy = "saw \"" + weakKeys[i] + "\""; }
                // our own name is printed on every menu and never on the drone OSD
                if (_userName.Length >= 4 && Norm(t).IndexOf(Norm(_userName)) >= 0)
                { why = "saw username"; weak = false; return true; }
            }
            foreach (string p in phrases)
                if (FindPhraseAll(p, null, set).Count > 0)
                {
                    // TOP KILLS / JOIN OR CREATE also flash in-flight -> treat those as weak
                    bool strongP = p != "TOP KILLS" && p != "JOIN OR CREATE";
                    why = "saw \"" + p + "\""; weak = !strongP; return true;
                }
        }
        // WEAK-only matches do NOT drop the HUD. They flash in flight (a button press pops the map /
        // scoreboard / SQUAD box, printing ELIMINATE / DEPLOY / SQUAD) and the overlay must stay up.
        // A real menu always carries a STRONG tell (CASH / your name / LOADOUT / TEAM BASE / PLAYERS..).
        if (weakWhy != "") { why = weakWhy; weak = true; }
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
        // Require the FLY/FLIGHT label first. A bare "mm:ss" is NOT proof of a drone - a menu can
        // print a time too, and accepting one turned the HUD on over the map/base. No label, no read.
        if (!label) return -1;
        Match m = Regex.Match(all, @"(\d{1,2})\s*[:.;]\s*(\d{2})");
        if (m.Success) return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        Match d = Regex.Match(all, @"(\d{1,2})\s?(\d{2})\b");
        if (d.Success) return int.Parse(d.Groups[1].Value) * 60 + int.Parse(d.Groups[2].Value);
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
    // ============= HORIZON MATH: camera model, robust fit, alpha-beta estimator =============

    // Pinhole camera model. The focal length in pixels follows from the vertical FOV, and that is
    // what makes the pitch PHYSICAL: a horizon `px` from the optical axis sits at a real angle
    // atan(px/f), not at a linearly-scaled px. Everything the estimator does lives in this space.
    float HudFocalPx()
    {
        float h = _hudFrameH > 0 ? _hudFrameH : 1080f;
        double t = Math.Tan(_hudFovY * Math.PI / 360.0);   // tan(FOV/2)
        if (t < 0.05) t = 0.05;
        return (float)(h / 2.0 / t);
    }
    float HudPxToDeg(float px) { float f = HudFocalPx(); return (float)(Math.Atan(px / f) * 180.0 / Math.PI); }
    float HudDegToPx(float deg) { float f = HudFocalPx(); return (float)(f * Math.Tan(deg * Math.PI / 180.0)); }
    // The "stick pitch" dial is px/s at the CENTRE; as an angular rate (what the game actually
    // commands) that is this many deg/s.
    float HudPitchRateDeg() { float f = HudFocalPx(); return _hudPitchRate / f * 57.29578f; }

    // Robust horizon-line fit. RANSAC consensus first (so a tree line, a road or a smoke plume
    // cannot drag the fit), then TOTAL LEAST SQUARES (PCA principal axis) on the inliers refined
    // with a Huber IRLS. TLS - not y-on-x least squares - because the transition points carry x
    // error too, and y-on-x is badly biased once the line tilts. Returns the residual spread so
    // the estimator can weight the frame.
    bool FitLineRobust(float[] xs, float[] ys, float[] ws, int n,
                       out float slope, out float icept, out float sigma, out int inl)
    {
        slope = 0f; icept = 0f; sigma = 6f; inl = 0;
        if (n < 6) return false;

        // --- RANSAC -------------------------------------------------------------
        float bm = 0f, bb = 0f; int best = -1;
        int iters = n * 6; if (iters > 360) iters = 360; if (iters < 60) iters = 60;
        Random rng = new Random(unchecked(n * 397 ^ Environment.TickCount));
        for (int it = 0; it < iters; it++)
        {
            int i1 = rng.Next(n), i2 = rng.Next(n);
            if (i1 == i2) continue;
            float dx = xs[i2] - xs[i1];
            if (Math.Abs(dx) < 60f) continue;
            float m = (ys[i2] - ys[i1]) / dx;
            if (Math.Abs(m) > 1.2f) continue;
            float b = ys[i1] - m * xs[i1];
            int cnt = 0;
            for (int i = 0; i < n; i++) if (Math.Abs(ys[i] - (m * xs[i] + b)) < 6f) cnt++;
            if (cnt > best) { best = cnt; bm = m; bb = b; }
        }
        if (best < n * 3 / 5 || best < 4) return false;
        inl = best;
        sigma = 2.5f;

        // --- TLS / PCA on the consensus set, refined with Huber IRLS ------------
        float c = 3f;
        for (int pass = 0; pass < 3; pass++)
        {
            // weighted centroid, with the robust weight computed from the LAST residuals
            double sw = 0, mx = 0, my = 0;
            for (int i = 0; i < n; i++)
            {
                float r = ys[i] - (bm * xs[i] + bb);
                if (Math.Abs(r) > 8f) continue;
                float w = (ws[i] + 0.1f) / (1f + (r / c) * (r / c));
                sw += w; mx += w * xs[i]; my += w * ys[i];
            }
            if (sw <= 0) break;
            mx /= sw; my /= sw;
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                float r = ys[i] - (bm * xs[i] + bb);
                if (Math.Abs(r) > 8f) continue;
                float w = (ws[i] + 0.1f) / (1f + (r / c) * (r / c));
                double dx = xs[i] - mx, dy = ys[i] - my;
                sxx += w * dx * dx; sxy += w * dx * dy; syy += w * dy * dy;
            }
            double th = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);   // principal axis direction
            double m2 = Math.Tan(th);
            if (Math.Abs(m2) > 1.2) break;
            bm = (float)m2; bb = (float)(my - bm * mx);
            // residual spread of the inliers -> the measurement noise the filter will use
            double sw2 = 0, ss = 0;
            for (int i = 0; i < n; i++)
            {
                float r = ys[i] - (bm * xs[i] + bb);
                if (Math.Abs(r) > 8f) continue;
                float w = ws[i] + 0.1f;
                sw2 += w; ss += w * r * r;
            }
            if (sw2 <= 0) continue;
            sigma = (float)Math.Sqrt(ss / sw2);
            c = sigma * 1.5f;
            if (c < 1.5f) c = 1.5f;
        }
        if (Math.Abs(bm) > 1.2f) return false;
        slope = bm; icept = bb;
        return true;
    }

    // Coarse appearance signature of a 6x6 colour grid, built from the block images the detector
    // already computed (so it costs almost nothing).
    static float[] SigFromBlocks(float[] imR, float[] imG, float[] imB, int gw, int gh)
    {
        float[] s = new float[108];
        for (int by = 0; by < 6; by++)
            for (int bx = 0; bx < 6; bx++)
            {
                int x0 = bx * gw / 6, x1 = (bx + 1) * gw / 6; if (x1 <= x0) x1 = x0 + 1;
                int y0 = by * gh / 6, y1 = (by + 1) * gh / 6; if (y1 <= y0) y1 = y0 + 1;
                double r = 0, g = 0, b = 0; int c = 0;
                for (int y = y0; y < y1 && y < gh; y++)
                    for (int x = x0; x < x1 && x < gw; x++)
                    { int ii = y * gw + x; r += imR[ii]; g += imG[ii]; b += imB[ii]; c++; }
                int o = (by * 6 + bx) * 3;
                s[o] = c > 0 ? (float)(r / c) / 255f : 0f;
                s[o + 1] = c > 0 ? (float)(g / c) / 255f : 0f;
                s[o + 2] = c > 0 ? (float)(b / c) / 255f : 0f;
            }
        return s;
    }

    // ---- BASE MAP SIGNATURES ("what map / scene are we on?") ------------------------------------
    // Baked from three real reference frames, each a 6x6x3 colour signature (normalised 0..1) in the
    // exact shape SigFromBlocks produces, so a live frame can be matched against them.
    //   snow : Screenshot 2026-09-27 065512 - sky lum 68 over SNOW ground lum 205 (the ground is the
    //          BRIGHT side; texture ratio 0.09 - a very flat sky).
    //   fog  : 2026-09-27 040809 - hazy; sky lum 172, ground 184, top band as busy as the whole scene.
    //   grey : 2026-09-27 172515 - flat overcast; B-R ~0.6 (no colour to key on).
    static readonly string[] MapNames = { "snow", "fog", "grey" };
    static readonly float[][] MapSigs = new float[][]
    {
        new float[] {0.2640f,0.2926f,0.3137f,0.2647f,0.2931f,0.3139f,0.2649f,0.2932f,0.3141f,0.2650f,0.2931f,0.3139f,0.2649f,0.2931f,0.3139f,0.2653f,0.2933f,0.3141f,0.3519f,0.3813f,0.4025f,0.3521f,0.3814f,0.4029f,0.3519f,0.3813f,0.4026f,0.3519f,0.3814f,0.4025f,0.3902f,0.4171f,0.4371f,0.3885f,0.4158f,0.4355f,0.4810f,0.5112f,0.5337f,0.4810f,0.5112f,0.5334f,0.4813f,0.5112f,0.5338f,0.4809f,0.5105f,0.5334f,0.4737f,0.5037f,0.5262f,0.4567f,0.4863f,0.5090f,0.6464f,0.6787f,0.7034f,0.6465f,0.6791f,0.7036f,0.6464f,0.6790f,0.7034f,0.6469f,0.6793f,0.7037f,0.6471f,0.6796f,0.7039f,0.6469f,0.6794f,0.7040f,0.7713f,0.8092f,0.8357f,0.7717f,0.8093f,0.8361f,0.7716f,0.8086f,0.8359f,0.7728f,0.8093f,0.8362f,0.7726f,0.8096f,0.8364f,0.7730f,0.8106f,0.8371f,0.7734f,0.8108f,0.8409f,0.7739f,0.8120f,0.8417f,0.7742f,0.8120f,0.8412f,0.7765f,0.8139f,0.8431f,0.7773f,0.8153f,0.8444f,0.7773f,0.8157f,0.8446f},
        new float[] {0.5626f,0.6244f,0.7100f,0.6020f,0.6724f,0.7710f,0.6141f,0.6679f,0.7555f,0.6628f,0.6997f,0.7650f,0.7289f,0.7503f,0.7926f,0.7614f,0.7715f,0.7935f,0.6497f,0.7078f,0.7829f,0.6429f,0.6987f,0.7740f,0.6566f,0.7006f,0.7682f,0.6938f,0.7209f,0.7652f,0.7343f,0.7441f,0.7649f,0.7463f,0.7487f,0.7553f,0.6677f,0.7052f,0.7535f,0.6677f,0.7012f,0.7455f,0.6829f,0.7055f,0.7383f,0.7082f,0.7192f,0.7354f,0.7259f,0.7298f,0.7360f,0.7325f,0.7374f,0.7375f,0.6509f,0.6777f,0.7125f,0.6577f,0.6802f,0.7099f,0.6868f,0.7082f,0.7185f,0.7292f,0.7602f,0.7349f,0.7559f,0.8025f,0.7462f,0.7748f,0.8320f,0.7567f,0.6898f,0.7129f,0.7145f,0.7419f,0.7870f,0.7400f,0.7710f,0.8272f,0.7520f,0.7803f,0.8337f,0.7584f,0.7905f,0.8423f,0.7680f,0.7976f,0.8500f,0.7753f,0.6692f,0.6724f,0.6345f,0.6556f,0.6636f,0.6187f,0.6879f,0.7101f,0.6580f,0.8035f,0.8224f,0.7801f,0.8015f,0.8413f,0.7772f,0.7874f,0.8395f,0.7659f},
        new float[] {0.7267f,0.7268f,0.7313f,0.8166f,0.8172f,0.8201f,0.8208f,0.8212f,0.8236f,0.8203f,0.8206f,0.8226f,0.7238f,0.7344f,0.7458f,0.6468f,0.6659f,0.6860f,0.7558f,0.7571f,0.7604f,0.7665f,0.7668f,0.7690f,0.7766f,0.7761f,0.7769f,0.7805f,0.7793f,0.7791f,0.7716f,0.7709f,0.7711f,0.7645f,0.7642f,0.7648f,0.7378f,0.7392f,0.7417f,0.7411f,0.7412f,0.7425f,0.7457f,0.7451f,0.7449f,0.7488f,0.7479f,0.7474f,0.7509f,0.7502f,0.7491f,0.7460f,0.7465f,0.7477f,0.7364f,0.7375f,0.7401f,0.7378f,0.7381f,0.7390f,0.7429f,0.7419f,0.7413f,0.7481f,0.7462f,0.7452f,0.7500f,0.7482f,0.7474f,0.7462f,0.7462f,0.7468f,0.7283f,0.7304f,0.7327f,0.7277f,0.7288f,0.7294f,0.7299f,0.7297f,0.7284f,0.7345f,0.7335f,0.7312f,0.7382f,0.7375f,0.7360f,0.7372f,0.7378f,0.7386f,0.6955f,0.6992f,0.7016f,0.7085f,0.7105f,0.7101f,0.7033f,0.7044f,0.7017f,0.7098f,0.7101f,0.7064f,0.7198f,0.7201f,0.7176f,0.7096f,0.7119f,0.7130f}
    };

    // Nearest baked base map. Returns the index and the RMS distance per channel (0..1 scale).
    static int MatchMap(float[] s, out float dist)
    {
        int best = -1; float bd = float.MaxValue;
        for (int i = 0; i < MapSigs.Length; i++)
        {
            float[] r = MapSigs[i]; float d = 0f;
            int n = r.Length < s.Length ? r.Length : s.Length;
            for (int j = 0; j < n; j++) { float df = r[j] - s[j]; d += df * df; }
            d = n > 0 ? (float)Math.Sqrt(d / n) : 0f;
            if (d < bd) { bd = d; best = i; }
        }
        dist = bd; return best;
    }

    // Same signature from a reference image on disk.
    static float[] SigFromBitmap(Bitmap bmp)
    {
        float[] s = new float[108];
        int W = bmp.Width, H = bmp.Height;
        for (int by = 0; by < 6; by++)
            for (int bx = 0; bx < 6; bx++)
            {
                int x0 = bx * W / 6, x1 = (bx + 1) * W / 6; if (x1 <= x0) x1 = x0 + 1;
                int y0 = by * H / 6, y1 = (by + 1) * H / 6; if (y1 <= y0) y1 = y0 + 1;
                int stp = Math.Max(1, Math.Min(x1 - x0, y1 - y0) / 12);
                double r = 0, g = 0, b = 0; int c = 0;
                for (int y = y0; y < y1; y += stp)
                    for (int x = x0; x < x1; x += stp)
                    { Color p = bmp.GetPixel(x, y); r += p.R; g += p.G; b += p.B; c++; }
                int o = (by * 6 + bx) * 3;
                s[o] = c > 0 ? (float)(r / c) / 255f : 0f;
                s[o + 1] = c > 0 ? (float)(g / c) / 255f : 0f;
                s[o + 2] = c > 0 ? (float)(b / c) / 255f : 0f;
            }
        return s;
    }

    // Load every image in "<exe>\hudref\" once. File name decides the label: contains "bad", "no_"
    // or "false" -> BAD example; anything else -> GOOD example. e.g.  bad-ground.png, sky01.png
    void LoadHudRefs()
    {
        _refLoaded = true;
        try
        {
            string dir = Path.Combine(_appDir, "hudref");
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
                Log("reference frames: none yet - drop labelled screenshots in " + dir +
                    " (name them bad-*.png when the lock is WRONG, good otherwise)");
                return;
            }
            string[] fs = Directory.GetFiles(dir);
            int ng = 0, nb = 0;
            for (int i = 0; i < fs.Length; i++)
            {
                string ext = Path.GetExtension(fs[i]).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") continue;
                try
                {
                    using (Bitmap b = new Bitmap(fs[i]))
                    {
                        string nm = Path.GetFileName(fs[i]).ToLowerInvariant();
                        bool good = nm.IndexOf("bad") < 0 && nm.IndexOf("no_") < 0 && nm.IndexOf("false") < 0;
                        _refSigs.Add(SigFromBitmap(b));
                        _refGoodL.Add(good);
                        // optional sidecar with the horizon line captured at the same moment
                        float[] ln = null;
                        try
                        {
                            string hz = Path.ChangeExtension(fs[i], ".hzn");
                            if (File.Exists(hz))
                            {
                                float rr = 0f, pp = 0f; bool got = false;
                                foreach (string line in File.ReadAllLines(hz))
                                {
                                    int eq = line.IndexOf('=');
                                    if (eq <= 0) continue;
                                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                                    float v; if (!float.TryParse(line.Substring(eq + 1).Trim(),
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                                    if (k == "roll") { rr = v; got = true; }
                                    else if (k == "pitch") { pp = v; got = true; }
                                }
                                if (got) ln = new float[] { rr, pp };
                            }
                        }
                        catch { }
                        _refLine.Add(ln);
                        if (good) ng++; else nb++;
                    }
                }
                catch { }
            }
            Log("reference frames: " + ng + " good / " + nb + " bad loaded from " + dir);
        }
        catch { }
    }

    // Save the CURRENT frame plus the horizon line we believe is right, as a labelled reference.
    // Later frames that look like it adopt that line outright. This is the "teach it with photos"
    // loop: fly somewhere the HUD is correct, click this, and it will be right there next time.
    void SaveRef()
    {
        try
        {
            string dir = Path.Combine(_appDir, "hudref");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int W, H; int[] px = Grab(out W, out H);
            string baseName = "good-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            float[] sig;
            using (Bitmap b = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                System.Drawing.Imaging.BitmapData bd = b.LockBits(new Rectangle(0, 0, W, H),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, b.PixelFormat);
                int stride = bd.Stride;
                byte[] buf = new byte[stride * H];
                for (int y = 0; y < H; y++)
                {
                    int ro = y * stride, so = y * W;
                    for (int x = 0; x < W; x++)
                    {
                        int c = px[so + x];
                        buf[ro + x * 4] = (byte)(c & 0xFF);
                        buf[ro + x * 4 + 1] = (byte)((c >> 8) & 0xFF);
                        buf[ro + x * 4 + 2] = (byte)((c >> 16) & 0xFF);
                        buf[ro + x * 4 + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
                b.UnlockBits(bd);
                sig = SigFromBitmap(b);
                b.Save(Path.Combine(dir, baseName + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            float rr = _hudDetValid ? _hudMRoll : _hudFRoll;
            float pp = _hudDetValid ? _hudMPitch : _hudFPitch;
            File.WriteAllText(Path.Combine(dir, baseName + ".hzn"),
                "roll=" + rr.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\r\npitch=" + pp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _refSigs.Add(sig);
            _refGoodL.Add(true);
            _refLine.Add(new float[] { rr, pp });
            _refLoaded = true;
            Log("capture ref: saved " + baseName + " at roll " + rr.ToString("0.0") + " deg / pitch " + pp.ToString("0") + " px (" + _refSigs.Count + " refs)");
        }
        catch (Exception ex) { Log("capture ref failed: " + ex.Message); }
    }

    // Nearest reference by signature distance (0 = identical). Returns the distance and the label.
    float MatchRefs(float[] sig, out bool good, out float[] line)
    {
        good = true; line = null; float best = 9f; int bi = -1;
        for (int i = 0; i < _refSigs.Count; i++)
        {
            float[] r = _refSigs[i];
            float d = 0f;
            for (int k = 0; k < 108; k++) { float e = sig[k] - r[k]; d += e * e; }
            d = (float)Math.Sqrt(d / 108.0);
            if (d < best) { best = d; good = _refGoodL[i]; bi = i; }
        }
        if (bi >= 0 && bi < _refLine.Count) line = _refLine[bi];
        return best;
    }

    // System ID. Compare the rate the IMAGE shows with the rate the stick COMMANDED, and nudge the
    // gain toward it. Guarded hard: only while the stick is clearly commanding, ratio sanity-gated,
    // and clamped - so a single bad frame can never wind the gain away.
    void LearnGain(ref float gain, ref float lastZ, ref long lastAt, float z,
                   float cmdRate, float stick, long now, float minStick, float trust)
    {
        try
        {
            // Only learn from a frame we actually TRUST, and only while the stick is clearly
            // commanding. A stubborn gain that winds up makes the model over-rotate and the vision
            // fight it - that is the visible "jump". Clamped tight (0.5..1.8) and very slow.
            // trust here is the MEASUREMENT quality passed in by the caller (see HudFuse), not the
            // stick-dependent vision trust.
            if (trust < 0.30f || Math.Abs(stick) < minStick) { lastAt = 0; return; }
            if (lastAt == 0) { lastZ = z; lastAt = now; return; }
            float dtZ = (now - lastAt) / 1000f;
            // HOLD THE REFERENCE until the measurement has had time to actually update. This runs
            // at the 50 Hz HUD rate, and refreshing the timestamp on EVERY call made dtZ
            // permanently ~0.02 s, so the window below was unreachable and the gain NEVER LEARNED.
            // (Changing only the threshold was not enough - the refresh itself had to move.)
            if (dtZ < 0.10f) return;
            if (dtZ < 0.70f)
            {
                float measRate = (z - lastZ) / dtZ;
                if (Math.Abs(cmdRate) > 4f)
                {
                    float ratio = measRate / cmdRate;
                    if (ratio > 0.30f && ratio < 3.00f)
                    {
                        gain += 0.050f * (ratio - gain);
                        if (gain < 0.15f) gain = 0.15f;
                        if (gain > 3.00f) gain = 3.00f;
                    }
                }
            }
            lastZ = z; lastAt = now;                  // refresh ONLY when the window has elapsed
        }
        catch { }
    }

    // One estimator step (runs at the HUD rate). The game is RATE controlled, so the stick is a
    // CONTROL input (its own dial value becomes deg/s), not a display offset. The vision is a
    // measurement of the true attitude. A scalar Kalman/alpha-beta per axis fuses them:
    //   predict:  x += v*dt ;  P += Q*dt          (Q grows -> coasting uncertainty)
    //   update:   alpha = P/(P+R) ; beta = a^2/(2-a)   (R = the fit's variance / trust)
    //   gate:     reject if |innovation| > 3*sqrt(P+R)   (Mahalanobis)
    // With no usable frame it simply coasts at the last rate, which is exactly "hold where the
    // horizon would be". Two slow learners ride along: the constant offset (the detector's bias)
    // and the stick->rate gain (system ID).
    void HudFuse(float dt, float sxS, float syS, long now)
    {
        if (dt <= 0f) return;
        // While the spawn pose is settling, HOLD the ladder: no stick integration and no vision.
        // (The drone can be falling / under the map for a moment, and integrating that would throw
        // the horizon away before the settle has even finished.)
        if (_hudSettling)
        {
            _kfRollV = 0f; _kfPitV = 0f;
            // HOLD THE ESTIMATE STILL, BUT SHOW THE HORIZON WE CAN ALREADY SEE. This used to return
            // with _hudFRoll/_hudFPitch untouched, and at the start of a flight those are still 0 -
            // so the ladder parked at the CENTRE OF THE SCREEN for the whole settle window while the
            // horizon was several hundred px away, then snapped. Seen live: HUD pit 0 against a
            // measured -382. The settle is meant to stop the estimate moving, not to hide the truth.
            if (_hudDetValid && (now - _hudDetAt) < 2000)
            {
                _kfRollX = _hudMRoll;
                _kfPitX = HudPxToDeg(_hudMPitch);
                _hudFRoll = _kfRollX;
                _hudFPitch = HudDegToPx(_kfPitX);
            }
            return;
        }
        float f = HudFocalPx();

        if (!_kfSeeded)
        {
            _kfSeeded = true;
            // SEED FROM THE FIRST MEASUREMENT when one is available. This used to seed from
            // _hudFRoll/_hudFPitch, which still held the PREVIOUS flight values (often 0), so for the
            // first seconds of every deploy the ladder sat at the screen centre while the horizon was
            // hundreds of px away, waiting for the filter to walk across. Seen live: HUD pit 0 against
            // a measured -313.
            bool seedFromMeas = _hudDetValid && (now - _hudDetAt) < 2000;
            _kfRollX = seedFromMeas ? _hudMRoll : _hudFRoll;
            _kfPitX = seedFromMeas ? HudPxToDeg(_hudMPitch) : (float)(Math.Atan(_hudFPitch / f) * 180.0 / Math.PI);
            _kfRollV = 0f; _kfPitV = 0f;
            _kfRollOb = 0f; _kfPitOb = 0f;
            _kfRollP = 25f; _kfPitP = 25f;
            _hudCoastMs = 0f;
        }

        // ---- PREDICT (control model) ----
        float rollCmd = -sxS * _hudRollRate * _kfRollGain;          // deg/s
        float pitCmd = -syS * HudPitchRateDeg() * _kfPitGain;       // deg/s
        float av = 1f - (float)Math.Pow(0.5, dt / _hudAccelTau);    // the stick->rate lag
        _kfRollV += (rollCmd - _kfRollV) * av;
        _kfPitV += (pitCmd - _kfPitV) * av;
        // ---- COAST LEASH -------------------------------------------------------------------------
        // THE "good for a minute then delusional" BUG. The control model is a pure INTEGRATOR of the
        // stick, but a real drone holds a steady ATTITUDE for a held stick - so while the vision is
        // missing (a discarded frame, fog, looking down) a held forward stick integrates into an
        // ever-growing pitch and the line walks away. Instead, with no fresh vision the velocity is
        // allowed to BLEED OFF, so the estimate hovers where it was rather than running.
        bool fresh = _hudDetValid && (now - _hudDetAt) < 700;
        if (!fresh)
        {
            // ACRO MODE: the stick is a RATE command, so with a stick held the attitude KEEPS
            // ROTATING and the model must keep integrating - that is the whole point, and it is
            // what makes the sticks able to tell us where the horizon is while it is off screen.
            // This used to bleed the rate to zero whenever the vision was missing, which is the
            // behaviour of an ANGLE/self-levelling flight mode, not acro: a held stick stopped
            // moving the line while the real drone kept turning, so it drifted further the longer
            // it was blind. The bleed now only runs when there is NO stick command at all (rate
            // should already be ~0), so it just settles the state instead of fighting the pilot.
            float stickMag = Math.Max(Math.Abs(sxS), Math.Abs(syS));
            if (stickMag < 0.05f)
            {
                float cl = 1f - (float)Math.Pow(0.5, dt / 0.9);
                _kfRollV -= _kfRollV * cl;
                _kfPitV -= _kfPitV * cl;
            }
            _hudCoastMs += dt * 1000f;
        }
        _kfRollX += _kfRollV * dt;
        _kfPitX += _kfPitV * dt;
        _kfRollP += _hudManeuver * dt;      // uncertainty grows; faster while coasting
        _kfPitP += _hudManeuver * dt;
        // Cap the growth: an unbounded P would make the gain (and the gate) run away after a long
        // blackout and a single frame could then yank the velocity state. 900 keeps the gate sane.
        if (_kfRollP > 900f) _kfRollP = 900f;
        if (_kfPitP > 900f) _kfPitP = 900f;

        // ---- UPDATE (vision), when a fresh line is available ----
        // APPLY EACH MEASUREMENT ONCE. The detector produces a new reading ~10x/s but this runs at
        // 50 Hz, and the 2000 ms freshness window meant the SAME reading was reused ~100 times -
        // pulling 30% of the innovation on every pass. That massively over-weighted one stale
        // number and actively fought the model: during a hard pull-up the measurement stayed frozen
        // while the model correctly integrated the stick, so the line barely moved (86 px against a
        // ~400 px model response), then snapped when the reading finally updated. Between
        // measurements the MODEL should carry the attitude - that is exactly what acro means.
        bool det = _hudDetValid && (now - _hudDetAt) < 2000 && _hudDetAt != _kfMeasAt;
        if (det)
        {
            _kfMeasAt = _hudDetAt;                 // consume this measurement exactly once
            // AXIS/MEASUREMENT SMOOTHING (dial "axis smooth"): low-pass the detector's measurement
            // BEFORE the estimator sees it, so a jittery per-frame colour-axis lock cannot wobble the
            // line. 0 = no smoothing (use the raw frame measurement).
            float mst = _hudMsTau; if (mst < 0f) mst = 0f;
            if (!_kfSmSeeded) { _kfSmSeeded = true; _kfSmRoll = _hudMRoll; _kfSmPit = _hudMPitch; }
            else if (mst > 0.001f)
            {
                float msa = 1f - (float)Math.Pow(0.5f, dt / mst);
                _kfSmRoll += (_hudMRoll - _kfSmRoll) * msa;
                _kfSmPit += (_hudMPitch - _kfSmPit) * msa;
            }
            else { _kfSmRoll = _hudMRoll; _kfSmPit = _hudMPitch; }

            float zr = _kfSmRoll - _kfRollOb;                       // offset learned away
            while (zr - _kfRollX > 180f) zr -= 360f;                // unwrap (roll is periodic)
            while (zr - _kfRollX < -180f) zr += 360f;
            float zp = HudPxToDeg(_kfSmPit) - _kfPitOb;
            float ir = zr - _kfRollX;
            float ip = zp - _kfPitX;

            // trust: confident frame, sky in view, and the stick NOT yanking (anti-false-flag)
            float skyT = Smooth01(_hudSkySm, 0.04f, 0.20f);
            float actT = Smooth01(Math.Max(Math.Abs(sxS), Math.Abs(syS)), 0.15f, 0.30f);
            // SOFTER WHILE FLYING: once the stick is working the frames are far more random (the dive,
            // turns, the ground rushing past), so the image gets LESS say than it did at the spawn
            // baseline. The damping was 0.45; at 0.75 a worked stick leaves the estimator largely to
            // the control model, which is what stops a random frame yanking the line.
            // Hard stick = the scene is changing fast (a 180 swings the whole view), so the image is
            // at its least reliable exactly when it can do the most damage. At full deflection the image
            // now gets ~5% say instead of 25%, leaving the turn to the control model; the vision resumes
            // as the stick comes back. This is what stops a hard turn throwing the line.
            float trust = (0.55f + 0.45f * _hudMConf) * (0.40f + 0.60f * skyT) * (1f - 0.95f * actT);
            if (trust < 0.03f) trust = 0.03f;

            float Rr = _hudMRollVar / trust;                        // deg^2
            float Rp = _hudMPitchVar * 3282.8f / (f * f) / trust;   // px^2 -> deg^2
            if (Rr < 0.02f) Rr = 0.02f;
            if (Rp < 0.02f) Rp = 0.02f;

            float aR = _kfRollP / (_kfRollP + Rr);
            float aP = _kfPitP / (_kfPitP + Rp);
            // BOUNDED gain. The vision is only allowed to NUDGE the attitude; it must never slam it.
            // An unbounded Kalman gain was exactly what made the ladder whip around: with a noisy
            // pixel measurement and a small dt the alpha-beta BETA term differentiates the noise and
            // drives the RATE state wild (+-100 deg/s spikes). So the RATE state is driven by the
            // stick model only (plus the slow system-ID learner below) - the vision corrects POSITION.
            // RE-ACQUIRE: after a long coast the estimate may be well off, so let a returning
            // measurement pull harder for this frame - otherwise the capped gain takes many seconds
            // to walk the line back and it looks like it never recovers.
            float coastBefore = _hudCoastMs; _hudCoastMs = 0f;
            // The steady-state lag is proportional to 1/gain, so a 0.30 ceiling left the line a
            // constant ~60 px behind the measurement (measured: 50-90 px on every sample). The rate
            // state is driven by the stick only these days, so a stiffer position gain no longer
            // differentiates noise and cannot make the ladder whip - the old reason for the low cap.
            // SOFTENED: the physics is a CONSTANT of the game (acro - no wind, no levelling, no
            // load variation), so once the rates are calibrated the model is exact and the image
            // can only add noise. Measured: median 195 px of fused-vs-measured disagreement, which
            // is the image pulling the estimate around. The image is now a TRIM - it may nudge the
            // attitude, not drive it. The re-acquire floor below still lets a genuinely lost
            // estimate be pulled back hard, so this does not trade away recovery.
            float gainCap = 0.15f;
            if (coastBefore > 1500f) gainCap = 0.15f + 0.25f * Math.Min(1f, (coastBefore - 1500f) / 2500f);
            if (aR > gainCap) aR = gainCap;
            if (aP > gainCap) aP = gainCap;
            // RE-ACQUIRE FLOOR. The gain above can come out TINY - aP = P/(P+Rp) with Rp inflated
            // by a large measurement variance and divided by a small trust - so a big honest error
            // that already PASSED the gate gets corrected at a crawl. Measured live: 34 px per
            // SECOND of correction against a 1300 px error, which looks like it is simply stuck.
            // A large innovation that the gate accepted deserves a decisive pull.
            float floorR = 0.05f, floorP = 0.05f;
            // ...but a GENUINE divergence must still be rescued fast. The detector only PUBLISHES
            // every 2-3 s in practice (it discards most frames), so a 0.20 gain closes a 500 px
            // error at about 10%/second - twenty to thirty seconds - while the model keeps
            // integrating. Measured live: fused +168 against a measured -324, drifting further
            // apart. A large innovation therefore pulls hard; a small one still barely moves it.
            if (Math.Abs(ir) > 4f) floorR = 0.55f;      // ~50 px at f=704
            if (Math.Abs(ip) > 4f) floorP = 0.55f;
            if (aR < floorR) aR = floorR;
            if (aP < floorP) aP = floorP;

            // ---- SOLID-LOCK COUNT + HUG ---------------------------------------------------------
            // A frame is SOLID when the detector is confident AND sky is genuinely in view. After
            // ~10 solid frames the horizon counts as ESTABLISHED, and we HUG it: the line then tracks
            // the CONTROLLER (the integrated stick model) and lets the vision pull only a small
            // fraction as fast, so it stops chasing every noisy frame. A SUSTAINED disagreement in
            // one direction is a real move, so that releases the hug and lets it re-lock.
            bool solidFrame = (_hudMConf >= 0.70f && _hudDetSky >= 0.18f);
            if (solidFrame) { if (_hudSolid < 40) _hudSolid++; }
            else if (_hudSolid > 0) _hudSolid -= 4;
            if (_hudSolid < 0) _hudSolid = 0;
            // A HUG MUST NOT SURVIVE MANEUVERING. While you work the stick - especially banking - the
            // scene moves and a held pitch would ride a small stick-Y bias into a steady drift while
            // the vision gain is cut to 25%. That is exactly "it was fine until I banked then it went
            // down". Any real stick input drops the hug (fast); it re-forms once things settle.
            float stickNow = Math.Max(Math.Abs(sxS), Math.Abs(syS));
            float hugT = (_hudSolid >= 10 && stickNow < 0.12f) ? 1f : 0f;
            float hugTau = hugT > 0.5f ? 0.40f : 0.10f;          // ease in slowly, drop out fast
            _hugSm += (hugT - _hugSm) * (1f - (float)Math.Pow(0.5f, dt / hugTau));

            // The thresholds are DELIBERATELY large: a hug means the estimate lags on purpose (the
            // pull is 4x slower), so a couple of degrees of innovation is normal and must NOT
            // release it. Only a big, one-directional divergence - the model genuinely wrong - does.
            if (_hugSm > 0.30f && Math.Abs(ir) > 10f)
            {
                int sgn = ir > 0f ? 1 : -1;
                if (sgn == _hugSignR) _hugDisR++; else { _hugDisR = 1; _hugSignR = sgn; }
                if (_hugDisR >= 10) { _hudSolid = 0; _hugSm = 0f; _hugDisR = 0; Log("   horizon: sustained ROLL divergence - releasing the hug"); }
            }
            else _hugDisR = 0;
            if (_hugSm > 0.30f && Math.Abs(HudDegToPx(ip)) > 60f)
            {
                int sgn = ip > 0f ? 1 : -1;
                if (sgn == _hugSignP) _hugDisP++; else { _hugDisP = 1; _hugSignP = sgn; }
                if (_hugDisP >= 10) { _hudSolid = 0; _hugSm = 0f; _hugDisP = 0; Log("   horizon: sustained PITCH divergence - releasing the hug"); }
            }
            else _hugDisP = 0;

            if (_hugSm > 0.5f && !_hugLogged) { _hugLogged = true; Log("   horizon: LOCKED (" + _hudSolid + " solid fixes) - hugging the location"); }
            if (_hugSm < 0.2f) _hugLogged = false;

            // hug = trust the model; only ~25% of the vision gain gets through
            float hg = 1f - 0.75f * _hugSm;
            aR *= hg; aP *= hg;

            // Mahalanobis-style gate: reject a frame that jumps further than a real horizon could in
            // one step. Grounded in the measurement noise so a clean lock gets a tighter gate.
            // ... opening up while the state is uncertain (a long coast) so re-locking still works.
            float gR = 4f * (float)Math.Sqrt(Rr) + (float)Math.Sqrt(_kfRollP); if (gR < 10f) gR = 10f;
            float gP = 4f * (float)Math.Sqrt(Rp) + (float)Math.Sqrt(_kfPitP); if (gP < 6f) gP = 6f;
            // FILTER DIVERGENCE GUARD. The gate above is sized for small errors, so if the estimate
            // drifts a long way while the horizon is off screen the error can exceed it - and then
            // EVERY correction is rejected and the filter can never recover. (Seen live: the ladder
            // sat 670 px from the measurement and was drifting further, not converging.) After a long
            // blind stretch the error is legitimately large, so widen the gate and let it come back.
            // After a long blind stretch the estimate has NO authority left - the model has been
            // free to drift for the whole time - so ANY measurement that already passed the
            // detector's own checks is better than what we have. The gate is therefore opened
            // COMPLETELY rather than merely widened. Measured live: the estimate sat 1116 px out
            // (74 deg) with a zero model rate and 0.7-0.9 confidence readings arriving, and even
            // the 6x-wide gate (~18 deg) rejected every single correction - so it stayed there.
            if (coastBefore > 1200f) { gR = 1e9f; gP = 1e9f; }
            else if (coastBefore > 800f) { float wf = 1f + coastBefore / 2000f; if (wf > 6f) wf = 6f; gR *= wf; gP *= wf; }
            bool okR = Math.Abs(ir) <= gR;
            bool okP = Math.Abs(ip) <= gP;

            if (okR) { _kfRollX += aR * ir; _kfRollP = (1f - aR) * _kfRollP; }
            if (okP) { _kfPitX += aP * ip; _kfPitP = (1f - aP) * _kfPitP; }

            // OFFSET LEARNING - this is what used to drag the horizon DOWN permanently ("it thinks
            // the ground is the sky"). It must only ever absorb a SMALL, CONSTANT bias, so it learns
            // only from a trusted frame whose residual is already small (a real bias shows up as a
            // consistent little residual). A large residual is a WRONG LOCK - learning it in would
            // bake the mistake in, so it is never learned. Clamped so it can never run away.
            float learnK = 1f - 0.85f * _hugSm;    // a locked horizon barely re-learns its offset
            if (okR && trust > 0.45f && Math.Abs(ir) < 2.5f) { _kfRollOb += 0.010f * ir * learnK; }
            if (okP && trust > 0.45f && Math.Abs(ip) < 2.0f) { _kfPitOb += 0.010f * ip * learnK; }
            if (_kfRollOb > 6f) _kfRollOb = 6f; if (_kfRollOb < -6f) _kfRollOb = -6f;
            if (_kfPitOb > 6f) _kfPitOb = 6f; if (_kfPitOb < -6f) _kfPitOb = -6f;

            // slow gain learning (system ID)
            // LearnGain must judge whether the MEASUREMENT is any good - NOT whether we are allowed
            // to move the state quickly this frame. Those are different questions, and using the vision
            // trust made learning impossible: trust contains (1 - 0.95*actT), so any stick big enough to
            // pass the |stick| >= 0.30 test had already driven trust to ~0.03. The two conditions could
            // never both hold, which is why the gain never moved however long you flew.
            float mq = _hudMConf * (0.5f + 0.5f * skyT);
            if (okR && _hugSm < 0.6f) LearnGain(ref _kfRollGain, ref _kfRollZ, ref _kfRollZAt, zr, -sxS * _hudRollRate, sxS, now, 0.30f, mq);
            if (okP && _hugSm < 0.6f) LearnGain(ref _kfPitGain, ref _kfPitZ, ref _kfPitZAt, zp, -syS * HudPitchRateDeg(), syS, now, 0.30f, mq);
        }

        // ---- OUTPUT (roll deg, pitch px for the ladder) ----
        if (_kfRollX > 180f) _kfRollX -= 360f;
        if (_kfRollX < -180f) _kfRollX += 360f;
        _hudFRoll = _kfRollX;
        _hudFPitch = HudDegToPx(_kfPitX);
    }

    // ---- COLD-START HORIZON: "just the median of the image" ------------------------------------
    // Deliberately dumb and deliberately robust. Walk down every column from the top and find the
    // first row where the picture stops looking like the sky at the top of the frame; the horizon
    // is the MEDIAN of those rows. A median is not dragged anywhere by a single road, wall or
    // bright patch, which is the whole point: for the first seconds of a flight we would rather be
    // boringly stable than clever and wrong. The slope comes from a robust fit so banking still
    // tracks; if the fit will not settle, we fall back to a FLAT line at the median row.
    bool MedianHorizonLine(int[] px, int W, int H, out float mslope, out float micept, out float sky, out int ninl)
    {
        mslope = 0f; micept = H * 0.5f; sky = 0.5f; ninl = 0;
        double ss = 0; int sn = 0;
        for (int y = 4; y < H / 14; y += 2)
            for (int x = W / 10; x < W * 9 / 10; x += 8)
            { int c = px[y * W + x]; ss += 0.299 * ((c >> 16) & 0xFF) + 0.587 * ((c >> 8) & 0xFF) + 0.114 * (c & 0xFF); sn++; }
        if (sn == 0) return false;
        float sLy = (float)(ss / sn);
        double gs = 0; int gn = 0;
        for (int y = H * 86 / 100; y < H * 95 / 100; y += 4)
            for (int x = W / 10; x < W * 9 / 10; x += 10)
            { int c = px[y * W + x]; gs += 0.299 * ((c >> 16) & 0xFF) + 0.587 * ((c >> 8) & 0xFF) + 0.114 * (c & 0xFF); gn++; }
        if (gn == 0) return false;
        float gLy = (float)(gs / gn);
        float dly = Math.Abs(gLy - sLy);
        // A WEAK sky/ground step is exactly where the median is untrustworthy: the "first row that
        // leaves the sky" then trips on noise just under the top band and reports a horizon near
        // the top of the frame. Measured live: a clean frame steps ~114, a hazy fog/grey frame only
        // ~18. Below 30 we hand the frame to the normal detector instead of guessing.
        // Relaxed for the COLD START ONLY. The median is used nowhere else, and at a deploy a rough
        // line beats no line - a blank HUD is worse than one that is a few percent out. (The 30
        // guard was added when a weak frame produced a bogus 7% horizon in normal flight.)
        if (dly < 15f) return false;

        int cap = W / 8 + 8;
        float[] onX = new float[cap], onY = new float[cap]; int onN = 0;
        for (int x = W / 10; x < W * 9 / 10; x += 8)
        {
            for (int y = H / 14; y < H * 82 / 100 - 6; y += 2)
            {
                int c = px[y * W + x];
                float l = 0.299f * ((c >> 16) & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * (c & 0xFF);
                if (Math.Abs(l - sLy) > 0.30f * dly)
                {
                    int q = px[(y + 4) * W + x];
                    float l2 = 0.299f * ((q >> 16) & 0xFF) + 0.587f * ((q >> 8) & 0xFF) + 0.114f * (q & 0xFF);
                    if (Math.Abs(l2 - sLy) > 0.27f * dly) { if (onN < cap) { onX[onN] = x; onY[onN] = y; onN++; } }
                    break;
                }
            }
        }
        ninl = onN;
        if (onN < 25) return false;                        // not enough of the frame agrees - skip

        float[] onW = new float[onN];
        for (int i = 0; i < onN; i++) onW[i] = 3f;
        float om, ob, osig; int oinl;
        if (FitLineRobust(onX, onY, onW, onN, out om, out ob, out osig, out oinl)
            && oinl >= onN * 6 / 10 && Math.Abs(om) < 1.0f)
        { mslope = om; micept = ob; }
        else
        {
            float[] sy = new float[onN]; Array.Copy(onY, sy, onN); Array.Sort(sy);
            mslope = 0f; micept = sy[onN / 2];             // flat line at the MEDIAN row
        }
        float cy = mslope * (W / 2f) + micept;
        sky = cy / H; if (sky < 0f) sky = 0f; if (sky > 1f) sky = 1f;
        return true;
    }

    // ================= HORIZON DETECTOR v2 =====================================================
    //
    // This replaces seven competing estimators (block gradient, full-res refine, TWO separate
    // sky-onset passes, a global line search, a RANSAC fallback, a cold-start median and a
    // reference-photo override) that were arbitrated by a chain of "if (!found)". They disagreed
    // systematically - the block pass was quantised to ~43px, the onset passes were full-res - so
    // WHICH one happened to succeed decided the answer, while the estimator's noise model assumed
    // there was only one of them.
    //
    // There is now ONE method, in four stages, built the way this problem is actually solved
    // (Ettinger et al. 2002, "Towards Flight Autonomy: Vision-Based Horizon Detection for Micro Air
    // Vehicles": the horizon is the line that best SEPARATES the sky and ground regions'
    // statistics, NOT the strongest edge - 30Hz, >99.9% correct over roads, buildings, meadows,
    // woods and water).
    //
    //   STAGE 1 - DOWNSAMPLE once and build luminance, blueness (B-R) and texture maps. The
    //     downsample IS the blur, so there is no separate coarse pass and full-res refinement.
    //
    //   STAGE 2 - SKY MODEL. A TEMPORAL colour model of the sky, learned from the pixels ABOVE
    //     the line, and only while the region above is genuinely the smoother one. The old code
    //     re-read "the top 6% of THIS frame" on every frame, so the moment the drone pitched down
    //     the sky reference became GROUND, the "first row that leaves the sky" fired at once, and
    //     the line snapped to the top of the screen. That is the "it works, then it gets lost".
    //     A model does not care where the camera is pointing.
    //
    //   STAGE 3 - ONE GLOBAL SCORER. The horizon is the VANISHING LINE of the ground plane, so it
    //     is parameterised the way that geometry actually is: an inclination (which IS the camera
    //     roll) plus a PERPENDICULAR offset from the principal point (which IS f*tan(pitch)). For
    //     each angle the pixels are projected onto the line's normal and histogrammed, so every
    //     candidate offset costs O(1) - the whole frame is scored at every angle in one pass. The
    //     score is the classic sky/ground separation, multiplied by three physical requirements:
    //     the region above must match the sky model, must be SMOOTHER than the region below, and
    //     must reach the top of the frame.
    //
    //   STAGE 4 - ONSET PRECISION. The winning offset is quantised to the histogram bin, so it is
    //     sharpened with the per-column "first row that leaves the sky" - the definition of a
    //     horizon - by taking the median of those rows. The ANGLE is never re-estimated there, so
    //     this cannot become a second opinion about direction; it only moves the height.
    //
    // Downstream there is one consistent measurement: roll in degrees and a PERPENDICULAR pitch
    // offset in pixels. (The old code published the VERTICAL offset at x=W/2, which is the
    // perpendicular distance divided by cos(roll) - a 15% pitch error at 30 degrees of bank, 41%
    // at 45.)

    // ---- detector working set (downsampled) ----
    float[] _hdL, _hdBR, _hdT, _hdR, _hdG, _hdB;
    int _hdW = 0, _hdH = 0;
    const int HD_DS = 6;                  // downsample factor: 6 -> 320x180 at 1080p

    // ---- STAGE 2: the temporal sky model ----
    float _skyMuL = 0f, _skyMuB = 0f;     // sky colour model: luminance, blueness (B-R)
    float _skySdL = 30f, _skySdB = 30f;   // how tight that model is
    bool _skyModelOk = false;
    float _hudSkyTex = 8f;                // dial "sky tex": how much smoother the region above must be

    // ---- last accepted line, NORMAL FORM ----
    // theta = inclination in degrees (this IS the camera roll); m = tan(theta); k = signed
    // PERPENDICULAR distance from the frame centre in working pixels, k > 0 = line ABOVE centre.
    float _hnTheta = 0f, _hnM = 0f, _hnK = 0f;
    bool _hnOk = false;

    // STAGE 1. One downsample, six maps. Every stage below reads these, so no two stages can ever
    // be looking at different pictures - which is exactly what the old version could not promise.
    bool HudBuildMaps(int[] px, int W, int H)
    {
        int dw = W / HD_DS, dh = H / HD_DS;
        if (dw < 40 || dh < 24) return false;
        int n = dw * dh;
        if (_hdL == null || _hdW != dw || _hdH != dh)
        {
            _hdL = new float[n]; _hdBR = new float[n]; _hdT = new float[n];
            _hdR = new float[n]; _hdG = new float[n]; _hdB = new float[n];
            _hdW = dw; _hdH = dh;
        }
        for (int gy = 0; gy < dh; gy++)
            for (int gx = 0; gx < dw; gx++)
            {
                int srr = 0, sgg = 0, sbb = 0, c = 0, td = 0, tc = 0;
                int y1 = gy * HD_DS + HD_DS; if (y1 > H) y1 = H;
                int x1 = gx * HD_DS + HD_DS; if (x1 > W) x1 = W;
                for (int y = gy * HD_DS; y < y1; y++)
                {
                    int prev = -1, row = y * W;
                    for (int x = gx * HD_DS; x < x1; x++)
                    {
                        int p = px[row + x];
                        int cr = (p >> 16) & 0xFF, cg = (p >> 8) & 0xFF, cb = p & 0xFF;
                        srr += cr; sgg += cg; sbb += cb; c++;
                        int l = (cr * 299 + cg * 587 + cb * 114) / 1000;
                        if (prev >= 0) { int d = l - prev; td += d < 0 ? -d : d; tc++; }
                        prev = l;
                    }
                }
                if (c == 0) c = 1;
                float fr = srr / (float)c, fg = sgg / (float)c, fb = sbb / (float)c;
                int i = gy * dw + gx;
                _hdR[i] = fr; _hdG[i] = fg; _hdB[i] = fb;
                _hdL[i] = 0.299f * fr + 0.587f * fg + 0.114f * fb;
                _hdBR[i] = fb - fr;
                _hdT[i] = tc > 0 ? td / (float)tc : 0f;     // mean |dL/dx|: sky smooth, ground busy
            }
        return true;
    }

    // STAGE 2. Learn the sky. Two guards, both load-bearing:
    //   - only pixels with a margin either side of the line are used, so the transition itself
    //     never contaminates either region;
    //   - the region ABOVE must be the SMOOTHER one, so however the camera is pointed, ground can
    //     never be learned as sky. That single test is what makes the model survive a pitch-down.
    void HudLearnSky(float m, float k, float norm)
    {
        int dw = _hdW, dh = _hdH;
        double sl = 0, sb = 0, st = 0, sl2 = 0, sb2 = 0, gt = 0;
        int nA = 0, nB = 0;
        float cx = dw * 0.5f, cy = dh * 0.5f;
        for (int y = 0; y < dh; y++)
        {
            float yy = y - cy;
            int row = y * dw;
            for (int x = 0; x < dw; x++)
            {
                float s = ((x - cx) * m - yy) / norm;
                int i = row + x;
                if (s > k + 2f)
                {
                    sl += _hdL[i]; sb += _hdBR[i]; st += _hdT[i];
                    sl2 += _hdL[i] * _hdL[i]; sb2 += _hdBR[i] * _hdBR[i]; nA++;
                }
                else if (s < k - 2f) { gt += _hdT[i]; nB++; }
            }
        }
        if (nA < 60 || nB < 60) return;
        float muL = (float)(sl / nA), muB = (float)(sb / nA), muT = (float)(st / nA);
        float vL = (float)(sl2 / nA - (sl / nA) * (sl / nA));
        float vB = (float)(sb2 / nA - (sb / nA) * (sb / nA));
        float gTex = (float)(gt / nB);
        if (muT > gTex * 0.9f) return;                    // the "sky" is the busy part - it is not sky
        if (vL < 4f) vL = 4f;
        if (vB < 4f) vB = 4f;
        float kap = _skyModelOk ? 0.10f : 1f;
        if (!_skyModelOk) { _skyMuL = muL; _skyMuB = muB; }
        else { _skyMuL += (muL - _skyMuL) * kap; _skyMuB += (muB - _skyMuB) * kap; }
        _skySdL += ((float)Math.Sqrt(vL) - _skySdL) * kap;
        _skySdB += ((float)Math.Sqrt(vB) - _skySdB) * kap;
        if (_skySdL < 6f) _skySdL = 6f;
        if (_skySdB < 6f) _skySdB = 6f;
        _skyModelOk = true;
    }

    // Start a FRESH physics recording. A flight is the natural unit of data: stitching several
    // flights into one file mixes different terrain, loadout and wind, which makes the fit unstable
    // (it did exactly that). Called automatically when the drone is deployed.
    void PhysLogReset(string why)
    {
        try
        {
            _physT0 = Environment.TickCount; _physAt = 0;
            File.WriteAllText(Path.Combine(_appDir, "hudphys.csv"),
                "t_ms,sx,sy,lx,ly,vision,detRoll,detPitch,conf,fuseRoll,fusePitch,mRoll,mPitch,modelRollRate,modelPitchRate,rollGain,pitchGain,alt,agl\r\n");
            Log("physics log: fresh recording for this flight (" + why + ")");
        }
        catch { }
    }

    void PhysLogLine()
    {
        try
        {
            long tn = Environment.TickCount;
            if (tn - _physAt < 18) return;              // ~50 Hz
            _physAt = tn;
            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder b = new StringBuilder();
            bool fresh = _hudDetValid && (tn - _hudDetAt) < 2000;
            b.Append((tn - _physT0).ToString(ci)).Append(',')
             .Append(_lastSxS.ToString("0.000", ci)).Append(',')
             .Append(_lastSyS.ToString("0.000", ci)).Append(',')
             .Append(_padLx.ToString("0.000", ci)).Append(',')
             .Append(_padLy.ToString("0.000", ci)).Append(',')
             .Append(fresh ? "1" : "0").Append(',')
             .Append(_hudDetRoll.ToString("0.00", ci)).Append(',')
             .Append(_hudDetPitch.ToString("0.0", ci)).Append(',')
             .Append(_hudDetConf.ToString("0.000", ci)).Append(',')
             .Append(_hudFRoll.ToString("0.00", ci)).Append(',')
             .Append(_hudFPitch.ToString("0.0", ci)).Append(',')
             .Append(_hudMRoll.ToString("0.00", ci)).Append(',')
             .Append(_hudMPitch.ToString("0.0", ci)).Append(',')
             .Append(_kfRollV.ToString("0.00", ci)).Append(',')
             .Append(_kfPitV.ToString("0.00", ci))
             .Append(',').Append(_kfRollGain.ToString("0.000", ci)).Append(',')
             .Append(_kfPitGain.ToString("0.000", ci))
             .Append(',').Append((_hudAlt ?? "").Replace(",", "")).Append(',')
             .Append((_hudAgl ?? "").Replace(",", ""))
             .Append("\r\n");
            File.AppendAllText(Path.Combine(_appDir, "hudphys.csv"), b.ToString());
        }
        catch { }
    }

    // ONE place that publishes a measurement, so there is a single definition of what the detector
    // hands the estimator. roll = degrees; pitch = PERPENDICULAR pixels from centre, + = below.
    void HudPublish(float roll, float pitch, float skyFrac, float conf, float pitchVar, float rollVar)
    {
        float mw = conf;
        if (!_hudSmSeeded)
        {
            _hudSmSeeded = true; _hudSmRoll = roll; _hudSmPitch = pitch;
            _hudW = mw; _hudSRoll = mw * roll; _hudSPitch = mw * pitch;
        }
        else
        {
            float decay = 0.75f;                        // ~4-frame memory
            _hudW = _hudW * decay + mw;
            _hudSRoll = _hudSRoll * decay + mw * roll;
            _hudSPitch = _hudSPitch * decay + mw * pitch;
            if (_hudW > 1e-4f) { _hudSmRoll = _hudSRoll / _hudW; _hudSmPitch = _hudSPitch / _hudW; }
        }
        _hudDetRoll = _hudSmRoll; _hudDetPitch = _hudSmPitch; _hudDetConf = conf;
        _hudMRoll = roll; _hudMPitch = pitch; _hudMConf = conf;
        _hudMRollVar = rollVar; _hudMPitchVar = pitchVar;
        _hudDetSky = skyFrac < 0f ? 0f : (skyFrac > 1f ? 1f : skyFrac);
    }

    void DetectHorizon()
    {
        try
        {
            // NOT-USEFUL FRAMES: a menu / loading screen has no horizon in it, and feeding one to the
            // estimator poisons it. Cached OCR only - this never triggers a fresh (slow) OCR.
            if (MenuishFrame())
            {
                _hudDetValid = false;
                if (Environment.TickCount - _hudLogAt >= 2000)
                { _hudLogAt = Environment.TickCount; Log("horizon det: menu/loading frame (username or Joining on screen) - discarded"); }
                return;
            }

            int W, H; int[] px = Grab(out W, out H);
            _hudFrameH = H;
            if (!HudBuildMaps(px, W, H)) { _hudDetValid = false; return; }
            int dw = _hdW, dh = _hdH, N = dw * dh;
            float cx = dw * 0.5f, cy = dh * 0.5f;

            // ---- FRAME QUALITY: a frame that is one flat tone has no horizon in it --------------
            {
                double m1 = 0, m2 = 0;
                for (int i = 0; i < N; i++) { float l = _hdL[i]; m1 += l; m2 += l * l; }
                m1 /= N; m2 /= N;
                float sd = (float)Math.Sqrt(Math.Max(0.0, m2 - m1 * m1));
                if (sd < _hudMinSpread)
                {
                    _hudDetValid = false;
                    if (Environment.TickCount - _hudLogAt >= 2000)
                    { _hudLogAt = Environment.TickCount; Log("horizon det: flat frame (spread " + sd.ToString("0") + ") - skipped"); }
                    return;
                }
            }

            // ---- COLD START: the plain median of the image until the flight settles -------------
            if (Environment.TickCount < _hudColdUntil)
            {
                float cSl, cIc, cSky; int cN;
                if (MedianHorizonLine(px, W, H, out cSl, out cIc, out cSky, out cN)
                    && cSky > 0.05f && cSky < 0.95f)
                {
                    float cNorm = (float)Math.Sqrt(1f + cSl * cSl);
                    float cRoll = (float)(Math.Atan(cSl) * 180.0 / Math.PI);
                    float cPitch = ((cSl * (W * 0.5f) + cIc) - H * 0.5f) / cNorm;   // PERPENDICULAR
                    HudPublish(cRoll, cPitch, cSky, 0.55f, 400f, 4f);
                    _hnOk = true; _hnTheta = cRoll; _hnM = cSl; _hnK = -cPitch / HD_DS;
                    HudLearnSky(cSl, _hnK, cNorm);
                    if (!_hudColdLogged || Environment.TickCount - _hudLogAt >= 2000)
                    {
                        _hudLogAt = Environment.TickCount; _hudColdLogged = true;
                        Log("horizon det: COLD START median line - roll " + cRoll.ToString("0") + " deg, sky " +
                            (cSky * 100f).ToString("0") + "%, " + cN + " onset columns");
                    }
                    return;
                }
                // no usable median this frame -> fall through to the full scorer
            }

            // ---- STAGE 2: keep the sky model current, from the LAST ACCEPTED line --------------
            if (_hnOk) HudLearnSky(_hnM, _hnK, (float)Math.Sqrt(1f + _hnM * _hnM));

            // ---- STAGE 3: the one global scorer -------------------------------------------------
            // For each angle, project every pixel onto the line's normal and histogram it. Every
            // candidate offset is then just a split of that histogram, so the whole frame is scored
            // at every angle in one pass.
            const int NTH = 25;
            const int NBI = 400;
            const float TH0 = -45f, TH1 = 45f, KSTEP = 1.5f;
            float[] thScore = new float[NTH], thK = new float[NTH], thSky = new float[NTH];
            float[] thAL = new float[NTH], thBL = new float[NTH];
            float[] thMul = new float[NTH];

            int[] hn = new int[NBI], ht = new int[NBI];
            double[] hsL = new double[NBI], hsB = new double[NBI], hsT = new double[NBI];
            double[] hsL2 = new double[NBI], hsB2 = new double[NBI];

            float axW = _hudAxisW;        // dial "axis weight": how hard blueness pulls the separation
            float texNeed = _hudSkyTex;   // dial "sky tex"
            float texW = _hudTexW;        // dial "texture": 0 = ignore the smoothness cue
            if (texNeed < 1f) texNeed = 1f;
            int minN = (int)(N * 0.04f);  // each region must be at least 4% of the frame
            float bestScore = -1f, bestK = 0f; int bestTh = -1;
            float bestTmL = 1f, bestMmL = 1f, bestTopL = 1f;

            for (int t = 0; t < NTH; t++)
            {
                float thDeg = TH0 + (TH1 - TH0) * t / (NTH - 1f);
                float m = (float)Math.Tan(thDeg * Math.PI / 180.0);
                float norm = (float)Math.Sqrt(1f + m * m);
                float s1 = ((0f - cx) * m - (0f - cy)) / norm;
                float s2 = ((dw - 1f - cx) * m - (0f - cy)) / norm;
                float s3 = ((0f - cx) * m - (dh - 1f - cy)) / norm;
                float s4 = ((dw - 1f - cx) * m - (dh - 1f - cy)) / norm;
                float smin = Math.Min(Math.Min(s1, s2), Math.Min(s3, s4));
                float smax = Math.Max(Math.Max(s1, s2), Math.Max(s3, s4));
                int nb = (int)((smax - smin) / KSTEP) + 2;
                if (nb > NBI) nb = NBI;
                Array.Clear(hn, 0, nb); Array.Clear(ht, 0, nb);
                Array.Clear(hsL, 0, nb); Array.Clear(hsB, 0, nb); Array.Clear(hsT, 0, nb);
                Array.Clear(hsL2, 0, nb); Array.Clear(hsB2, 0, nb);

                for (int y = 0; y < dh; y++)
                {
                    float yy = y - cy;
                    int row = y * dw;
                    for (int x = 0; x < dw; x++)
                    {
                        float s = ((x - cx) * m - yy) / norm;
                        int b = (int)((s - smin) / KSTEP);
                        if (b < 0) b = 0; if (b >= nb) b = nb - 1;
                        int i = row + x;
                        hn[b]++; hsL[b] += _hdL[i]; hsB[b] += _hdBR[i]; hsT[b] += _hdT[i];
                        hsL2[b] += _hdL[i] * _hdL[i]; hsB2[b] += _hdBR[i] * _hdBR[i];
                        if (y == 0) ht[b]++;                      // the top row, for the "reaches the top" test
                    }
                }

                double totL = 0, totB = 0, totT = 0, totL2 = 0, totB2 = 0;
                for (int b = 0; b < nb; b++)
                { totL += hsL[b]; totB += hsB[b]; totT += hsT[b]; totL2 += hsL2[b]; totB2 += hsB2[b]; }

                double an = 0, aL = 0, aB = 0, aT = 0, aL2 = 0, aB2 = 0, aTop = 0;
                float bScore = -1f; int bIdx = -1;
                for (int b = nb - 1; b >= 1; b--)
                {
                    an += hn[b]; aL += hsL[b]; aB += hsB[b]; aT += hsT[b];
                    aL2 += hsL2[b]; aB2 += hsB2[b]; aTop += ht[b];
                    int nA = (int)an, nB = N - nA;   // nA is the region ABOVE the line: the sky side
                    if (nA < minN || nB < minN) continue;

                    double muAL = aL / nA, muBL = (totL - aL) / nB;
                    double muAB = aB / nA, muBB = (totB - aB) / nB;
                    double vAL = aL2 / nA - muAL * muAL; if (vAL < 0) vAL = 0;
                    double vBL = (totL2 - aL2) / nB - muBL * muBL; if (vBL < 0) vBL = 0;
                    double vAB = aB2 / nA - muAB * muAB; if (vAB < 0) vAB = 0;
                    double vBB = (totB2 - aB2) / nB - muBB * muBB; if (vBB < 0) vBB = 0;
                    double dL = muAL - muBL, dB = muAB - muBB;
                    double pooled = 0.5 * (vAL + vBL) + 1.0 + axW * (0.5 * (vAB + vBB));
                    double fisher = (dL * dL + axW * dB * dB) / pooled;

                    // the region ABOVE must be the SMOOTHER one (sky is flat, ground is busy)
                    double aTx = aT / nA, bTx = (totT - aT) / nB;
                    float tm = Smooth01((float)(bTx - aTx), 0f, texNeed);
                    tm = 1f - texW * (1f - tm);
                    if (tm < 0f) tm = 0f;
                    // the region ABOVE must look like the sky we have learned
                    float mm = 1f;
                    if (_skyModelOk)
                    {
                        float dA = (float)Math.Abs(muAL - _skyMuL) / Math.Max(_skySdL, 18f) + (float)Math.Abs(muAB - _skyMuB) / Math.Max(_skySdB, 14f);
                        float dBm = (float)Math.Abs(muBL - _skyMuL) / Math.Max(_skySdL, 18f) + (float)Math.Abs(muBB - _skyMuB) / Math.Max(_skySdB, 14f);
                        mm = 0.5f + 0.5f * Smooth01(dBm - dA, 0f, 1.2f);   // one global sky colour is too tight for a sky that changes with heading - a preference, never a veto
                    }
                    // and the region ABOVE must REACH THE TOP of the frame
                    float topFrac = (float)(aTop / dw);
                    float tpm = Smooth01(topFrac, 0.45f, 0.90f);

                    // TEMPORAL PRIOR: prefer to stay on the edge we were already tracking.
                    // Without it the detector RE-LOCKS between different terrain edges on busy
                    // ground and the measurement jumps ~200 px between frames - which is
                    // physically impossible (a 2000 px/s slew moves 20 px in 10 ms). Those jumps
                    // are why the pitch measurement is useless on a high, cluttered horizon, and
                    // why the horizon "messes up a lot" when flying forward. The tolerance grows
                    // while we have had no accepted frame, so a genuine fast move still gets
                    // through after a coast.
                    float prior = 1f;
                    if (_hnOk)
                    {
                        float since = (Environment.TickCount - _hudDetAt) / 1000f;
                        if (since < 0f) since = 0f; if (since > 2f) since = 2f;
                        float sig = 45f + 260f * since;
                        float kCand = smin + (b + 0.5f) * KSTEP;
                        float dk = kCand - _hnK;
                        prior = 1f / (1f + (dk * dk) / (sig * sig));
                    }
                    float sc = (float)fisher * tm * mm * tpm * (0.3f + 0.7f * prior);
                    if (sc >= bScore) { bScore = sc; bIdx = b; bestTmL = tm; bestMmL = mm; bestTopL = tpm; }
                }
                if (bIdx < 0) { thScore[t] = 0f; continue; }

                thScore[t] = bScore;
                thK[t] = smin + bIdx * KSTEP;
                thMul[t] = bestTmL * bestMmL * bestTopL;
                double n2 = 0, l2 = 0, b2 = 0;
                for (int b = bIdx; b < nb; b++) { n2 += hn[b]; l2 += hsL[b]; b2 += hsB[b]; }
                if (n2 < 1) n2 = 1;
                thAL[t] = (float)(l2 / n2);
                thBL[t] = (float)((totL - l2) / Math.Max(1.0, N - n2));
                thSky[t] = (float)(n2 / N);
                if (bScore > bestScore) { bestScore = bScore; bestK = thK[t]; bestTh = t; }
            }

            if (bestTh < 0 || bestScore <= 0f)
            {
                _hudDetValid = false;
                if (Environment.TickCount - _hudLogAt >= 2000)
                { _hudLogAt = Environment.TickCount; Log("horizon det: no separating line in this frame (all ground or all sky) - coasting"); }
                return;
            }

            // ---- angle precision: parabolic interpolation across the angle scores ----------------
            float theta = TH0 + (TH1 - TH0) * bestTh / (NTH - 1f);
            if (bestTh > 0 && bestTh < NTH - 1)
            {
                float a = thScore[bestTh - 1], b = thScore[bestTh], c = thScore[bestTh + 1];
                float den = a - 2f * b + c;
                if (Math.Abs(den) > 1e-6f)
                {
                    float frac = 0.5f * (a - c) / den;                   // in units of one angle step
                    if (frac > 0.9f) frac = 0.9f; if (frac < -0.9f) frac = -0.9f;
                    theta += frac * (TH1 - TH0) / (NTH - 1f);
                }
            }
            // how much better is this angle than the next best ANGLE (not the next bin)
            float second = 0f;
            for (int t = 0; t < NTH; t++) if (Math.Abs(t - bestTh) >= 2 && thScore[t] > second) second = thScore[t];

            float mFin = (float)Math.Tan(theta * Math.PI / 180.0);
            float normF = (float)Math.Sqrt(1f + mFin * mFin);
            float kHist = bestK;

            // ---- STAGE 4: onset precision on the HEIGHT only --------------------------------------
            // The winning offset is quantised to a bin, so sharpen it with the per-column "first row
            // that leaves the sky" - the definition of a horizon. The reference is the frame's OWN
            // above-region mean, not the top band, so this does not care where the camera points.
            float kFinal = kHist, onsetSd = 6f; int onN = 0;
            {
                float aRef = thAL[bestTh], bRef = thBL[bestTh];
                float spreadL = Math.Abs(bRef - aRef);
                if (spreadL > 8f)
                {
                    float[] oks = new float[dw];
                    for (int x = 1; x < dw - 1; x++)
                    {
                        for (int y = 1; y < dh - 3; y++)
                        {
                            float l = _hdL[y * dw + x];
                            if (Math.Abs(l - aRef) > 0.30f * spreadL)
                            {
                                float l2 = _hdL[(y + 2) * dw + x];
                                if (Math.Abs(l2 - aRef) > 0.27f * spreadL)
                                    oks[onN++] = ((x - cx) * mFin - (y - cy)) / normF;
                                break;
                            }
                        }
                    }
                    if (onN >= 24)
                    {
                        float[] tmp = new float[onN]; Array.Copy(oks, tmp, onN); Array.Sort(tmp);
                        float med = tmp[onN / 2];
                        double acc = 0; int cc = 0;
                        for (int i = 0; i < onN; i++) if (Math.Abs(oks[i] - med) <= 12f) { acc += oks[i]; cc++; }
                        if (cc >= 16)
                        {
                            float rm = (float)(acc / cc);
                            if (Math.Abs(rm - kHist) < dh * 0.25f)
                            {
                                kFinal = rm;
                                double v = 0;
                                for (int i = 0; i < onN; i++) if (Math.Abs(oks[i] - med) <= 12f) v += (oks[i] - rm) * (oks[i] - rm);
                                onsetSd = cc > 1 ? (float)Math.Sqrt(v / (cc - 1)) : 6f;
                            }
                        }
                    }
                }
            }

            // ---- measurement ---------------------------------------------------------------------
            float pitchPerp = -kFinal * HD_DS;          // PERPENDICULAR px, + = line below centre
            int skyN = 0;
            for (int y = 0; y < dh; y++)
            {
                float yy = y - cy; int row = y * dw;
                for (int x = 0; x < dw; x++)
                    if (((x - cx) * mFin - yy) / normF > kFinal) skyN++;
            }
            float skyFrac2 = skyN / (float)N;

            float margin = bestScore > 1e-3f ? (bestScore - second) / bestScore : 1f;
            float conf = Smooth01(margin, 0.03f, 0.25f) * thMul[bestTh] * Smooth01(bestScore, 0.3f, 2f);
            if (conf < 0.05f) conf = 0.05f; if (conf > 1f) conf = 1f;

            // clutter: how busy it is right around the line (trees/structures drag a lock up)
            {
                double cs = 0; int cn2 = 0;
                for (int y = 0; y < dh; y++)
                {
                    float yy = y - cy; int row = y * dw;
                    for (int x = 0; x < dw; x++)
                    {
                        float s = ((x - cx) * mFin - yy) / normF;
                        if (Math.Abs(s - kFinal) <= 2f) { cs += _hdT[row + x]; cn2++; }
                    }
                }
                _hudClutter = cn2 > 0 ? Smooth01((float)(cs / cn2), 3f, 22f) : 0f;
            }

            // ---- THE GATE: no verified sky in the frame means no measurement ----------------------
            // NO REAL SEPARATION, NO MEASUREMENT. A frame where every candidate line scores near
            // zero has no sky/ground edge in it at all (all ground, or all sky). Publishing it hands
            // the filter a line that LOOKS valid, and the estimator then yanks hard on it even though
            // the confidence is low - because the outlier gate OPENS UP on the huge variance that
            // comes with it. That is the "it gets lost". Coast instead.
            if (bestScore < 0.5f || (bestScore < 2.5f && (skyFrac2 > 0.90f || skyFrac2 < 0.05f)))
            {
                _hudDetValid = false;
                _hudNoSkyFrames++;
                if (Environment.TickCount - _hudLogAt >= 2000)
                { _hudLogAt = Environment.TickCount; Log("horizon det: no usable sky/ground separation (sep " + bestScore.ToString("0.0") + ") - coasting"); }
                return;
            }
            if (skyFrac2 < _hudSkyMin)
            {
                _hudDetValid = false;
                _hudNoSkyFrames++;
                if (Environment.TickCount - _hudLogAt >= 2000)
                {
                    _hudLogAt = Environment.TickCount;
                    Log("horizon det: frame DISCARDED - only " + (skyFrac2 * 100f).ToString("0") +
                        "% verified sky (min " + (_hudSkyMin * 100f).ToString("0") + "%), coasting on stick model");
                }
                return;
            }

            // ---- publish -------------------------------------------------------------------------
            float pVar = onsetSd * onsetSd * HD_DS * HD_DS / Math.Max(1, onN)
                       + (KSTEP * HD_DS) * (KSTEP * HD_DS) * 0.25f + 4f;
            if (pVar > 4000f) pVar = 4000f;
            float rsd = 0.4f + 4f * (1f - conf);
            HudPublish(theta, pitchPerp, skyFrac2, conf, pVar, rsd * rsd);

            // remember it in NORMAL FORM - this is what the sky model learns from next frame
            _hnOk = true; _hnTheta = theta; _hnM = mFin; _hnK = kFinal;
            _hudTrkM = mFin; _hudTrkB = cy - mFin * cx - normF * kFinal;
            _hudTrkAt = Environment.TickCount;

            // scene classifier (informational) + the "trained on your photos" override
            if (_hudSceneTick++ % 20 == 0)
            {
                float sd2; int mi = MatchMap(SigFromBlocks(_hdR, _hdG, _hdB, dw, dh), out sd2);
                _hudSceneD = sd2;
                _hudScene = (mi >= 0 && sd2 <= _hudSceneTol) ? MapNames[mi] : "normal";
            }
            bool refHit = false;
            if (!_refLoaded) LoadHudRefs();
            float refD = 9f; bool refGood = true;
            if (_refSigs.Count > 0)
            {
                float[] rl;
                refD = MatchRefs(SigFromBlocks(_hdR, _hdG, _hdB, dw, dh), out refGood, out rl);
                _refD = refD; _refGood = refGood; _refBestLine = rl;
                if (rl != null && refGood && refD <= _refDist * 0.6f)
                {
                    // the stored line's pitch is the old VERTICAL offset - convert to perpendicular
                    float c2 = (float)Math.Cos(rl[0] * Math.PI / 180.0);
                    if (Math.Abs(c2) < 0.2f) c2 = 0.2f;
                    HudPublish(rl[0], rl[1] / c2, skyFrac2, 0.95f, 9f, 0.25f);
                    refHit = true;
                }
            }

            _hudDetValid = true;
            _hudDetAt = Environment.TickCount;
            if (Environment.TickCount - _hudLogAt >= 2000)
            {
                _hudLogAt = Environment.TickCount;
                Log("horizon det: roll " + theta.ToString("0.0") + " deg, pitch " + pitchPerp.ToString("0") +
                    " px, sky " + (skyFrac2 * 100f).ToString("0") + "%, conf " + (conf * 100f).ToString("0") +
                    "%, sep " + bestScore.ToString("0.0") + " (next angle " + second.ToString("0.0") + "), onset " +
                    onN + ", clutter " + (_hudClutter * 100f).ToString("0") + "%, sky-model " +
                    (_skyModelOk ? "on" : "none") + ", scene " + _hudScene +
                    (refHit ? "  REF-ANSWER" : ""));
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
        readonly Dictionary<string, string> _ui = new Dictionary<string, string>();
        public void SetUi(string k, string v) { lock (_lock) { _ui[k] = v ?? ""; } }
        string _team = "", _drone = "", _bomb = "";
        bool _active = false;
        bool _flight = false;            // drone deployed - show RF static in the stream overlay
        float _level = 0f;               // static intensity, based on time since deploy
        int _secs = 0;
        bool _hud = false;               // draw the FPV/UAV HUD in the stream overlay
        string _hdg = "", _spd = "", _agl = "";
        float _roll = 0f, _pit = 0f;
        bool _uav = false;
        float _v1 = 0f, _v2 = 0f;
        float _dpp = 8f, _shear = 0.9f, _len = 1f, _rungTilt = 0f, _maxTilt = 30f, _spread = 2.2f, _spreadAccel = 0f;   // ladder geometry dials (from settings)
        float _plx = 0f, _ply = 0f, _prx = 0f, _pry = 0f;   // live stick values (diagnostics)
        float _alx = 0f, _aly = 0f, _arx = 0f, _ary = 0f;   // stick values fed by the joystick app
        long _aPadAt = 0;
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
        public void SetPad(float lx, float ly, float rx, float ry) { lock (_lock) { _plx = lx; _ply = ly; _prx = rx; _pry = ry; } }
        public void SetAppPad(float lx, float ly, float rx, float ry)
        { lock (_lock) { _alx = lx; _aly = ly; _arx = rx; _ary = ry; _aPadAt = Environment.TickCount; } }
        // The joystick app's stick values, if it is running and fresh (< 0.6s old).
        public bool AppPad(out float lx, out float ly, out float rx, out float ry)
        {
            lock (_lock)
            {
                lx = _alx; ly = _aly; rx = _arx; ry = _ary;
                return _aPadAt != 0 && Environment.TickCount - _aPadAt < 600;
            }
        }
        public void SetHud(bool on, string hdg, string spd, string agl, float roll, float pit, bool uav, float v1, float v2)
        { lock (_lock) { _hud = on; _hdg = hdg ?? ""; _spd = spd ?? ""; _agl = agl ?? ""; _roll = roll; _pit = pit; _uav = uav; _v1 = v1; _v2 = v2; } }
        public void SetDials(float dpp, float shear, float len, float rungTilt, float maxTilt, float spread, float spreadAccel)
        { lock (_lock) { _dpp = dpp; _shear = shear; _len = len; _rungTilt = rungTilt; _maxTilt = maxTilt; _spread = spread; _spreadAccel = spreadAccel; } }
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
                    sb.Append(",\"plx\":").Append(_plx.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"ply\":").Append(_ply.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"prx\":").Append(_prx.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"pry\":").Append(_pry.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"v1\":").Append(_v1.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"v2\":").Append(_v2.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"dpp\":").Append(_dpp.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"shear\":").Append(_shear.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"len\":").Append(_len.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"rungTilt\":").Append(_rungTilt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"maxTilt\":").Append(_maxTilt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"spread\":").Append(_spread.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"spreadAccel\":").Append(_spreadAccel.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    // overlay.html version = its last-write time; the page reloads itself when it changes
                    // so OBS can never sit on a stale render again
                    string oVer = "0";
                    try { string of = Path.Combine(_dir, "overlay.html"); if (File.Exists(of)) oVer = File.GetLastWriteTimeUtc(of).Ticks.ToString(); } catch { }
                    sb.Append(",\"ver\":\"").Append(oVer).Append("\"");
                    // translated static strings for the OBS overlay (title / mission header / statuses)
                    sb.Append(",\"ui\":{");
                    bool uf = true;
                    foreach (KeyValuePair<string, string> kv in _ui)
                    {
                        if (!uf) sb.Append(',');
                        uf = false;
                        sb.Append('"').Append(Esc(kv.Key)).Append("\":\"").Append(Esc(kv.Value)).Append('"');
                    }
                    sb.Append("}");
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
            else if (path == "/pad")
            {
                // the joystick app pushes its mapped stick values here (lx/ly/rx/ry) so the HUD has a
                // reliable input even when our own XInput read misses the pad
                try
                {
                    string q = c.Request.Url.Query;
                    float lx = 0f, ly = 0f, rx = 0f, ry = 0f;
                    foreach (string kv in q.TrimStart('?').Split('&'))
                    {
                        int e = kv.IndexOf('=');
                        if (e <= 0) continue;
                        string k = kv.Substring(0, e);
                        float f;
                        if (!float.TryParse(kv.Substring(e + 1), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                        if (k == "lx") lx = f; else if (k == "ly") ly = f; else if (k == "rx") rx = f; else if (k == "ry") ry = f;
                    }
                    SetAppPad(lx, ly, rx, ry);
                }
                catch { }
                body = "ok"; ctype = "text/plain";
            }
            else
            {
                string f = Path.Combine(_dir, "overlay.html");
                try
                {
                    if (File.Exists(f))
                    {
                        // stamp the page with its own version so its JS can detect a newer file and reload
                        body = File.ReadAllText(f).Replace("__OVERLAY_VER__", File.GetLastWriteTimeUtc(f).Ticks.ToString());
                    }
                    else body = "<html><body style='background:#000;color:#8cff9c;font-family:Consolas;font-size:28px'>overlay.html missing</body></html>";
                }
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
            lbl.Text = T("LAND NOW");   // translated
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
            if (resuming) OverlayHub.I.SetProgress(_flowPct, T("re-sync"));
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
                OverlayHub.I.SetProgress(_flowPct, T("standby"));

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
            string tx = T(text);   // CLI/terminal line - translated for BOTH the monitor and OBS
            OverlayHub.I.AddLine(tx, status);
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { AddConsoleLine(tx, status); }); return; }
            AddConsoleLine(tx, status);
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
        if (label != null) OverlayHub.I.SetProgress(_flowPct, T(label));
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

    // --selftest: read the CURRENT screen with the exact OCR + matcher the AUTO flow uses and write
    // every decision to selftest.log, without clicking anything. This is how a false positive like
    // "Base" on the loadout screen gets caught: the log names the word, its box and its score.
    static bool _selfTest;

    void SelfTest()
    {
        try
        {
            Thread.Sleep(800);
            List<string[]> ws = OcrWords();
            List<string[]> ww = OcrWordsWhiten();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SELFTEST " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            sb.AppendLine("ScreenName   : " + ScreenName(ws));
            sb.AppendLine("MenuOnScreen : " + MenuOnScreen());
            sb.AppendLine("MapPointLock : " + MapPointLockUp());
            sb.AppendLine("TeamBasePhraseUp : " + TeamBasePhraseUp(ws) + "   BasePanelVisual: " + BasePanelVisual());
            sb.AppendLine();
            string[] probes = { "POINT", "Base", "TEAM BASE", "WARHEAD", "DEPLOY AS DRONE", "DEPLOY", "LOADOUT", "SELECT DRONE", "Return", "PLAYERS IN TEAM", "CHANGE TEAM" };
            foreach (string pr in probes)
            {
                List<Hit> h1 = FindPhraseAll(pr, null, ws);
                List<Hit> h2 = FindPhraseAll(pr, null, ww);
                sb.AppendLine("probe \"" + pr + "\"  normal=" + h1.Count + "  whiten=" + h2.Count);
                foreach (Hit h in h1) sb.AppendLine("    normal (" + h.X + "," + h.Y + ") q=" + h.Quality + " area=" + h.Area + " InPanel=" + InPanel(h.X, h.Y));
                foreach (Hit h in h2) sb.AppendLine("    whiten (" + h.X + "," + h.Y + ") q=" + h.Quality + " area=" + h.Area + " InPanel=" + InPanel(h.X, h.Y));
            }
            sb.AppendLine();
            sb.AppendLine("--- every on-screen word that fuzzy-matches a case word (this is WHAT matched) ---");
            string[] cases = { "base", "point", "return", "warhead", "deploy", "drone", "team" };
            foreach (string[] w in ws)
                foreach (string c in cases)
                    if (TokEq(w[4], c))
                        sb.AppendLine("    normal \"" + w[4] + "\" ~ \"" + c + "\"  @ " + w[0] + "," + w[1] + " " + w[2] + "x" + w[3] + "  sim=" + SimPct(w[4], c) + "%  InPanel=" + InPanel(int.Parse(w[0]), int.Parse(w[1])));
            foreach (string[] w in ww)
                foreach (string c in cases)
                    if (TokEq(w[4], c))
                        sb.AppendLine("    whiten \"" + w[4] + "\" ~ \"" + c + "\"  @ " + w[0] + "," + w[1] + " " + w[2] + "x" + w[3] + "  sim=" + SimPct(w[4], c) + "%  InPanel=" + InPanel(int.Parse(w[0]), int.Parse(w[1])));
            sb.AppendLine();
            sb.AppendLine("--- normal OCR words (" + ws.Count + ") ---");
            foreach (string[] w in ws) sb.AppendLine("    \"" + w[4] + "\"  @ " + w[0] + "," + w[1] + " " + w[2] + "x" + w[3]);
            sb.AppendLine("--- whiten OCR words (" + ww.Count + ") ---");
            foreach (string[] w in ww) sb.AppendLine("    \"" + w[4] + "\"  @ " + w[0] + "," + w[1] + " " + w[2] + "x" + w[3]);
            File.WriteAllText(Path.Combine(_appDir, "selftest.log"), sb.ToString());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(_appDir, "selftest.log"), "SELFTEST ERROR: " + ex); } catch { }
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // A stray exception in any timer tick used to pop the ".NET Framework - Unhandled exception"
        // dialog and take the whole tool down (e.g. Math.Abs on a full-deflection stick). Log it and
        // keep running instead - one bad frame must never freeze the HUD or kill the app.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
        {
            SafeLog("WARNING: recovered from a UI error: " + e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
        {
            SafeLog("WARNING: recovered from an error: " + e.ExceptionObject);
        };
        if (args.Length > 0 && args[0] == "--selftest")
        {
            _selfTest = true;
            RobloxAuto st = new RobloxAuto();
            st.SelfTest();
            return;
        }
        // --click x y : one real click through the normal click path (focus + human move), for
        // driving the game from a terminal while testing.
        if (args.Length > 2 && args[0] == "--click")
        {
            _selfTest = true;
            RobloxAuto st = new RobloxAuto();
            FocusRoblox(); Thread.Sleep(300);
            st.ClickLogged(int.Parse(args[1]), int.Parse(args[2]), "test click");
            Thread.Sleep(500);
            return;
        }
        // --wheel n : focus, drift over the game like the map step does, then one wheel burst.
        // Logs whether the screen actually changed, so "the wheel did nothing" is never a guess.
        // --wheel 0 is the control: it does everything except the wheel.
        if (args.Length > 1 && args[0] == "--wheel")
        {
            _selfTest = true;
            RobloxAuto st = new RobloxAuto();
            int n = int.Parse(args[1]);
            FocusRoblox(); Thread.Sleep(250);
            st.MoveOverGameSoft(1); Thread.Sleep(250);
            st.MoveOverGameSoft(3); Thread.Sleep(250);   // a second, different point: guarantees real motion
            int[] sig1 = Signature();
            ScrollIn(n);
            Thread.Sleep(500);
            int[] sig2 = Signature();
            IntPtr rw = RobloxWindow();
            st.Log("test wheel " + n + ": screenchange=" + DiffPct(sig1, sig2).ToString("0.0") + "%  fg=" +
                   (GetForegroundWindow() == rw ? "roblox" : "OTHER") + "  cursor=" + Cursor.Position.X + "," + Cursor.Position.Y);
            return;
        }
        // --auto : run the real AUTO flow exactly as the button/pad trigger does, then exit.
        // Lets a test run the whole thing from a terminal and read the log.
        if (args.Length > 0 && args[0] == "--auto")
        {
            _selfTest = true;
            RobloxAuto st = new RobloxAuto();
            st.AutoRun();
            int waited = 0;
            while (st._running && waited < 240000) { Thread.Sleep(500); waited += 500; }
            st.Log("test auto: finished after " + (waited / 1000.0).ToString("0.0") + "s  deployed=" + st._autoDeployed);
            return;
        }
        Application.Run(new RobloxAuto());
    }

    // main() is static, so the crash handlers cannot call the instance Log(). Append to error.log.
    static void SafeLog(string msg)
    {
        try
        {
            string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
            System.IO.File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}












