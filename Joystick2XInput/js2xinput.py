"""
js2xinput - map a Logitech Extreme 3D Pro (or any pygame joystick) to a virtual
Xbox 360 controller via ViGEm, so games that only read XInput can use it.

Requires: pygame, vgamepad   (both already installed)
Run:      python js2xinput.py            normal
          python js2xinput.py --debug    live readout of every axis/button
          python js2xinput.py --list     list joysticks and their axes

Edit jsxinput.json (created on first run) to change the mapping. The defaults
are set up for the Warfare MAVIC: stick = roll/pitch, twist = yaw, throttle = up/down.
"""

import os
import sys
import json
import time

import pygame
import vgamepad as vg

HERE = os.path.dirname(os.path.abspath(__file__))
CFG_PATH = os.path.join(HERE, "jsxinput.json")

# target axis names understood by the mapper -> (vgamepad setter)
#   lx, ly = left stick X/Y   rx, ry = right stick X/Y   lt, rt = triggers
DEFAULT = {
    "device_name": "Extreme",   # substring to match (empty = first joystick)
    "poll_hz": 125,
    "deadzone": 0.05,
    # axis index -> {target, inv}
    # Extreme 3D Pro axes are usually: 0=X stick, 1=Y stick, 2=throttle, 3=twist (Z)
    # (twist is deliberately NOT used - it is broken on this stick)
    "axes": {
        "0": {"target": "rx", "inv": False},   # stick left/right -> right stick X (roll)
        "1": {"target": "ry", "inv": True},    # stick fwd/back   -> right stick Y (pitch)
        "2": {"target": "ly", "inv": False}    # throttle lever   -> left stick Y  (climb)
    },
    # POV hat -> axis targets, so yaw can live on the hat (twist is broken).
    # "x"/"y" each take a target ("lx","ly","rx","ry", or "" to ignore).
    "hat_axes": {"x": "lx", "y": ""},
    "hat_dpad": False,
    # button index -> Xbox button name
    "buttons": {
        "0": "A", "1": "B", "2": "X", "3": "Y",
        "4": "LEFT_SHOULDER", "5": "RIGHT_SHOULDER",
        "6": "BACK", "7": "START",
        "8": "LEFT_THUMB", "9": "RIGHT_THUMB"
    }
}

BTN = {
    "A": vg.XUSB_BUTTON.XUSB_GAMEPAD_A,
    "B": vg.XUSB_BUTTON.XUSB_GAMEPAD_B,
    "X": vg.XUSB_BUTTON.XUSB_GAMEPAD_X,
    "Y": vg.XUSB_BUTTON.XUSB_GAMEPAD_Y,
    "LEFT_SHOULDER": vg.XUSB_BUTTON.XUSB_GAMEPAD_LEFT_SHOULDER,
    "RIGHT_SHOULDER": vg.XUSB_BUTTON.XUSB_GAMEPAD_RIGHT_SHOULDER,
    "BACK": vg.XUSB_BUTTON.XUSB_GAMEPAD_BACK,
    "START": vg.XUSB_BUTTON.XUSB_GAMEPAD_START,
    "LEFT_THUMB": vg.XUSB_BUTTON.XUSB_GAMEPAD_LEFT_THUMB,
    "RIGHT_THUMB": vg.XUSB_BUTTON.XUSB_GAMEPAD_RIGHT_THUMB,
    "DPAD_UP": vg.XUSB_BUTTON.XUSB_GAMEPAD_DPAD_UP,
    "DPAD_DOWN": vg.XUSB_BUTTON.XUSB_GAMEPAD_DPAD_DOWN,
    "DPAD_LEFT": vg.XUSB_BUTTON.XUSB_GAMEPAD_DPAD_LEFT,
    "DPAD_RIGHT": vg.XUSB_BUTTON.XUSB_GAMEPAD_DPAD_RIGHT,
}


def load_cfg():
    if not os.path.exists(CFG_PATH):
        with open(CFG_PATH, "w") as f:
            json.dump(DEFAULT, f, indent=2)
        print("wrote default config -> " + CFG_PATH)
        return dict(DEFAULT)
    try:
        with open(CFG_PATH) as f:
            c = json.load(f)
        # fill in any missing keys from the defaults
        for k, v in DEFAULT.items():
            c.setdefault(k, v)
        return c
    except Exception as e:
        print("config error (" + str(e) + ") - using defaults")
        return dict(DEFAULT)


def open_joystick(cfg):
    pygame.joystick.init()
    n = pygame.joystick.get_count()
    if n == 0:
        print("no joystick found - plug in the Extreme 3D Pro")
        return None
    pick = 0
    want = (cfg.get("device_name") or "").lower()
    for i in range(n):
        j = pygame.joystick.Joystick(i)
        j.init()
        name = j.get_name()
        print("  [%d] %s  axes=%d buttons=%d hats=%d" % (i, name, j.get_numaxes(), j.get_numbuttons(), j.get_numhats()))
        if want and want in name.lower():
            pick = i
    js = pygame.joystick.Joystick(pick)
    js.init()
    print("using [%d] %s" % (pick, js.get_name()))
    return js


def dz(v, d):
    return 0.0 if abs(v) < d else v


def clamp(v, lo=-1.0, hi=1.0):
    return lo if v < lo else (hi if v > hi else v)


# ------------------------- interactive binder -------------------------

AXIS_BIND = [
    ("rx", "ROLL   - push the stick fully RIGHT and hold"),
    ("ry", "PITCH  - push the stick fully UP and hold"),
    ("lx", "YAW    - hold the twist/turn control (or tap the hat left/right)"),
    ("ly", "THROTTLE - push the throttle lever fully UP and hold"),
]

BTN_BIND = [
    ("A", "A"), ("B", "B"), ("X", "X"), ("Y", "Y"),
    ("LEFT_SHOULDER", "LB"), ("RIGHT_SHOULDER", "RB"),
    ("BACK", "Back/Select"), ("START", "Start"),
]


def capture_input(js, seconds=2.5):
    """Watch for the control the user moves. Returns ('axis', idx, sign), ('hat','x'|'y',sign) or None."""
    n = js.get_numaxes()
    base = [js.get_axis(i) for i in range(n)]
    best_i, best_d = -1, 0.0
    t0 = time.time()
    while time.time() - t0 < seconds:
        pygame.event.pump()
        for i in range(n):
            d = js.get_axis(i) - base[i]
            if abs(d) > abs(best_d):
                best_d, best_i = d, i
        if js.get_numhats() > 0:
            hx, hy = js.get_hat(0)
            if hx != 0:
                return ("hat", "x", hx)
            if hy != 0:
                return ("hat", "y", hy)
        time.sleep(0.008)
    if best_i >= 0 and abs(best_d) > 0.4:
        return ("axis", best_i, best_d)
    return None


def capture_button(js, timeout=8.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        pygame.event.pump()
        for i in range(js.get_numbuttons()):
            if js.get_button(i):
                return i
        time.sleep(0.008)
    return -1


def bind(js, cfg):
    print("")
    print("=== BIND AXES ===")
    print("Move ONE control each time. Press Ctrl+C to stop early.\n")
    for target, prompt in AXIS_BIND:
        print(prompt + "  (move/hold it now)")
        got = capture_input(js)
        if got is None:
            print("  nothing detected - skipping %s\n" % target)
            continue
        kind, idx, sign = got
        if kind == "hat":
            cfg["hat_axes"][idx] = target
            print("  hat %s -> %s\n" % (idx, target))
        else:
            cfg["axes"][str(idx)] = {"target": target, "inv": sign < 0}
            print("  axis %d -> %s  (inverted=%s)\n" % (idx, target, sign < 0))

    print("=== BIND BUTTONS ===")
    print("Press the joystick button for each prompt (skip by waiting ~8s).\n")
    for name, label in BTN_BIND:
        print("Press the button for %s ..." % label)
        b = capture_button(js)
        if b < 0:
            print("  no button - skipping %s\n" % name)
            continue
        cfg["buttons"][str(b)] = name
        print("  button %d -> %s\n" % (b, name))

    with open(CFG_PATH, "w") as f:
        json.dump(cfg, f, indent=2)
    print("saved -> " + CFG_PATH)


def main():
    debug = "--debug" in sys.argv
    if "--list" in sys.argv or debug:
        pygame.init()
        open_joystick(load_cfg())
        if "--list" in sys.argv:
            return

    first_run = not os.path.exists(CFG_PATH)
    if "--bind" in sys.argv or first_run:
        pygame.init()
        cfg = load_cfg()
        js = open_joystick(cfg)
        if js is None:
            return
        bind(js, cfg)
        pygame.quit()
        if first_run and "--bind" not in sys.argv:
            print("\nrun again to start flying (run.bat).")
            return

    cfg = load_cfg()
    axes_map = {int(k): v for k, v in cfg["axes"].items()}
    btns_map = {int(k): v for k, v in cfg["buttons"].items()}
    dead = float(cfg["deadzone"])
    hz = max(20, int(cfg["poll_hz"]))

    pygame.init()
    js = open_joystick(cfg)
    if js is None:
        return

    pad = vg.VX360Gamepad()
    print("virtual Xbox 360 controller up. Ctrl+C to quit.")

    t_last = 0.0
    while True:
        pygame.event.pump()

        # ---- axes: start centred, then apply the configured mappings ----
        lx = ly = rx = ry = 0.0
        lt = rt = 0.0
        raw = []
        for i in range(js.get_numaxes()):
            v = js.get_axis(i)
            raw.append(v)
            m = axes_map.get(i)
            if not m:
                continue
            val = -v if m.get("inv") else v
            val = dz(val, dead)
            tgt = m.get("target")
            if tgt == "lx":
                lx = val
            elif tgt == "ly":
                ly = val
            elif tgt == "rx":
                rx = val
            elif tgt == "ry":
                ry = val
            elif tgt == "lt":
                lt = clamp((val + 1.0) / 2.0, 0.0, 1.0)
            elif tgt == "rt":
                rt = clamp((val + 1.0) / 2.0, 0.0, 1.0)

        # ---- POV hat can drive an axis (e.g. yaw) since the twist is broken ----
        hx = hy = 0
        if js.get_numhats() > 0:
            hx, hy = js.get_hat(0)
            ha = cfg.get("hat_axes") or {}
            tx = (ha.get("x") or "").lower()
            ty = (ha.get("y") or "").lower()
            if tx == "lx":
                lx = float(hx)
            elif tx == "rx":
                rx = float(hx)
            elif tx == "ly":
                ly = float(hx)
            elif tx == "ry":
                ry = float(hx)
            if ty == "lx":
                lx = float(hy)
            elif ty == "rx":
                rx = float(hy)
            elif ty == "ly":
                ly = float(hy)
            elif ty == "ry":
                ry = float(hy)

        pad.left_joystick_float(x_value_float=clamp(lx), y_value_float=clamp(ly))
        pad.right_joystick_float(x_value_float=clamp(rx), y_value_float=clamp(ry))
        pad.left_trigger_float(value_float=lt)
        pad.right_trigger_float(value_float=rt)

        # ---- buttons ----
        for i in range(js.get_numbuttons()):
            name = btns_map.get(i)
            if not name:
                continue
            b = BTN.get(name.upper())
            if b is None:
                continue
            if js.get_button(i):
                pad.press_button(button=b)
            else:
                pad.release_button(button=b)

        # ---- optional: also send the hat to the D-pad ----
        if cfg.get("hat_dpad") and js.get_numhats() > 0:
            for b, on in ((BTN["DPAD_LEFT"], hx < 0), (BTN["DPAD_RIGHT"], hx > 0),
                          (BTN["DPAD_UP"], hy > 0), (BTN["DPAD_DOWN"], hy < 0)):
                if on:
                    pad.press_button(button=b)
                else:
                    pad.release_button(button=b)

        pad.update()

        if debug:
            now = time.time()
            if now - t_last > 0.4:
                t_last = now
                print("axes " + " ".join("%5.2f" % a for a in raw) +
                      "  ->  lx%5.2f ly%5.2f rx%5.2f ry%5.2f lt%4.2f rt%4.2f" % (lx, ly, rx, ry, lt, rt))

        time.sleep(1.0 / hz)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
