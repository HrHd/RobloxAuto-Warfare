using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows.Forms;

// DRONE VIEW - shows the live game frame plus WHERE THE DATA THINKS THE DRONE IS POINTING:
// the fused horizon line is drawn onto the picture (green = horizon ID solid, amber = guessing),
// the colourless side estimate appears as a dashed ghost line. Read-only, does not touch the game.
class DroneView : Form
{
    const int VW = 640, VH = 360;
    Bitmap live = new Bitmap(VW, VH);
    object gate = new object();
    double roll, pit, idpct, r2, p2, c2, gainP, gainR, skyE, rngM;
    int gainPn, gainRn;
    bool flight, uav, alive, appear;
    string status = "connecting...";
    long lastAt = 0;

    public DroneView()
    {
        Text = "DRONE VIEW - live frame + predicted horizon";
        ClientSize = new Size(VW, VH);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(1980, 660);

        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();

        System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
        tm.Interval = 40;
        tm.Tick += delegate { Invalidate(); };
        tm.Start();
    }

    void Loop()
    {
        WebClient wc = new WebClient();
        Rectangle scr = Screen.PrimaryScreen.Bounds;
        Bitmap src = new Bitmap(scr.Width, scr.Height);
        Graphics sg = Graphics.FromImage(src);
        Graphics dg = Graphics.FromImage(live);
        dg.InterpolationMode = InterpolationMode.Bilinear;
        while (true)
        {
            try
            {
                string s = wc.DownloadString("http://localhost:8730/state");
                roll = Num(s, "roll"); pit = Num(s, "pit"); idpct = Num(s, "idpct");
                r2 = Num(s, "roll2"); p2 = Num(s, "pit2"); c2 = Num(s, "c2");
                gainP = Num(s, "gainP"); gainR = Num(s, "gainR");
                gainPn = (int)Num(s, "gainPn"); gainRn = (int)Num(s, "gainRn");
                skyE = Num(s, "skyE"); appear = s.IndexOf("\"appear\":true") >= 0;
                rngM = Num(s, "range");
                flight = s.IndexOf("\"flight\":true") >= 0;
                hud = s.IndexOf("\"hud\":true") >= 0;
                uav = s.IndexOf("\"uav\":true") >= 0;
                lastAt = Environment.TickCount;
                alive = true; status = "";
            }
            catch (Exception e) { alive = false; status = "no app data: " + e.Message; }
            try
            {
                sg.CopyFromScreen(scr.X, scr.Y, 0, 0, src.Size);
                lock (gate) dg.DrawImage(src, 0, 0, VW, VH);
            }
            catch { }
            Thread.Sleep(100);
        }
    }

    bool hud;

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
        bool fresh = alive && Environment.TickCount - lastAt < 1500;
        float sc = VH / 1080f;
        int cx = VW / 2, cy = VH / 2;

        lock (gate) g.DrawImageUnscaled(live, 0, 0);

        using (Pen sh = new Pen(Color.FromArgb(120, 0, 0, 0), 6))
        using (Pen ln = new Pen(idpct >= 45 ? Color.FromArgb(220, 120, 255, 150) : Color.FromArgb(200, 255, 200, 80), 3))
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy + (float)(pit * sc));
            g.RotateTransform((float)roll);
            g.DrawLine(sh, -450, 0, 450, 0);
            g.DrawLine(ln, -450, 0, 450, 0);
            g.Restore(st);
        }
        if (fresh && c2 > 0.10)
        {
            using (Pen gh = new Pen(Color.FromArgb(160, 255, 170, 60), 2))
            {
                gh.DashStyle = DashStyle.Dash;
                GraphicsState st = g.Save();
                g.TranslateTransform(cx, cy + (float)(p2 * sc));
                g.RotateTransform((float)r2);
                g.DrawLine(gh, -450, 0, 450, 0);
                g.Restore(st);
            }
        }

        using (Pen ret = new Pen(Color.FromArgb(220, 255, 255, 255), 2))
        using (Pen retb = new Pen(Color.FromArgb(150, 0, 0, 0), 4))
        {
            g.DrawLine(retb, cx - 22, cy, cx - 7, cy); g.DrawLine(retb, cx + 7, cy, cx + 22, cy);
            g.DrawLine(retb, cx, cy - 22, cx, cy - 7); g.DrawLine(retb, cx, cy + 7, cx, cy + 22);
            g.DrawLine(ret, cx - 22, cy, cx - 7, cy); g.DrawLine(ret, cx + 7, cy, cx + 22, cy);
            g.DrawLine(ret, cx, cy - 22, cx, cy - 7); g.DrawLine(ret, cx, cy + 7, cx, cy + 22);
        }

        using (Font f = new Font("Consolas", 10F, FontStyle.Bold))
        using (Font fs = new Font("Consolas", 8F))
        using (SolidBrush w = new SolidBrush(Color.White))
        using (SolidBrush bb = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
        using (SolidBrush grn = new SolidBrush(Color.FromArgb(150, 255, 160)))
        using (SolidBrush amb = new SolidBrush(Color.FromArgb(255, 205, 90)))
        using (SolidBrush dim2 = new SolidBrush(Color.FromArgb(170, 190, 195)))
        {
            string s1 = "ROLL " + roll.ToString("0.0") + "  PITCH " + pit.ToString("0") + "px  ID " + idpct.ToString("0") + "%";
            g.FillRectangle(bb, 0, 0, VW, 20);
            g.DrawString(s1, f, idpct >= 45 ? grn : amb, 6, 3);
            string s2 = (flight ? (uav ? "UAV" : "FPV") : "IDLE") + (hud ? " +HUD" : "") +
                        "   side r" + r2.ToString("0") + " p" + p2.ToString("0") + " c" + c2.ToString("0.00") +
                        "   " + (fresh ? "live" : (status.Length > 0 ? status : "stale"));
            g.FillRectangle(bb, 0, VH - 30, VW, 30);
            g.DrawString(s2, fs, fresh ? w : amb, 6, VH - 29);
            string s4 = "learn P " + gainP.ToString("0.00") + " (n" + gainPn + ")  R " + gainR.ToString("0.00") +
                        " (n" + gainRn + ")   skyE " + (skyE * 100.0).ToString("0") + "%   rng " + rngM.ToString("0") + "m";
            g.DrawString(s4, fs, dim2, 6, VH - 15);
            if (appear) g.DrawString("WRONG-LOCK DE-TRUSTED", fs, amb, VW - 160, VH - 15);
        }
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new DroneView());
    }
}
