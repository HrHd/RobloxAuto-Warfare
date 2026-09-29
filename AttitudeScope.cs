using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows.Forms;

// ATTITUDE - standalone data view. Polls the flight app's /state (localhost:8730) and draws
// WHERE THE DATA THINKS THE DRONE IS POINTING: the fused horizon line + roll, the horizon ID%,
// and the colourless side estimate as a ghost line. No game interaction, read-only.
class AttitudeScope : Form
{
    double roll, pit, idpct, r2, p2, c2, gainP, gainR, rngM;
    int gainPn, gainRn;
    bool flight, hud, uav, alive;
    string status = "connecting...";
    long lastAt = 0;

    public AttitudeScope()
    {
        Text = "ATTITUDE - data view";
        ClientSize = new Size(360, 470);
        BackColor = Color.FromArgb(14, 15, 18);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        // visible over everything, parked on the RIGHT monitor (portrait, x=1920). Drag anywhere.
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(1980, 200);

        Thread t = new Thread(Poll);
        t.IsBackground = true;
        t.Start();

        System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
        tm.Interval = 33;
        tm.Tick += delegate { Invalidate(); };
        tm.Start();
    }

    void Poll()
    {
        WebClient wc = new WebClient();
        while (true)
        {
            try
            {
                string s = wc.DownloadString("http://localhost:8730/state");
                roll = Num(s, "roll"); pit = Num(s, "pit"); idpct = Num(s, "idpct");
                r2 = Num(s, "roll2"); p2 = Num(s, "pit2"); c2 = Num(s, "c2");
                gainP = Num(s, "gainP"); gainR = Num(s, "gainR");
                gainPn = (int)Num(s, "gainPn"); gainRn = (int)Num(s, "gainRn");
                rngM = Num(s, "range");
                flight = s.IndexOf("\"flight\":true") >= 0;
                hud = s.IndexOf("\"hud\":true") >= 0;
                uav = s.IndexOf("\"uav\":true") >= 0;
                lastAt = Environment.TickCount;
                alive = true;
                status = "";
            }
            catch (Exception e) { alive = false; status = "no data: " + e.Message; }
            Thread.Sleep(40);
        }
    }

    static double Num(string s, string key)
    {
        int i = s.IndexOf("\"" + key + "\":");
        if (i < 0) return 0;
        i += key.Length + 3;
        int j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-' || s[j] == '.' || s[j] == '+' || s[j] == 'e' || s[j] == 'E')) j++;
        double v;
        double.TryParse(s.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        return v;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int W = ClientSize.Width, H = ClientSize.Height;
        int cx = W / 2, cy = 210;
        float scale = 0.38f;                       // screen px -> window px

        bool fresh = alive && Environment.TickCount - lastAt < 1500;

        // ---- attitude ball ----
        float rad = 150f;
        using (GraphicsPath ball = new GraphicsPath())
        {
            ball.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
            g.Clip = new Region(ball);
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform((float)roll);
            g.TranslateTransform(0, (float)(pit * scale));
            using (SolidBrush skyF = new SolidBrush(Color.FromArgb(34, 52, 72)))
            using (SolidBrush gndF = new SolidBrush(Color.FromArgb(56, 47, 30)))
            {
                g.FillRectangle(skyF, -400, -800, 800, 800);
                g.FillRectangle(gndF, -400, 0, 800, 800);
            }
            using (Pen hz = new Pen(Color.FromArgb(235, 255, 255, 255), 3))
            {
                g.DrawLine(hz, -400, 0, 400, 0);
            }
            using (Pen rung = new Pen(Color.FromArgb(190, 255, 255, 255), 2))
            {
                for (int i = -4; i <= 4; i++)
                {
                    if (i == 0) continue;
                    int y = i * 25;
                    int wl = Math.Max(14, 44 - Math.Abs(i) * 7);
                    g.DrawLine(rung, -wl, y, -8, y);
                    g.DrawLine(rung, 8, y, wl, y);
                }
            }
            g.Restore(st);
            g.ResetClip();
            using (Pen ring = new Pen(Color.FromArgb(150, 120, 200, 255), 2))
                g.DrawEllipse(ring, cx - rad, cy - rad, rad * 2, rad * 2);
        }

        // ghost side line (colourless estimate)
        if (fresh && c2 > 0.10)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform((float)r2);
            g.TranslateTransform(0, (float)(p2 * scale));
            using (Pen gh = new Pen(Color.FromArgb(150, 255, 170, 60), 2))
            {
                gh.DashStyle = DashStyle.Dash;
                g.DrawLine(gh, -140, 0, 140, 0);
            }
            g.Restore(st);
        }

        // fixed reticle (where the machine points)
        using (Pen ret = new Pen(Color.FromArgb(230, 255, 255, 255), 2))
        using (Pen retb = new Pen(Color.FromArgb(160, 0, 0, 0), 4))
        {
            g.DrawLine(retb, cx - 26, cy, cx - 8, cy); g.DrawLine(retb, cx + 8, cy, cx + 26, cy);
            g.DrawLine(retb, cx, cy - 26, cx, cy - 8); g.DrawLine(retb, cx, cy + 8, cx, cy + 26);
            g.DrawLine(ret, cx - 26, cy, cx - 8, cy); g.DrawLine(ret, cx + 8, cy, cx + 26, cy);
            g.DrawLine(ret, cx, cy - 26, cx, cy - 8); g.DrawLine(ret, cx, cy + 8, cx, cy + 26);
        }

        // ---- telemetry ----
        using (Font f = new Font("Consolas", 11F, FontStyle.Bold))
        using (Font fs = new Font("Consolas", 9F))
        using (SolidBrush w = new SolidBrush(Color.Gainsboro))
        using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 155, 165)))
        using (SolidBrush grn = new SolidBrush(Color.FromArgb(120, 255, 160)))
        using (SolidBrush amb = new SolidBrush(Color.FromArgb(255, 205, 90)))
        {
            g.DrawString("ROLL " + roll.ToString("0.0") + " deg", f, w, 14, 16);
            g.DrawString("PITCH " + pit.ToString("0") + " px", f, w, 14, 36);
            g.DrawString("HORIZON ID " + idpct.ToString("0") + "%", f, idpct >= 45 ? grn : amb, 14, 56);
            string st2 = !flight ? "ON FOOT / IDLE" : (uav ? "UAV LINK" : "FPV LINK");
            g.DrawString(st2 + (hud ? " + HUD" : ""), f, dim, 14, 76);

            string s2 = "side  roll " + r2.ToString("0.0") + "  pitch " + p2.ToString("0") + "px  conf " + c2.ToString("0.00");
            g.DrawString(s2, fs, dim, 14, 396);
            string s3 = "learn P " + gainP.ToString("0.00") + " (n" + gainPn + ")   R " + gainR.ToString("0.00") + " (n" + gainRn + ")   rng " + rngM.ToString("0") + "m";
            g.DrawString(s3, fs, dim, 14, 412);
            g.DrawString(fresh ? "live data" : (status.Length > 0 ? status : "stale"), fs, fresh ? grn : amb, 14, 432);
        }

        // id bar
        using (Pen bar = new Pen(Color.FromArgb(90, 8, 10, 14), 8))
            g.DrawLine(bar, 14, 388, W - 14, 388);
        using (Pen bar2 = new Pen(Color.FromArgb(200, 120, 255, 160), 6))
            g.DrawLine(bar2, 14, 388, 14 + (int)((W - 28) * Math.Min(100, Math.Max(0, idpct)) / 100.0), 388);
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new AttitudeScope());
    }
}
