using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

// OFFLINE AUTO-TUNER + REF AUDITOR for the horizon model.
//  * reads hudphys.csv (50 Hz flight recording) and settings.ini:
//      - fits PITCH/ROLL rate gains from steady-stick windows (robust median)
//      - measures the persistent fused-vs-measured offset (height adj / roll shift)
//      - measures the real correction closing speed (corr speed / lock pull sanity)
//      - prints WHAT IT NEEDS (what to fly / capture to make the next fit solid)
//      - writes confident values into settings.ini (backup .autotune.bak)
//  * audits the hudref screenshots (the human-labelled set): thermal-signature
//    collisions between different scenes + suspect marked lines, and lists the scenes
//    that need MORE refs (that is "train on the screenshots + what else it needs").
// Usage: autotune.exe <folder with hudphys.csv / settings.ini / hudref>
class AutoTune
{
    static List<string> R = new List<string>();
    static void Say(string s) { R.Add(s); Console.WriteLine(s); }

    // ---- POOLED LEARNER -----------------------------------------------------------------------
    // The tuner used to recompute everything from the newest recording and NUDGE the current
    // settings - noisy flight to flight, occasionally double-applying (seen: +53 then -50 then
    // -106, throttle sign flip). Now every run POOLS its measurements into persistent weighted
    // estimates (value + sample count) and applies settings as base + pooled-estimate, which is
    // idempotent: running twice changes nothing, and confidence grows flight after flight.
    static readonly Dictionary<string, double> _pool = new Dictionary<string, double>();
    static readonly Dictionary<string, int> _pn = new Dictionary<string, int>();

    static void Pool(string key, double meas, int w)
    {
        if (w <= 0 || double.IsNaN(meas) || double.IsInfinity(meas)) return;
        if (w > 600) w = 600;                          // one huge flight must not dominate
        double cur = _pool.ContainsKey(key) ? _pool[key] : 0;
        int cn = _pn.ContainsKey(key) ? _pn[key] : 0;
        if (key == "estPitch" || key == "estRoll")
        {
            if (cn == 0) { _pool[key] = meas; _pn[key] = w; return; }   // ratio: seed at first
        }
        _pool[key] = (cur * cn + meas * w) / (cn + w);
        _pn[key] = cn + w;
    }
    static double Pv(string key) { return _pool.ContainsKey(key) ? _pool[key] : 0; }
    static int Pn(string key) { return _pn.ContainsKey(key) ? _pn[key] : 0; }

    // ---- VOTE SYSTEM (user request) -----------------------------------------------------------
    // A second, independent tuner: EVERY recorded flight casts one vote per parameter (bias,
    // roll offset, throttle slope, pitch/roll scale). Each run re-votes over ALL stored flights
    // and applies a parameter only when a supermajority of flights AGREE - bimodal data shows up
    // as low agreement and the parameter simply waits. This is what stops the "stays random"
    // feel of averaging flight-to-flight noise.
    static readonly Dictionary<string, double> _votes = new Dictionary<string, double>();

    static Dictionary<string, double> VoteFile(string file)
    {
        var v = new Dictionary<string, double>();
        try
        {
            var rows = new List<double[]>(); var fr = new List<double>(); var fp = new List<double>();
            LoadPhys(file, rows, fr, fp);
            if (rows.Count < 300) return v;
            var offP = new List<double>(); var offR = new List<double>();
            for (int i = 0; i < rows.Count && i < fp.Count; i++)
            {
                if (rows[i][3] < 1 || rows[i][6] < 0.6) continue;
                offP.Add(rows[i][5] - fp[i]); offR.Add(rows[i][4] - fr[i]);
            }
            if (offP.Count >= 60) v["bias"] = Med(offP);
            if (offR.Count >= 60) v["roll"] = Med(offR);
            double lyMean = 0; int lyN = 0;
            for (int i = 0; i < rows.Count; i++) if (rows[i][3] >= 1 && rows[i][6] >= 0.6 && i < fp.Count) { lyMean += rows[i][7]; lyN++; }
            if (lyN > 0)
            {
                lyMean /= lyN;
                double sxy = 0, sxx = 0; int tn = 0; double lmin = 9, lmax = -9;
                for (int i = 0; i < rows.Count && i < fp.Count; i++)
                {
                    if (rows[i][3] < 1 || rows[i][6] < 0.6) continue;
                    double x = rows[i][7] - lyMean;
                    sxy += x * (rows[i][5] - fp[i]); sxx += x * x; tn++;
                    if (rows[i][7] < lmin) lmin = rows[i][7]; if (rows[i][7] > lmax) lmax = rows[i][7];
                }
                if (tn >= 250 && (lmax - lmin) > 0.2 && sxx > 1e-6)
                {
                    double sl = sxy / sxx;
                    if (Math.Abs(sl) <= 150) v["thr"] = sl;
                }
            }
            var mt = new List<double>(); var mp = new List<double>(); var mr = new List<double>(); var msy = new List<double>(); var msx = new List<double>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i][3] < 1 || rows[i][6] < 0.4) continue;
                if (mp.Count > 0 && Math.Abs(rows[i][5] - mp[mp.Count - 1]) < 0.5) continue;
                mt.Add(rows[i][0]); mp.Add(rows[i][5]); mr.Add(rows[i][4]); msy.Add(-rows[i][2]); msx.Add(-rows[i][1]);
            }
            var kP = new List<double>(); var kR = new List<double>();
            for (int i = 1; i < mt.Count; i++)
            {
                double dtc = (mt[i] - mt[i - 1]) / 1000.0;
                if (dtc < 0.08 || dtc > 1.0) continue;
                double rP = (mp[i] - mp[i - 1]) / dtc, rR = (mr[i] - mr[i - 1]) / dtc;
                double sy = (msy[i] + msy[i - 1]) / 2.0, sx = (msx[i] + msx[i - 1]) / 2.0;
                if (Math.Abs(rP) > 4000 || Math.Abs(rR) > 3000) continue;
                if (Math.Abs(sy) > 0.15 && Math.Abs(rP) > 5) kP.Add(rP / sy);   // px/s per stick
                if (Math.Abs(sx) > 0.15 && Math.Abs(rR) > 3) kR.Add(rR / sx);   // deg/s per stick
            }
            if (kP.Count >= 3) v["pk"] = Med(kP);
            if (kR.Count >= 3) v["rk"] = Med(kR);
        }
        catch { }
        return v;
    }

    // Collect votes for one parameter (optionally dividing each by a reference for ratio votes).
    static void Votes(string param, double div, out double med, out int n, out double agree)
    {
        var vals = new List<double>();
        foreach (var kv in _votes)
        {
            if (!kv.Key.StartsWith("v:" + param + ":")) continue;
            vals.Add(div != 0 ? kv.Value / div : kv.Value);
        }
        n = vals.Count; med = 0; agree = 0;
        if (n == 0) return;
        med = Med(vals);
        var devs = new List<double>();
        foreach (double x in vals) devs.Add(Math.Abs(x - med));
        double mad = Med(devs);
        int inl = 0;
        foreach (double x in vals) if (Math.Abs(x - med) <= Math.Max(3.0 * mad, 1e-9)) inl++;
        agree = inl / (double)n;
    }


    // Parse one physlog csv (current or archived) into the shared row lists. Legacy 19-col rows
    // from the old writer are mapped positionally; modern rows by header name.
    static void LoadPhys(string file, List<double[]> rows, List<double> fuseR, List<double> fuseP)
    {
        try
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length <= 30) return;
            string[] hdr = lines[0].TrimStart('\ufeff').Split(',');
            bool modern = Array.IndexOf(hdr, "sideConf") >= 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split(',');
                if (p.Length == 19 && modern)
                {
                    double v0, v1, v2, v5, v6, v7, v8, v9, v10;
                    if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v0)) continue;
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v1);
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v2);
                    double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out v5);
                    double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out v6);
                    double.TryParse(p[7], NumberStyles.Float, CultureInfo.InvariantCulture, out v7);
                    double.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out v8);
                    double.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out v9);
                    double.TryParse(p[10], NumberStyles.Float, CultureInfo.InvariantCulture, out v10);
                    double v4; double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out v4);
                    rows.Add(new double[] { v0, v1, v2, v5, v6, v7, v8, v4 });
                    fuseR.Add(v9); fuseP.Add(v10);
                    continue;
                }
                if (p.Length < hdr.Length) continue;
                int iT = Array.IndexOf(hdr, "t_ms"), iSx = Array.IndexOf(hdr, "sx"), iSy = Array.IndexOf(hdr, "sy");
                int iVis = Array.IndexOf(hdr, "vision"), iDR = Array.IndexOf(hdr, "detRoll"), iDP = Array.IndexOf(hdr, "detPitch");
                int iConf = Array.IndexOf(hdr, "conf"), iFR = Array.IndexOf(hdr, "fuseRoll"), iFP = Array.IndexOf(hdr, "fusePitch");
                double t, sx, sy, dr, dp, cf;
                double.TryParse(p[iT], NumberStyles.Float, CultureInfo.InvariantCulture, out t);
                double.TryParse(p[iSx], NumberStyles.Float, CultureInfo.InvariantCulture, out sx);
                double.TryParse(p[iSy], NumberStyles.Float, CultureInfo.InvariantCulture, out sy);
                double.TryParse(p[iDR], NumberStyles.Float, CultureInfo.InvariantCulture, out dr);
                double.TryParse(p[iDP], NumberStyles.Float, CultureInfo.InvariantCulture, out dp);
                double.TryParse(p[iConf], NumberStyles.Float, CultureInfo.InvariantCulture, out cf);
                double fr = 0, fp = 0;
                if (iFR >= 0) double.TryParse(p[iFR], NumberStyles.Float, CultureInfo.InvariantCulture, out fr);
                if (iFP >= 0) double.TryParse(p[iFP], NumberStyles.Float, CultureInfo.InvariantCulture, out fp);
                int iLy = Array.IndexOf(hdr, "ly");
                double lyd = 0;
                if (iLy >= 0) double.TryParse(p[iLy], NumberStyles.Float, CultureInfo.InvariantCulture, out lyd);
                rows.Add(new double[] { t, sx, sy, (iVis >= 0 && p[iVis].Trim() == "1") ? 1 : 0, dr, dp, cf, lyd });
                fuseR.Add(fr); fuseP.Add(fp);
            }
        }
        catch { }
    }
    static double Med(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[v.Count / 2 - 1] + v[v.Count / 2]);
    }

    // ---- thermal signature (same as the app: block-mean luminance -> LUT -> RGB) ----
    static byte[] rL = new byte[256], gL = new byte[256], bL = new byte[256];
    static byte Cl(double v) { int i = (int)(v * 255.0 + 0.5); return (byte)(i < 0 ? 0 : i > 255 ? 255 : i); }
    static float[] SigThm(Bitmap bmp)
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
                    for (int x = x0; x < x1; x += stp) { Color p = bmp.GetPixel(x, y); r += p.R; g += p.G; b += p.B; c++; }
                int o = (by * 6 + bx) * 3;
                if (c > 0)
                {
                    float lum = (float)((0.299 * r + 0.587 * g + 0.114 * b) / c);
                    int li = (int)lum; if (li < 0) li = 0; if (li > 255) li = 255;
                    s[o] = rL[li] / 255f; s[o + 1] = gL[li] / 255f; s[o + 2] = bL[li] / 255f;
                }
            }
        return s;
    }
    static float Rmse(float[] a, float[] b) { float d = 0f; for (int k = 0; k < 108; k++) { float e = a[k] - b[k]; d += e * e; } return (float)Math.Sqrt(d / 108.0); }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        for (int i = 0; i < 256; i++) { double v = i / 255.0; rL[i] = Cl((1.0 - v) * 1.30 - 0.10); gL[i] = Cl((1.0 - v) * 1.10 - 0.08); bL[i] = Cl((1.0 - v) * 0.90 - 0.06); }
        string csv = Path.Combine(dir, "hudphys.csv");
        string ini = Path.Combine(dir, "settings.ini");

        double dialPitch = 1200, dialRoll = 300, hudBias = 0, hudRollOff = 0, corrPx = 2200, lockPull = 0.30, accelTau = 0.12;
        var iniLines = File.Exists(ini) ? File.ReadAllLines(ini) : new string[0];
        foreach (string ln in iniLines)
        {
            int eq = ln.IndexOf('='); if (eq <= 0) continue;
            string k = ln.Substring(0, eq).Trim(); double v;
            if (!double.TryParse(ln.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
if (k == "hudPitch") dialPitch = v; else if (k == "hudRoll") dialRoll = v;
else if (k == "hudBias") hudBias = v; else if (k == "hudRollOff") hudRollOff = v;
else if (k == "hudAccel") accelTau = v;
            else if (k == "hudCorrPx") corrPx = v; else if (k == "hudLockPull") lockPull = v;
        }

        // ================= REF AUDIT (the labelled screenshots) =================
        Say("==== REF AUDIT (hudref screenshots) ====");
        string refDir = Path.Combine(dir, "hudref");
        int collided = 0, suspect = 0;
        var collidedNames = new List<string>();
        if (Directory.Exists(refDir))
        {
            string[] files = Directory.GetFiles(refDir, "*.png");
            float[][] sigs = new float[files.Length][];
            for (int i = 0; i < files.Length; i++) { using (var b = new Bitmap(files[i])) sigs[i] = SigThm(b); }
            for (int i = 0; i < files.Length; i++)
            {
                double bo = 9;
                for (int j = 0; j < files.Length; j++)
                {
                    if (j == i) continue;
                    float d = Rmse(sigs[i], sigs[j]);
                    if (d > 1e-6 && d < bo) bo = d;
                }
                string nm = Path.GetFileNameWithoutExtension(files[i]);
                if (bo < 0.033 && bo < 9)
                {
                    collided++;
                    if (collidedNames.Count < 10) collidedNames.Add(nm + " (" + bo.ToString("0.000") + ")");
                }
                float rr = 0, pp = 0; bool got = false;
                string hzn = Path.ChangeExtension(files[i], ".hzn");
                if (File.Exists(hzn))
                    foreach (string l in File.ReadAllLines(hzn))
                    {
                        int eq = l.IndexOf('='); if (eq <= 0) continue;
                        float v; if (!float.TryParse(l.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                        if (l.StartsWith("roll")) { rr = v; got = true; } else if (l.StartsWith("pitch")) { pp = v; got = true; }
                    }
                if (!got || Math.Abs(pp) > 1500f || Math.Abs(rr) > 100f) suspect++;
            }
            Say("refs: " + files.Length + "   collide-prone (<0.033 to a different scene): " + collided + "   suspect lines: " + suspect);
            foreach (string n in collidedNames) Say("   close pair: " + n);
        }
        else Say("no hudref folder");

        // ================= FLIGHT FIT =================
        Say("");
        Say("==== FLIGHT FIT (hudphys.csv) ====");
        var rows = new List<double[]>();
        var fuseR = new List<double>(); var fuseP = new List<double>();
        if (File.Exists(csv)) LoadPhys(csv, rows, fuseR, fuseP);
        // ARCHIVED FLIGHTS: the app copies each flight's recording to hudrec\phys-*.csv on the
        // next spawn. Merging them all is what finally gives the response fit real material
        // (one flight holds only ~65-73 fresh measurement points).
        int arch = 0;
        try
        {
            string ad = Path.Combine(dir, "hudrec");
            if (Directory.Exists(ad))
            {
                string[] pf = Directory.GetFiles(ad, "phys-*.csv");
                Array.Sort(pf);
                foreach (string pf1 in pf) { LoadPhys(pf1, rows, fuseR, fuseP); arch++; }
            }
        }
        catch { }
        if (arch > 0) Say("archived flights merged: " + arch);
        if (rows.Count == 0) { Say("no usable flight rows"); Finish(dir); return; }
        Say("rows " + rows.Count + "   (" + ((rows[rows.Count - 1][0] - rows[0][0]) / 1000.0).ToString("0") + "s)   dials: pitch " + dialPitch + "  roll " + dialRoll + "  corr " + corrPx + "  lock " + (lockPull * 100).ToString("0") + "%");

        // ---- FRESH-MEASUREMENT SERIES -----------------------------------------------------------
        // The recorder writes 50 rows/s but the detector updates ~8x/s: between updates detPitch /
        // detRoll are STALE COPIES, so per-tick diffs are mostly zero and both the rate-gain and the
        // response fits collapsed on real flights (gains 0.000 n=229, response corr 0.03). Build a
        // series of genuine NEW measurements: rows where detPitch moved against the last kept point.
        var mt = new List<double>(); var mp = new List<double>(); var mr = new List<double>();
        var msy = new List<double>(); var msx = new List<double>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i][3] < 1 || rows[i][6] < 0.4) continue;
            if (mp.Count > 0 && Math.Abs(rows[i][5] - mp[mp.Count - 1]) < 0.5) continue;
            mt.Add(rows[i][0]); mp.Add(rows[i][5]); mr.Add(rows[i][4]);
            msy.Add(-rows[i][2]); msx.Add(-rows[i][1]);
        }
        Say("fresh measurements: " + mt.Count + "  (detector update points)");

        // RATE GAIN, per fresh interval: k = measured rate / (stick * dial). Sign convention
        // mirrors the app: pitch command = -stickY, roll command = -stickX.
        var kP = new List<double>(); var kR = new List<double>();
        for (int i = 1; i < mt.Count; i++)
        {
            double dtc = (mt[i] - mt[i - 1]) / 1000.0;
            if (dtc < 0.08 || dtc > 1.0) continue;
            double rmP = (mp[i] - mp[i - 1]) / dtc;
            double rmR = (mr[i] - mr[i - 1]) / dtc;
            double sy = (msy[i] + msy[i - 1]) / 2.0;
            double sx = (msx[i] + msx[i - 1]) / 2.0;
            if (Math.Abs(rmP) > 4000 || Math.Abs(rmR) > 3000) continue;
            if (Math.Abs(sy) > 0.15 && Math.Abs(sy * dialPitch) > 100) kP.Add(rmP / (sy * dialPitch));
            if (Math.Abs(sx) > 0.15 && Math.Abs(sx * dialRoll) > 8) kR.Add(rmR / (sx * dialRoll));
        }
        double gainP = Med(kP), gainR = Med(kR);
        Say("rate gains:  pitch " + gainP.ToString("0.000") + " (n=" + kP.Count + ")   roll " + gainR.ToString("0.000") + " (n=" + kR.Count + ")");

        // RESPONSE FIT on the fresh series: for candidate lags, correlate the STICK (sampled tau
        // seconds earlier) with the measured rate. Gives the stick->rate lag and the true scale.
        double respPk = 0, respPcorr = 0, respPt = 0;
        double respRk = 0, respRcorr = 0, respRt = 0;
        {
            double[] taus = { 0.03, 0.05, 0.08, 0.11, 0.14, 0.18, 0.24, 0.32, 0.45, 0.60 };
            for (int axis = 0; axis < 2; axis++)
            {
                foreach (double tau in taus)
                {
                    double vv = 0, vy = 0, yy = 0;
                    for (int i = 1; i < mt.Count; i++)
                    {
                        double dtc = (mt[i] - mt[i - 1]) / 1000.0;
                        if (dtc < 0.08 || dtc > 1.0) continue;
                        double rate = axis == 0 ? (mp[i] - mp[i - 1]) / dtc : (mr[i] - mr[i - 1]) / dtc;
                        if (axis == 0 && Math.Abs(rate) > 4000) continue;
                        if (axis == 1 && Math.Abs(rate) > 3000) continue;
                        // find the stick sample nearest to (mid-time minus tau)
                        double tt = (mt[i] + mt[i - 1]) / 2.0 - tau * 1000.0;
                        int lo = i - 1;
                        while (lo > 0 && mt[lo - 1] > tt) lo--;
                        double v = axis == 0 ? msy[lo] : msx[lo];
                        vv += v * v; vy += v * rate; yy += rate * rate;
                    }
                    double corr = (vv > 1e-9 && yy > 1e-9) ? Math.Abs(vy) / Math.Sqrt(vv * yy) : 0;
                    double kk = vv > 1e-9 ? vy / vv : 0;
                    if (axis == 0)
                    {
                        if (corr > respPcorr) { respPcorr = corr; respPt = tau; respPk = kk; }
                    }
                    else
                    {
                        if (corr > respRcorr) { respRcorr = corr; respRt = tau; respRk = kk; }
                    }
                }
            }
        }
        Say("stick resp:  pitch lag " + respPt.ToString("0.00") + "s  scale " + respPk.ToString("0") + " px/s per stick  (corr " + respPcorr.ToString("0.00") + ")");
        Say("             roll  lag " + respRt.ToString("0.00") + "s  scale " + respRk.ToString("0") + " deg/s per stick  (corr " + respRcorr.ToString("0.00") + ")");
        bool respPValid = respPcorr >= 0.5 && respPt > 0.02 && respPt < 0.8 && respPk > 0.3 * dialPitch && respPk < 2.2 * dialPitch;
        bool respRValid = respRcorr >= 0.5 && respRt > 0.02 && respRt < 0.8 && respRk > 0.3 * dialRoll && respRk < 2.2 * dialRoll;
        double tauBest = 0;
        if (respPValid && respRValid) tauBest = (respPt + respRt) / 2.0;
        else if (respPValid) tauBest = respPt;
        else if (respRValid) tauBest = respRt;

        var offP = new List<double>(); var offR = new List<double>();
        for (int i = 0; i < rows.Count && i < fuseP.Count; i++)
        {
            if (rows[i][3] < 1 || rows[i][6] < 0.6) continue;
            offP.Add(rows[i][5] - fuseP[i]);
            offR.Add(rows[i][4] - fuseR[i]);
        }
        double biasP = Med(offP), biasR = Med(offR);
        Say("offsets:     pitch bias " + biasP.ToString("0.0") + " px (n=" + offP.Count + ")   roll bias " + biasR.ToString("0.00") + " deg (n=" + offR.Count + ")");

        var rates = new List<double>();
        for (int i = 0; i + 10 < rows.Count; i++)
        {
            double e0 = Math.Abs(rows[i][5] - fuseP[i]);
            if (e0 < 100) continue;
            int j = i + 1;
            while (j < rows.Count && j < i + 60 && Math.Abs(rows[j][5] - fuseP[j]) > 40) j++;
            if (j >= rows.Count || j >= i + 60) continue;
            double dtc = (rows[j][0] - rows[i][0]) / 1000.0;
            if (dtc < 0.05) continue;
            rates.Add((e0 - 40.0) / dtc);
        }
        double conv = Med(rates);
        Say("correction:  median closing rate " + conv.ToString("0") + " px/s (n=" + rates.Count + ")");

        // ---- THROTTLE EFFECT: does the pitch bias scale with the left-stick Y? ----
        // (The "throttle" dial = a direct horizon offset from the left stick. Mostly it is
        // PARALLAX - climbing drops the treeline - so this fits the AVERAGE px-per-unit effect
        // over this flight. Only applied when the throttle actually VARIED and the fit is solid.)
        double lyMean = 0; int lyN = 0;
        for (int i = 0; i < rows.Count; i++) if (rows[i][3] >= 1 && rows[i][6] >= 0.6 && i < fuseP.Count) { lyMean += rows[i][7]; lyN++; }
        if (lyN > 0) lyMean /= lyN;
        double sxy = 0, sxx = 0, sp = 0; int tn2 = 0; double lyMin = 9, lyMax = -9;
        for (int i = 0; i < rows.Count && i < fuseP.Count; i++)
        {
            if (rows[i][3] < 1 || rows[i][6] < 0.6) continue;
            double x = rows[i][7] - lyMean;
            double y = rows[i][5] - fuseP[i];          // measurement minus fused line
            sxy += x * y; sxx += x * x; sp += y; tn2++;
            if (rows[i][7] < lyMin) lyMin = rows[i][7];
            if (rows[i][7] > lyMax) lyMax = rows[i][7];
        }
        double throttleCoef = sxx > 1e-6 ? sxy / sxx : 0;
        Say("throttle:    slope " + throttleCoef.ToString("0.0") + " px per left-stick Y (n=" + tn2 +
            ", stick range " + lyMin.ToString("0.00") + ".." + lyMax.ToString("0.00") + ")");
        bool throttleFit = tn2 >= 250 && (lyMax - lyMin) > 0.15 && Math.Abs(throttleCoef) > 25 && Math.Abs(throttleCoef) < 400;

        // ---- settings.ini apply ----
        // GUARD: never apply twice to the same flight - the trims would stack. The flight id is
        // its first/last timestamps; store it after applying.
        string statePath = Path.Combine(dir, "autotune-state.txt");
        string flightId = rows.Count + ":" + rows[0][0].ToString("0") + ":" + rows[rows.Count - 1][0].ToString("0") + ":a" + arch;
        bool already = false;
        try
        {
            if (File.Exists(statePath))
            {
                string[] st = File.ReadAllText(statePath).Trim().Split('\n');
                string oldId = ""; long oldAt = 0;
                foreach (string ln in st)
                {
                    string t = ln.Trim();
                    int eq = t.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = t.Substring(0, eq), v = t.Substring(eq + 1);
                        double dv; int iv;
                        if (k == "id") oldId = v;
                        else if (k == "at") long.TryParse(v, out oldAt);
                        else if (k.StartsWith("n_")) { if (int.TryParse(v, out iv)) _pn[k.Substring(2)] = iv; }
                        else if (k.StartsWith("v:") || k.StartsWith("seen:"))
                        { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) _votes[k] = dv; }
                        else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) _pool[k] = dv;
                    }
                    else if (t.Length > 0 && oldId == "")
                    {
                        oldId = t;                       // old single-line format (backward compat)
                    }
                }
                if (oldId == flightId) already = true;
                // COOLDOWN: the recording keeps growing while the app runs, so the id can change
                // between runs on the SAME data. Ten minutes minimum between applies.
                long nowS = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                if (oldAt > 0 && nowS - oldAt < 600) already = true;
            }
        }
        catch { }
        if (already) Say("(this flight was already learned - report only, no changes)");

        // POOL THIS FLIGHT'S MEASUREMENTS (only when not already learned)
        if (!already)
        {
            if (offP.Count >= 30) Pool("estBias", biasP, offP.Count);
            if (offR.Count >= 30) Pool("estRoll", biasR, offR.Count);
            if (throttleFit && Math.Abs(throttleCoef) <= 150) Pool("estThr", throttleCoef, tn2);
            if (kP.Count >= 2 && gainP > 0.5 && gainP < 2.0) Pool("estPitch", gainP, kP.Count);
            if (kR.Count >= 2 && gainR > 0.5 && gainR < 2.0) Pool("estRollG", gainR, kR.Count);
            if (respPcorr >= 0.45 && respPk > 200 && respPk < 8000) Pool("estPk", respPk, 100);
            if (respRcorr >= 0.45 && respRk > 10 && respRk < 800) Pool("estRk", respRk, 100);
            if ((respPcorr >= 0.45 || respRcorr >= 0.45) && tauBest > 0.02f) Pool("estTau", tauBest, 60);
            // capture the base settings ONCE, before the tuner ever moved them
            if (!_pool.ContainsKey("baseBias")) _pool["baseBias"] = hudBias;
            if (!_pool.ContainsKey("baseRoll")) _pool["baseRoll"] = hudRollOff;
            if (!_pool.ContainsKey("basePitch")) _pool["basePitch"] = dialPitch;
            if (!_pool.ContainsKey("baseRollG")) _pool["baseRollG"] = dialRoll;
        }

        // ---- VOTE INGESTION + CONSENSUS --------------------------------------------------------
        // Each recorded flight votes once per parameter (keyed by file+size, so re-running never
        // counts a flight twice). The consensus is the median over flights + the fraction of
        // flights within 3xMAD - low agreement = bimodal data = keep waiting.
        {
            var vfiles = new List<string>();
            if (File.Exists(csv)) vfiles.Add(csv);
            try
            {
                string ad = Path.Combine(dir, "hudrec");
                if (Directory.Exists(ad)) { string[] pf = Directory.GetFiles(ad, "phys-*.csv"); Array.Sort(pf); vfiles.AddRange(pf); }
            }
            catch { }
            foreach (string vf in vfiles)
            {
                try
                {
                    string vkey = Path.GetFileName(vf) + ":" + new FileInfo(vf).Length;
                    if (_votes.ContainsKey("seen:" + vkey)) continue;
                    _votes["seen:" + vkey] = 1;
                    var vs = VoteFile(vf);
                    foreach (var kv in vs) _votes["v:" + kv.Key + ":" + vkey] = kv.Value;
                }
                catch { }
            }
        }
        double vBias, vRoll, vThr, vPR, vRR;
        int nBias, nRoll, nThr, nPR, nRR; double aBias, aRoll, aThr, aPR, aRR;
        Votes("bias", 1, out vBias, out nBias, out aBias);
        Votes("roll", 1, out vRoll, out nRoll, out aRoll);
        Votes("thr", 1, out vThr, out nThr, out aThr);
        Votes("pk", dialPitch, out vPR, out nPR, out aPR);
        Votes("rk", dialRoll, out vRR, out nRR, out aRR);
        var newIni = new StringBuilder(); int changed = 0;
        foreach (string ln in iniLines)
        {
            int eq = ln.IndexOf('=');
            if (eq <= 0) { newIni.AppendLine(ln); continue; }
            string k = ln.Substring(0, eq).Trim();
            string outLn = ln;
            if (already) { newIni.AppendLine(ln); continue; }
            // VOTE FIRST, POOLED AS FALLBACK, IDEMPOTENT ALWAYS: settings = base + (vote consensus
            // or pooled estimate). A vote consensus needs n>=3 flights with >=70% agreement.
            if (k == "hudBias")
            {
                double nv = double.NaN;
                if (nBias >= 3 && aBias >= 0.7) nv = Math.Round(Pv("baseBias") + vBias);
                else if (Pn("estBias") >= 120) nv = Math.Round(Pv("baseBias") + Pv("estBias"));
                if (!double.IsNaN(nv))
                {
                    if (nv < -120) nv = -120; if (nv > 120) nv = 120;
                    if (Math.Abs(nv - hudBias) >= 1) { outLn = "hudBias=" + nv.ToString("0"); changed++; }
                }
            }
            else if (k == "hudRollOff")
            {
                double nv = double.NaN;
                if (nRoll >= 3 && aRoll >= 0.7) nv = Math.Round((Pv("baseRoll") + vRoll) * 10) / 10.0;
                else if (Pn("estRoll") >= 120) nv = Math.Round((Pv("baseRoll") + Pv("estRoll")) * 10) / 10.0;
                if (!double.IsNaN(nv))
                {
                    if (nv < -180) nv = -180; if (nv > 180) nv = 180;
                    if (Math.Abs(nv - hudRollOff) >= 0.1) { outLn = "hudRollOff=" + nv.ToString("0.0", CultureInfo.InvariantCulture); changed++; }
                }
            }
            else if (k == "hudThr")
            {
                double nv = double.NaN;
                if (nThr >= 3 && aThr >= 0.7) nv = Math.Round(-vThr);
                else if (Pn("estThr") >= 400) nv = Math.Round(-Pv("estThr"));
                if (!double.IsNaN(nv))
                {
                    if (nv < -120) nv = -120; if (nv > 120) nv = 120;
                    double oldT;
                    double.TryParse(ln.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out oldT);
                    if (Math.Abs(nv - oldT) >= 1) { outLn = "hudThr=" + nv.ToString("0"); changed++; }
                }
            }
            else if (k == "hudPitch")
            {
                double nv = double.NaN;
                if (nPR >= 3 && aPR >= 0.7 && vPR > 0.4 && vPR < 2.5) nv = Math.Round(Pv("basePitch") * vPR);
                else if (Pn("estPitch") >= 8) nv = Math.Round(Pv("basePitch") * Pv("estPitch"));
                if (!double.IsNaN(nv))
                {
                    if (nv < 80) nv = 80; if (nv > 4000) nv = 4000;
                    if (Math.Abs(nv - dialPitch) >= 10) { outLn = "hudPitch=" + nv.ToString("0"); changed++; }
                }
            }
            else if (k == "hudRoll")
            {
                double nv = double.NaN;
                if (nRR >= 3 && aRR >= 0.7 && vRR > 0.4 && vRR < 2.5) nv = Math.Round(Pv("baseRollG") * vRR);
                else if (Pn("estRollG") >= 8) nv = Math.Round(Pv("baseRollG") * Pv("estRollG"));
                if (!double.IsNaN(nv))
                {
                    if (nv < 20) nv = 20; if (nv > 900) nv = 900;
                    if (Math.Abs(nv - dialRoll) >= 2) { outLn = "hudRoll=" + nv.ToString("0"); changed++; }
                }
            }
            else if (k == "hudAccel" && Pn("estTau") >= 30)
            {
                double nv = Math.Round(Pv("estTau") * 100.0) / 100.0;
                if (nv < 0.02) nv = 0.02; if (nv > 1.0) nv = 1.0;
                if (Math.Abs(nv - accelTau) >= 0.02) { outLn = "hudAccel=" + nv.ToString("0.00", CultureInfo.InvariantCulture); changed++; }
            }
            newIni.AppendLine(outLn);
        }
        // keys that do not exist in the ini yet can never be rewritten by the loop above - the
        // field ini has no hudThr line, so the throttle fit silently never landed. Append the
        // POOLED value (idempotent, same math as the branch above).
        {
            bool hasThr = false;
            foreach (string ln in iniLines) if (ln.TrimStart().StartsWith("hudThr")) { hasThr = true; break; }
            if (!already && !hasThr && Pn("estThr") >= 150)
            {
                double nv = Math.Round(-Pv("estThr"));
                if (nv < -120) nv = -120; if (nv > 120) nv = 120;
                newIni.AppendLine("hudThr=" + nv.ToString("0", CultureInfo.InvariantCulture));
                changed++;
            }
        }
        Say("");
        // persist the pooled learner on every learned flight (even with no applies - the pool
        // itself is progress) + a history line so the crawl toward a stable solution is visible
        if (!already)
        {
            var sb = new StringBuilder();
            sb.AppendLine("id=" + flightId);
            sb.AppendLine("at=" + (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            foreach (var kv in _pool) sb.AppendLine(kv.Key + "=" + kv.Value.ToString("0.######", CultureInfo.InvariantCulture));
            foreach (var kv in _pn) sb.AppendLine("n_" + kv.Key + "=" + kv.Value);
            foreach (var kv in _votes) sb.AppendLine(kv.Key + "=" + kv.Value.ToString("0.######", CultureInfo.InvariantCulture));
            try { File.WriteAllText(statePath, sb.ToString()); } catch { }
            try
            {
                File.AppendAllText(Path.Combine(dir, "autotune-history.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  flight " + flightId + "  " +
                    "biasP " + biasP.ToString("0.0") + " (n" + offP.Count + ")  biasR " + biasR.ToString("0.0") +
                    "  thr " + throttleCoef.ToString("0.0") + "  gainP " + gainP.ToString("0.00") + "  gainR " + gainR.ToString("0.00") +
                    "  tau " + tauBest.ToString("0.00") + "\r\n");
            }
            catch { }
        }
        if (changed > 0 && iniLines.Length > 0)
        {
            File.Copy(ini, ini + ".autotune.bak", true);
            File.WriteAllText(ini, newIni.ToString());
            Say("settings.ini updated (" + changed + " value(s), backup settings.ini.autotune.bak) - restart the app to load");
        }
        else Say(already ? "no changes (already learned)" : "pooled, no settings change needed this run");
        Say("");
        Say("POOLED ESTIMATES (what it currently believes; applied = base + estimate):");
        Say(string.Format(CultureInfo.InvariantCulture, "  bias       {0,8:0.0} px    n {1,5}   -> hudBias {2,5:0}",
            Pv("estBias"), Pn("estBias"), Math.Round(Pv("baseBias") + Pv("estBias"))));
        Say(string.Format(CultureInfo.InvariantCulture, "  rollOff    {0,8:0.0} deg   n {1,5}   -> hudRollOff {2,5:0.0}",
            Pv("estRoll"), Pn("estRoll"), Pv("baseRoll") + Pv("estRoll")));
        Say(string.Format(CultureInfo.InvariantCulture, "  throttle   {0,8:0.0} px/ls n {1,5}   -> hudThr {2,5:0}",
            Pv("estThr"), Pn("estThr"), Math.Round(-Pv("estThr"))));
        Say(string.Format(CultureInfo.InvariantCulture, "  pitchScale {0,8:0.00}      n {1,5}   -> hudPitch {2,5:0}",
            Pv("estPitch"), Pn("estPitch"), Math.Round(Pv("basePitch") * Pv("estPitch"))));
        Say(string.Format(CultureInfo.InvariantCulture, "  rollScale  {0,8:0.00}      n {1,5}   -> hudRoll {2,5:0}",
            Pv("estRollG"), Pn("estRollG"), Math.Round(Pv("baseRollG") * Pv("estRollG"))));
        Say(string.Format(CultureInfo.InvariantCulture, "  resp lag   {0,8:0.00} s    n {1,5}   -> hudAccel {2,5:0.00}",
            Pv("estTau"), Pn("estTau"), Pv("estTau")));
        Say("");
        Say("VOTES (one per recorded flight; apply needs n>=3 and >=70% agreement):");
        Say(string.Format(CultureInfo.InvariantCulture, "  bias       {0,8:0.0} px    votes {1,3}   agree {2,4:0}%", vBias, nBias, aBias * 100));
        Say(string.Format(CultureInfo.InvariantCulture, "  rollOff    {0,8:0.0} deg   votes {1,3}   agree {2,4:0}%", vRoll, nRoll, aRoll * 100));
        Say(string.Format(CultureInfo.InvariantCulture, "  throttle   {0,8:0.0} px/ls votes {1,3}   agree {2,4:0}%", vThr, nThr, aThr * 100));
        Say(string.Format(CultureInfo.InvariantCulture, "  pitchScale {0,8:0.00}      votes {1,3}   agree {2,4:0}%", vPR, nPR, aPR * 100));
        Say(string.Format(CultureInfo.InvariantCulture, "  rollScale  {0,8:0.00}      votes {1,3}   agree {2,4:0}%", vRR, nRR, aRR * 100));

        // ---- WHAT IT NEEDS ----
        Say("");
        Say("==== WHAT IT NEEDS NEXT ====");
        if (kP.Count < 8) Say("- PITCH: fly ~30 s doing 3-4 steady full-stick pitch pulls with the horizon in view (only " + kP.Count + " good windows)");
        if (kR.Count < 8) Say("- ROLL: same with steady banks (" + kR.Count + " good windows)");
        if (rates.Count < 3) Say("- CORRECTION: do a few pull-ups so the line loses and re-acquires (only " + rates.Count + " closures)");
        if (nBias >= 3 && aBias >= 0.7) Say("- OFFSET: vote consensus " + vBias.ToString("0.0") + " px over " + nBias + " flights (" + (aBias * 100).ToString("0") + "% agree) - applied as hudBias");
        else Say("- OFFSET: this flight measured " + biasP.ToString("0.0") + " px (needs >=3 agreeing flights to apply)");
        if (conv > 0 && conv < corrPx * 0.5) Say("- The line closes at " + conv.ToString("0") + "px/s but the dial allows " + corrPx.ToString("0") + " - raise \"lock pull\" a notch (try " + Math.Min(60, Math.Round((lockPull + 0.1) * 100)).ToString("0") + "%)");
        if (Pn("estThr") >= 400 || (nThr >= 3 && aThr >= 0.7)) Say("- THROTTLE: applying " + Math.Round(-(nThr >= 3 && aThr >= 0.7 ? vThr : Pv("estThr"))).ToString("0") + " px/ls as hudThr");
        else if (throttleFit) Say("- THROTTLE: this flight measured " + throttleCoef.ToString("0") + " px/ls - held until pooled n>=400 or 3 agreeing flight votes (clamped +-120 when it applies)");
        else if (tn2 >= 250 && (lyMax - lyMin) <= 0.15) Say("- THROTTLE: the throttle barely moved this flight (range " + (lyMax - lyMin).ToString("0.00") + ") - no fit possible; varied climbs/descents would let it tune");
        if (respPValid || respRValid) Say("- STICK RESP: fitted lag " + tauBest.ToString("0.00") + "s (was " + accelTau.ToString("0.00") + "s)  pitch scale " + respPk.ToString("0") + " vs dial " + dialPitch.ToString("0") + " - the model now starts on YOUR drone's response");
        else if (rows.Count > 200) Say("- STICK RESP: low correlation - needs smooth long stick moves with the horizon visible to fit the response");
        if (collided > 0) Say("- REFS: " + collided + " screenshots are close to a different scene's signature - capture more refs for: " + string.Join(", ", collidedNames.ToArray()));
        if (suspect > 0) Say("- " + suspect + " refs have missing/suspect marked lines - re-check them in the editor");
        Finish(dir);
    }

    static void Finish(string dir)
    {
        try { File.WriteAllLines(Path.Combine(dir, "autotune-report.txt"), R); } catch { }
    }
}

