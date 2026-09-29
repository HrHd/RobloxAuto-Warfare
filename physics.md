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
