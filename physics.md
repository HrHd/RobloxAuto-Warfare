PHYSICS DATA - recorded from hudphys.csv

The drone's stick->rate physics is a CONSTANT OF THE GAME: there is no wind, no load
variation, no self-levelling (it is acro). So this is a ONE-TIME calibration - once the
rates are right the model is exact and the image can only add noise. That is why the
image influence is being reduced rather than relied on.

MODEL (settings.ini):
  hudRoll  = 100 deg/s per unit stick
  hudPitch = 2000 px/s per unit stick
  hudRollStick  = 8   (stick-derived bank, was 30 - it DOUBLE-COUNTED the real bank)
  hudPStick     = 320 (stick-derived pitch offset)

LEARNED GAINS (the model measuring itself against the horizon):
  rollGain stayed 0.95-1.02, pitchGain settled ~0.95
  => the dials are correct to within about 5%. Do NOT retune them.

EARLIER OFFLINE FITS (57 deg/s, ~400 px/s) WERE WRONG - they were corrupted by stale
repeated samples and by the detector re-locking between terrain edges. Ignore them.

BUGS FOUND AND FIXED WHILE COLLECTING THIS (all made the model look wrong when it was not):
  1. the rate learner's timestamp was refreshed on every call, so its time window was
     unreachable and it never learned anything (gains stuck at exactly 1.000)
  2. the learner's two conditions were mutually exclusive - it needed |stick| >= 0.30,
     but 'trust' contains (1 - 0.95*actT) which that same stick had already zeroed
  3. the model rate was BLED TO ZERO whenever the vision was missing - that is
     angle/self-levelling behaviour, not acro, so a held stick stopped moving the line
  4. one measurement was reused ~100 times at 50 Hz, pinning the line to a stale reading
  5. the outlier gate was too narrow after a coast, so a diverged estimate could never
     be corrected again (it rejected every correction)
  6. the gain collapsed to ~0.008 (no floor), so a big honest error was corrected at
     34 px/s against a 1300 px error

MEASURED AFTER THOSE FIXES:
  tracking error median 195 px, 90th 624 px  <- still too loose, image is pulling it
  vision coverage 41% of samples

NEXT: image authority reduced (gain ceiling 0.50 -> 0.15) so the model carries the
attitude and the image only trims it. Large errors still re-acquire decisively.

VIDEO CROSS-CHECK - 2026-09-26 11-33-53.mp4 (segment t=100.2..107.3s), added 2026-09-29:
  - The file is 1920x1080@60 in the container, but the CONTENT is ~20-30 fps: frames
    duplicate in 2-3 blocks. 60 fps extraction therefore adds NO temporal detail; any
    "rate" computed from a single 1/60 step is inflated up to 3x. Read rates only across
    the duplicate blocks (as done below).
  - Where the good footage is: the clip is mostly static document/menu screens; real
    flight is t~100.2-107.3s and t~107.5-116s (scene cuts at 100.2, 106.5, 107.5, 109.4,
    110.6, 111.x, 114.5, 115.3). Best single window: t=100.2-101.2s (active roll sweep
    10deg -> -6deg with pitch 47 -> -312 px).
  - Block-deconvolved rates measured: sustained pitch ~500 px/s (gentle) up to ~1280 px/s
    (flick); roll ~12..40 deg/s in the same windows; isolated roll flicks up to ~80-200
    deg/s (inflated by the duplicate frames - treat as upper bound).
  - Rate changes take effect within one content frame (~30-50 ms): direct rate command
    with no spin-up ramp and no self-levelling. This matches the acro calibration above
    (hudRoll=100 deg/s/stick, hudPitch=2000 px/s/stick); the observed plateaus sit at
    ~25-65% stick, which is exactly what a hand-flown segment should show.
  - The offline horizon harness loses lock exactly during the fastest moves (sep < 1):
    expected - the scene changes fastest when detection matters most. The live detector's
    coasting/model hand-off is what carries those moments.

