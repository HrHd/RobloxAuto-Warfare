using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

class RefEditor : Form
{
    ListBox list;
    PictureBox pic;
    NumericUpDown numRoll, numPitch;
    Label lblInfo;
    Label lblSaved;
    bool dirty = false;
    string dir, dirMarked;
    float roll = 0f, pitch = 0f;
    Bitmap img;
    float sc = 1f; int ox = 0, oy = 0;
    bool drag = false;
    int dragMode = 0;                       // 0 = anywhere translate, 1 = centre dot, 2 = side dot
    int h1x, h1y, h2x, h2y;                 // handle positions in display coords (set each paint)
    TrackBar tbTrees;
    Label lblTreeLevel;
    bool loadingRef = false;                // guard: slider changes during a load must not rename

    public RefEditor()
    {
        Text = "Ref Editor - drag the line onto the real horizon, S = save";
        Width = 1420; Height = 1000;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        dir = Path.Combine(Application.StartupPath, "hudref");
        dirMarked = Path.Combine(Application.StartupPath, "hudref_marked");

        list = new ListBox();
        list.SetBounds(8, 8, 320, Height - 90);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
        list.SelectedIndexChanged += delegate { LoadRef(); };
        Controls.Add(list);

        pic = new PictureBox();
        pic.SetBounds(336, 34, Width - 372, Height - 116);
        pic.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.Paint += PicPaint;
        pic.MouseDown += delegate(object s, MouseEventArgs e)
        {
            drag = true;
            int d1 = (e.X - h1x) * (e.X - h1x) + (e.Y - h1y) * (e.Y - h1y);
            int d2 = (e.X - h2x) * (e.X - h2x) + (e.Y - h2y) * (e.Y - h2y);
            if (d2 < 900 && d2 <= d1) dragMode = 2;
            else if (d1 < 900) dragMode = 1;
            else dragMode = 0;
            SetFromMouse(e);
        };
        pic.MouseMove += delegate(object s, MouseEventArgs e) { if (drag) SetFromMouse(e); };
        pic.MouseUp += delegate { drag = false; dragMode = 0; };
        Controls.Add(pic);

        // SAVED flag above the photo: green right after S, orange as soon as anything changes.
        lblSaved = new Label();
        lblSaved.SetBounds(336, 6, 352, 24);
        lblSaved.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblSaved.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblSaved.ForeColor = Color.FromArgb(0, 220, 80);
        lblSaved.Text = "";
        Controls.Add(lblSaved);

        Label lr = new Label(); lr.Text = "roll"; lr.SetBounds(340, Height - 74, 40, 22); Controls.Add(lr);
        numRoll = new NumericUpDown(); numRoll.DecimalPlaces = 1; numRoll.Increment = 0.5m;
        numRoll.Minimum = -180; numRoll.Maximum = 180; numRoll.SetBounds(384, Height - 76, 90, 24);
        numRoll.ValueChanged += delegate { if (!drag) { roll = (float)numRoll.Value; pic.Invalidate(); MarkDirty(); } };
        Controls.Add(numRoll);

        Label lp = new Label(); lp.Text = "pitch"; lp.SetBounds(490, Height - 74, 44, 22); Controls.Add(lp);
        numPitch = new NumericUpDown(); numPitch.DecimalPlaces = 0; numPitch.Increment = 2m;
        numPitch.Minimum = -900; numPitch.Maximum = 900; numPitch.SetBounds(538, Height - 76, 90, 24);
        numPitch.ValueChanged += delegate { if (!drag) { pitch = (float)numPitch.Value; pic.Invalidate(); MarkDirty(); } };
        Controls.Add(numPitch);

        Button bs = new Button(); bs.Text = "SAVE (S)"; bs.SetBounds(650, Height - 78, 110, 28);
        bs.Click += delegate { SaveRef(); }; Controls.Add(bs);
        Button br = new Button(); br.Text = "reload"; br.SetBounds(768, Height - 78, 80, 28);
        br.Click += delegate { LoadRef(); }; Controls.Add(br);
        Button bn = new Button(); bn.Text = "next >"; bn.SetBounds(856, Height - 78, 80, 28);
        bn.Click += delegate { if (list.SelectedIndex < list.Items.Count - 1) list.SelectedIndex++; }; Controls.Add(bn);
        Button bd = new Button(); bd.Text = "DELETE (Del)"; bd.SetBounds(944, Height - 78, 108, 28);
        bd.Click += delegate { DeleteRef(); }; Controls.Add(bd);
        Button bg = new Button(); bg.Text = "GROUND (G)"; bg.SetBounds(1060, Height - 78, 112, 28);
        bg.Click += delegate { GroundRef(); }; Controls.Add(bg);

        // TREE LEVEL SLIDER above the photo: AUTO / LOW / MED / HIGH - a bar, not a cycle.
        Label lt = new Label(); lt.Text = "trees:"; lt.SetBounds(700, 6, 44, 20);
        lt.Font = new Font("Segoe UI", 9F, FontStyle.Bold); Controls.Add(lt);
        tbTrees = new TrackBar();
        tbTrees.SetBounds(744, 2, 190, 30);
        tbTrees.Minimum = 0; tbTrees.Maximum = 3; tbTrees.TickFrequency = 1;
        tbTrees.SmallChange = 1; tbTrees.LargeChange = 1;
        Controls.Add(tbTrees);
        lblTreeLevel = new Label(); lblTreeLevel.SetBounds(938, 8, 70, 20);
        lblTreeLevel.Font = new Font("Segoe UI", 9F, FontStyle.Bold); Controls.Add(lblTreeLevel);
        tbTrees.ValueChanged += delegate
        {
            lblTreeLevel.Text = TreeName(tbTrees.Value);
            if (!loadingRef) SetTreeLevel(tbTrees.Value);
        };

        lblInfo = new Label(); lblInfo.SetBounds(1286, Height - 74, Width - 1306, 44);
        lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        lblInfo.Text = Hint();
        Controls.Add(lblInfo);

        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) { pitch -= 5; Refresh(); MarkDirty(); }
            else if (e.KeyCode == Keys.Down) { pitch += 5; Refresh(); MarkDirty(); }
            else if (e.KeyCode == Keys.Left) { roll -= 0.5f; Refresh(); MarkDirty(); }
            else if (e.KeyCode == Keys.Right) { roll += 0.5f; Refresh(); MarkDirty(); }
            else if (e.KeyCode == Keys.S) SaveRef();
            else if (e.KeyCode == Keys.Delete) DeleteRef();
            else if (e.KeyCode == Keys.G) GroundRef();
            else if (e.KeyCode == Keys.A && list.SelectedIndex > 0) list.SelectedIndex--;
            else if (e.KeyCode == Keys.D && list.SelectedIndex < list.Items.Count - 1) list.SelectedIndex++;
            else if (e.KeyCode == Keys.PageDown && list.SelectedIndex < list.Items.Count - 1) list.SelectedIndex++;
            else if (e.KeyCode == Keys.PageUp && list.SelectedIndex > 0) list.SelectedIndex--;
        };

        RefreshList();
    }

    const int REF_CAP = 80;

    string Hint()
    {
        return "refs " + list.Items.Count + "/" + REF_CAP +
            "  |  WHITE dot = height | CYAN dot = tilt | trees slider | S save | G ground | Del delete | A/D prev/next";
    }

    void MarkDirty()
    {
        dirty = true;
        if (lblSaved != null) { lblSaved.Text = "UNSAVED - press S"; lblSaved.ForeColor = Color.FromArgb(255, 170, 60); }
    }

    void Refresh()
    {
        numRoll.Value = (decimal)Math.Round(roll, 1);
        numPitch.Value = (decimal)Math.Round(pitch, 0);
        pic.Invalidate();
    }

    void RefreshList()
    {
        list.Items.Clear();
        if (!Directory.Exists(dir)) return;
        string[] fs = Directory.GetFiles(dir, "*.png");
        Array.Sort(fs);
        foreach (string f in fs)
        {
            string nm = Path.GetFileName(f).ToLowerInvariant();
            if (nm.IndexOf("bad") >= 0 || nm.IndexOf("no_") >= 0 || nm.IndexOf("false") >= 0) continue;
            list.Items.Add(Path.GetFileName(f));
        }
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        if (lblInfo != null) lblInfo.Text = Hint();
    }

    string CurPath()
    {
        if (list.SelectedIndex < 0) return null;
        return Path.Combine(dir, (string)list.Items[list.SelectedIndex]);
    }

    void LoadRef()
    {
        string f = CurPath();
        if (f == null) return;
        try
        {
            if (img != null) img.Dispose();
            img = new Bitmap(f);
            roll = 0; pitch = 0;
            string hz = Path.ChangeExtension(f, ".hzn");
            if (File.Exists(hz))
            {
                foreach (string ln in File.ReadAllLines(hz))
                {
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim().ToLowerInvariant();
                    float v;
                    if (!float.TryParse(ln.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                    if (k == "roll") roll = v;
                    else if (k == "pitch") pitch = v;
                }
            }
            Refresh();
            // reflect the tree level in the SLIDER (guarded so it does not rename during load)
            int lv = TreeLevelOf(Path.GetFileName(f));
            loadingRef = true;
            tbTrees.Value = lv;
            lblTreeLevel.Text = TreeName(lv);
            loadingRef = false;
            dirty = false;
            if (lblSaved != null) { lblSaved.Text = "SAVED \u2713"; lblSaved.ForeColor = Color.FromArgb(0, 220, 80); }
        }
        catch { }
    }

    void SetFromMouse(MouseEventArgs e)
    {
        if (img == null) return;
        float nw = img.Width, nh = img.Height;
        float nx = (e.X - ox) / sc, ny = (e.Y - oy) / sc;
        float cx = nw / 2f, cy = nh / 2f;
        float rad = roll * (float)Math.PI / 180f;
        float c2 = (float)Math.Cos(rad); if (Math.Abs(c2) < 0.2f) c2 = 0.2f;
        if (dragMode == 2)
        {
            // SIDE dot: TILT about the centre point - the line's height at the centre stays put
            float y0 = cy + pitch / c2;
            float xb = cx + nw * 0.38f;
            float slope = (ny - y0) / (xb - cx);
            float newRoll = (float)(Math.Atan(slope) * 180.0 / Math.PI);
            if (newRoll > 80f) newRoll = 80f; if (newRoll < -80f) newRoll = -80f;
            roll = newRoll;
            float nc2 = (float)Math.Cos(roll * (float)Math.PI / 180f); if (Math.Abs(nc2) < 0.2f) nc2 = 0.2f;
            pitch = (y0 - cy) * nc2;
        }
        else if (dragMode == 1)
        {
            // CENTRE dot: move the whole line UP/DOWN - the angle is untouched
            pitch = (ny - cy) * c2;
        }
        else
        {
            if (nx < 0 || ny < 0 || nx > nw || ny > nh) return;
            float yOff = ny - cy - (nx - cx) * (float)Math.Tan(rad);
            pitch = yOff * c2;
        }
        MarkDirty();
        Refresh();
    }

    void PicPaint(object s, PaintEventArgs e)
    {
        if (img == null) return;
        int W = pic.Width, H = pic.Height;
        sc = Math.Min((float)W / img.Width, (float)H / img.Height);
        int dw = (int)(img.Width * sc), dh = (int)(img.Height * sc);
        ox = (W - dw) / 2; oy = (H - dh) / 2;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        e.Graphics.DrawImage(img, ox, oy, dw, dh);
        float rad = roll * (float)Math.PI / 180f;
        float c2 = (float)Math.Cos(rad); if (Math.Abs(c2) < 0.2f) c2 = 0.2f;
        float cx = ox + dw / 2f, cy = oy + dh / 2f;
        float yOff = pitch / c2 * sc;
        PointF a = new PointF(cx - (float)Math.Cos(rad) * dw, cy + yOff - (float)Math.Sin(rad) * dw);
        PointF b = new PointF(cx + (float)Math.Cos(rad) * dw, cy + yOff + (float)Math.Sin(rad) * dw);
        using (Pen p = new Pen(Color.FromArgb(235, 0, 255, 60), 3)) e.Graphics.DrawLine(p, a, b);
        using (Pen p2 = new Pen(Color.FromArgb(230, 255, 230, 0), 2))
        {
            e.Graphics.DrawLine(p2, cx - 20, cy + yOff, cx + 20, cy + yOff);
            e.Graphics.DrawLine(p2, cx, cy + yOff - 20, cx, cy + yOff + 20);
        }
        using (Font fo = new Font("Segoe UI", 16, FontStyle.Bold))
        using (SolidBrush sb = new SolidBrush(Color.FromArgb(255, 255, 0, 255)))
            e.Graphics.DrawString("roll=" + roll.ToString("0.0") + "  pitch=" + pitch.ToString("0") + "px   " +
                (string)list.Items[list.SelectedIndex], fo, sb, ox + 16, oy + 12);
        // TWO DRAGGABLE DOTS: white CENTRE dot = whole line up/down (height), cyan SIDE dot = tilt.
        float yc2 = cy + yOff;
        h1x = (int)cx; h1y = (int)yc2;
        float hx2 = cx + dw * 0.38f;
        float hy2 = yc2 + (float)Math.Tan(rad) * (hx2 - cx);
        h2x = (int)hx2; h2y = (int)hy2;
        using (Brush b1 = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
        using (Brush b2 = new SolidBrush(Color.FromArgb(245, 0, 220, 255)))
        {
            e.Graphics.FillEllipse(b1, h1x - 8, h1y - 8, 16, 16);
            e.Graphics.FillEllipse(b2, h2x - 8, h2y - 8, 16, 16);
        }
    }

    static int TreeLevelOf(string bn)
    {
        string s = bn.ToLowerInvariant();
        if (s.IndexOf("-trees-high") >= 0) return 3;
        if (s.IndexOf("-trees-med") >= 0) return 2;
        if (s.IndexOf("-trees-low") >= 0) return 1;
        if (s.IndexOf("-trees") >= 0) return 3;      // legacy "-trees" = HIGH
        if (s.IndexOf("-clear") >= 0) return 1;      // legacy "-clear" = LOW
        return 0;
    }

    static string StripTree(string bn)
    {
        return bn.Replace("-trees-high", "").Replace("-trees-med", "").Replace("-trees-low", "")
                 .Replace("-trees", "").Replace("-clear", "");
    }

    static string TreeName(int lv)
    {
        return lv == 0 ? "AUTO" : (lv == 1 ? "LOW" : (lv == 2 ? "MED" : "HIGH"));
    }

    void SetTreeLevel(int lv)
    {
        string f = CurPath();
        if (f == null) return;
        try
        {
            string bn = Path.GetFileNameWithoutExtension(f);
            if (TreeLevelOf(bn) == lv) return;
            string nb = StripTree(bn) + (lv == 0 ? "" : (lv == 1 ? "-trees-low" : (lv == 2 ? "-trees-med" : "-trees-high")));
            string nf = Path.Combine(dir, nb + ".png");
            if (img != null) { img.Dispose(); img = null; }
            File.Move(f, nf);
            string ohz = Path.ChangeExtension(f, ".hzn");
            if (File.Exists(ohz)) File.Move(ohz, Path.ChangeExtension(nf, ".hzn"));
            try { img = new Bitmap(nf); } catch { }
            lblSaved.Text = "trees = " + (lv == 0 ? "AUTO" : (lv == 1 ? "LOW" : (lv == 2 ? "MED" : "HIGH")));
            lblSaved.ForeColor = Color.FromArgb(255, 170, 60);
            string nowName = Path.GetFileName(nf);
            int i2 = list.SelectedIndex;
            RefreshList();
            int ix = list.Items.IndexOf(nowName);
            if (ix >= 0) list.SelectedIndex = ix;
            else if (i2 < list.Items.Count) list.SelectedIndex = i2;
        }
        catch (Exception ex) { lblInfo.Text = "tree mark failed: " + ex.Message; }
    }

    void GroundRef()
    {
        // Toggle the "-ground" marker: a GROUND ref tells the app this scene has NO horizon, so
        // matching frames COAST instead of locking (no line is enforced from it). Toggle again to
        // turn it back into a normal ref.
        string f = CurPath();
        if (f == null) return;
        try
        {
            string bn = Path.GetFileNameWithoutExtension(f);
            bool isG = bn.IndexOf("-ground") >= 0;
            string nb = isG ? bn.Replace("-ground", "") : bn + "-ground";
            string nf = Path.Combine(dir, nb + ".png");
            if (img != null) { img.Dispose(); img = null; }
            File.Move(f, nf);
            string ohz = Path.ChangeExtension(f, ".hzn");
            if (File.Exists(ohz)) File.Move(ohz, Path.ChangeExtension(nf, ".hzn"));
            try { img = new Bitmap(nf); } catch { }
            lblSaved.Text = isG ? "GROUND marker removed" : "MARKED AS GROUND - matching frames will coast";
            lblSaved.ForeColor = Color.FromArgb(255, 170, 60);
            string nowName = Path.GetFileName(nf);
            int i2 = list.SelectedIndex;
            RefreshList();
            int ix = list.Items.IndexOf(nowName);
            if (ix >= 0) list.SelectedIndex = ix;
            else if (i2 < list.Items.Count) list.SelectedIndex = i2;
        }
        catch (Exception ex) { lblInfo.Text = "ground mark failed: " + ex.Message; }
    }

    void DeleteRef()
    {
        string f = CurPath();
        if (f == null) return;
        try
        {
            // SAFE DELETE: the file is MOVED to hudref\rejected\, not erased - a mistake is one
            // drag away from being restored, and the app ignores nothing there because it only
            // scans the top folder.
            string rej = Path.Combine(dir, "rejected");
            Directory.CreateDirectory(rej);
            int i = list.SelectedIndex;
            // release OUR OWN lock on the file first - the loaded Bitmap holds it open, which made
            // delete fail with "being used by another process".
            if (img != null) { img.Dispose(); img = null; }
            pic.Invalidate();
            File.Move(f, Path.Combine(rej, Path.GetFileName(f)));
            string hz = Path.ChangeExtension(f, ".hzn");
            if (File.Exists(hz)) File.Move(hz, Path.Combine(rej, Path.GetFileName(hz)));
            lblInfo.Text = "deleted " + Path.GetFileName(f) + "  ->  hudref\\rejected\\  (move it back to restore)";
            RefreshList();
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
        }
        catch (Exception ex) { lblInfo.Text = "delete failed: " + ex.Message; }
    }

    void SaveRef()
    {
        string f = CurPath();
        if (f == null) return;
        try
        {
            // MANUAL EDIT -> RENAME (field): a hand-adjusted ref gets "-edit" in its name so the
            // library shows at a glance which lines a human set. Never double-suffixed.
            bool wasDirty = dirty;
            if (wasDirty && Path.GetFileNameWithoutExtension(f).IndexOf("-edit") < 0)
            {
                string bn = Path.GetFileNameWithoutExtension(f);
                string nf = Path.Combine(dir, bn + "-edit.png");
                if (img != null) { img.Dispose(); img = null; }   // release our own lock before renaming
                File.Move(f, nf);
                string ohz = Path.ChangeExtension(f, ".hzn");
                if (File.Exists(ohz)) File.Move(ohz, Path.ChangeExtension(nf, ".hzn"));
                f = nf;
                try { img = new Bitmap(f); } catch { }             // reload from the new name
            }
            File.WriteAllText(Path.ChangeExtension(f, ".hzn"),
                "roll=" + roll.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\r\npitch=" + pitch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // keep the marked copy in step if the folder exists
            try
            {
                Directory.CreateDirectory(dirMarked);
                using (Bitmap b = new Bitmap(f))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        float rad = roll * (float)Math.PI / 180f;
                        float c2 = (float)Math.Cos(rad); if (Math.Abs(c2) < 0.2f) c2 = 0.2f;
                        float W2 = b.Width, H2 = b.Height, cxx = W2 / 2f, cyy = H2 / 2f;
                        float y2 = pitch / c2;
                        PointF a2 = new PointF(cxx - (float)Math.Cos(rad) * W2, cyy + y2 - (float)Math.Sin(rad) * W2);
                        PointF b2 = new PointF(cxx + (float)Math.Cos(rad) * W2, cyy + y2 + (float)Math.Sin(rad) * W2);
                        using (Pen p = new Pen(Color.FromArgb(235, 0, 255, 60), 5)) g.DrawLine(p, a2, b2);
                        using (Pen p2 = new Pen(Color.FromArgb(230, 255, 230, 0), 3))
                        {
                            g.DrawLine(p2, cxx - 26, cyy + y2, cxx + 26, cyy + y2);
                            g.DrawLine(p2, cxx, cyy + y2 - 26, cxx, cyy + y2 + 26);
                        }
                        using (Font fo = new Font("Segoe UI", 26, FontStyle.Bold))
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(255, 255, 0, 255)))
                            g.DrawString("roll=" + roll.ToString("0.0") + "  pitch=" + pitch.ToString("0") + "px   " +
                                Path.GetFileName(f), fo, sb, 24, 24);
                    }
                    b.Save(Path.Combine(dirMarked, Path.GetFileName(f)), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
            dirty = false;
            if (lblSaved != null) { lblSaved.Text = "SAVED  \u2713"; lblSaved.ForeColor = Color.FromArgb(0, 220, 80); }
            // keep the list in step when the name changed (and the selection on the same ref)
            string nowName = Path.GetFileName(f);
            if (list.Items.Count > 0 && !string.Equals((string)list.Items[list.SelectedIndex], nowName))
            {
                int i2 = list.SelectedIndex;
                RefreshList();
                int ix = list.Items.IndexOf(nowName);
                if (ix >= 0) list.SelectedIndex = ix;
                else if (i2 < list.Items.Count) list.SelectedIndex = i2;
            }
            lblInfo.Text = "saved " + Path.GetFileName(f) + "  roll=" + roll.ToString("0.0") + " pitch=" + pitch.ToString("0") +
                "px   (hit RELOAD REFS in the main panel to load it live)";
        }
        catch (Exception ex) { lblInfo.Text = "save failed: " + ex.Message; }
    }

    [STAThread]
    static void Main()
    {
        Application.Run(new RefEditor());
    }
}
