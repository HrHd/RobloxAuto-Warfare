js2xinput - Logitech Extreme 3D Pro -> virtual Xbox 360 controller
=================================================================

Lets you fly the MAVIC (or anything) with the flight stick in games that only
read an Xbox controller. Uses ViGEm (ViGEmBus driver - already installed).

RUN
  run.bat                 normal
  js2xinput.py --bind     re-run the button/axis binder (saves jsxinput.json)
  js2xinput.py --list     list joysticks + their axis/button counts
  js2xinput.py --debug    live readout of every axis while you move it

FIRST RUN
  It detects jsxinput.json is missing and walks you through binding:
    * move ROLL / PITCH / YAW / THROTTLE one at a time (hold ~2s each)
    * press the button you want for A / B / X / Y / LB / RB / Back / Start
  Everything is saved to jsxinput.json. Run again to fly.

  The twist on this stick is BROKEN, so YAW is bound to the POV hat by default -
  tap the hat left/right to yaw. You can rebind yaw to any axis or keep the hat.

DEFAULTS (Warfare MAVIC)
  axis 0 (stick X)  -> right stick X  (roll)
  axis 1 (stick Y)  -> right stick Y  (pitch, inverted so up = nose up)
  axis 2 (throttle) -> left stick Y   (climb / descend)
  hat left/right    -> left stick X   (yaw)

CONFIG (jsxinput.json)
  device_name   substring of the joystick name to use (default "Extreme")
  axes          { "<axis index>": { "target": "lx|ly|rx|ry|lt|rt", "inv": true/false } }
  hat_axes      { "x": "<target>", "y": "<target>" }   POV hat -> axis
  buttons       { "<button index>": "A|B|X|Y|LEFT_SHOULDER|RIGHT_SHOULDER|BACK|START|..." }
  deadzone      centre dead zone (0.05)
  poll_hz       update rate (125)

NOTE
  Your real Xbox pad still works, but games may see BOTH pads. Unplug/deselect
  the real one while flying with the stick.
