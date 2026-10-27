// MidnightEssentials Updater 2.0
// C# WinForms on .NET Framework 4.5+ (built into Windows 10/11): one .exe, nothing to install.
//
// - Reads the releases of github.com/NotOpti/MidnightEssentials (tag = version, one zip each).
// - The folder count comes from the latest release itself: the updater reads only the zip's
//   file index (a few KB, HTTP range request), so adding or removing a module needs no rebuild.
// - Update swaps ONLY the MidnightEssentials folders (MidnightEssentials + ME_*) in
//   _retail_\Interface\AddOns. Old module folders that a release dropped are removed.
//   Settings live in WTF, which is never touched.
// - What's New renders each release's notes (Markdown) from GitHub.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MEUpdater
{
    // ======================================================================
    //  Look
    // ======================================================================
    static class Theme
    {
        public static readonly Color Chrome     = Color.FromArgb(11, 12, 16);
        public static readonly Color Bg         = Color.FromArgb(15, 16, 21);
        public static readonly Color Surface    = Color.FromArgb(21, 22, 29);
        public static readonly Color SurfaceHi  = Color.FromArgb(28, 29, 38);
        public static readonly Color Line       = Color.FromArgb(37, 38, 49);
        public static readonly Color LineHi     = Color.FromArgb(56, 58, 76);
        public static readonly Color Text       = Color.FromArgb(236, 236, 241);
        public static readonly Color TextDim    = Color.FromArgb(178, 179, 193);
        public static readonly Color Muted      = Color.FromArgb(124, 126, 143);
        public static readonly Color Faint      = Color.FromArgb(84, 86, 102);
        public static readonly Color Accent     = Color.FromArgb(150, 92, 240);   // fills
        public static readonly Color AccentHover= Color.FromArgb(165, 112, 246);
        public static readonly Color AccentDown = Color.FromArgb(128, 74, 222);
        public static readonly Color AccentText = Color.FromArgb(196, 152, 252);  // text on dark
        public static readonly Color Good       = Color.FromArgb(92, 202, 136);
        public static readonly Color Warn       = Color.FromArgb(232, 178, 86);
        public static readonly Color Bad        = Color.FromArgb(236, 98, 98);
        public static readonly Color Fix        = Color.FromArgb(94, 190, 206);

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    static class Dpi
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }
    }

    // Inter (Regular / Medium / SemiBold; renamed "ME Inter ..." so each weight is its own
    // family on every system and never clashes with an installed Inter), embedded in the .exe and registered for both
    // GDI+ and GDI, so every PC renders the same type. Consolas for the activity log.
    static class Fonts
    {
        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

        static readonly PrivateFontCollection pfc = new PrivateFontCollection();
        static bool loaded;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            foreach (string res in new[] { "MEUpdater.inter-regular.ttf", "MEUpdater.inter-medium.ttf", "MEUpdater.inter-semibold.ttf" })
            {
                try
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                    {
                        if (s == null) continue;
                        byte[] data = new byte[s.Length];
                        s.Read(data, 0, data.Length);
                        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);   // kept for the app's lifetime
                        Marshal.Copy(data, 0, mem, data.Length);
                        pfc.AddMemoryFont(mem, data.Length);
                        try { uint n = 0; AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref n); } catch { }
                    }
                }
                catch { }
            }
        }

        static Font Try(Func<Font> make)
        {
            try { Font f = make(); f.GetHeight(); return f; } catch { return null; }
        }

        // The embedded face first, then an installed copy, then Segoe UI.
        static Font Face(string family, float size, string fallback, FontStyle fallbackStyle)
        {
            Load();
            string key = family + "|" + size;
            Font f;
            if (cache.TryGetValue(key, out f)) return f;
            foreach (FontFamily ff in pfc.Families)
                if (string.Equals(ff.Name, family, StringComparison.OrdinalIgnoreCase))
                { f = Try(() => new Font(ff, size, FontStyle.Regular, GraphicsUnit.Point)); break; }
            if (f == null)
            {
                Font n = Try(() => new Font(family, size, FontStyle.Regular, GraphicsUnit.Point));
                if (n != null && string.Equals(n.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase)) f = n;
            }
            if (f == null) f = Try(() => new Font(fallback, size, fallbackStyle, GraphicsUnit.Point)) ?? SystemFonts.DefaultFont;
            cache[key] = f;
            return f;
        }

        public static Font Ui(float size)      { return Face("ME Inter", size, "Segoe UI", FontStyle.Regular); }
        public static Font UiMedium(float size){ return Face("ME Inter Medium", size, "Segoe UI", FontStyle.Regular); }
        public static Font UiSemi(float size)  { return Face("ME Inter SemiBold", size, "Segoe UI Semibold", FontStyle.Regular); }
        public static Font Display(float size) { return Face("ME Inter SemiBold", size, "Segoe UI Semibold", FontStyle.Regular); }

        public static Font Mono(float size)
        {
            Font f = Face("Consolas", size, "Courier New", FontStyle.Regular);
            return f;
        }
    }

    // Text drawn with GDI (TextRenderer): crisp ClearType on Windows.
    static class Txt
    {
        public const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
        static readonly Dictionary<Font, Dictionary<string, int>> widths = new Dictionary<Font, Dictionary<string, int>>();

        public static void Draw(Graphics g, string s, Font f, Color c, int x, int y)
        {
            TextRenderer.DrawText(g, s, f, new Point(x, y), c, Base);
        }

        public static void DrawIn(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, s, f, r, c, Base | TextFormatFlags.EndEllipsis | extra);
        }

        public static int Width(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Dictionary<string, int> d;
            if (!widths.TryGetValue(f, out d)) { d = new Dictionary<string, int>(); widths[f] = d; }
            int w;
            if (d.TryGetValue(s, out w)) return w;
            w = TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), Base).Width;
            d[s] = w;
            return w;
        }

        public static int SpaceWidth(Font f)
        {
            return Math.Max(1, Width("a a", f) - Width("aa", f));
        }

        public static int Height(Font f)
        {
            return TextRenderer.MeasureText("Ag", f, new Size(int.MaxValue, int.MaxValue), Base).Height;
        }
    }

    static class Shapes
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, rad)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rr = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            using (GraphicsPath p = Round(rr, rad)) using (Pen pen = new Pen(c)) g.DrawPath(pen, p);
        }

        public static void Card(Graphics g, Rectangle r, float rad, Color fill, Color edge)
        {
            Fill(g, r, rad, fill);
            Stroke(g, r, rad, edge);
        }

        public static void Dot(Graphics g, float cx, float cy, float d, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
        }

        public static void Ring(Graphics g, float cx, float cy, float d, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1f, Dpi.Scale * 1.2f))) g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
        }

        // Small right arrow (links) and up-right arrow (external).
        public static void Arrow(Graphics g, float x, float cy, float size, Color c, bool external)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(c, Math.Max(1.2f, Dpi.Scale * 1.4f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                if (external)
                {
                    float y0 = cy - size / 2, x1 = x + size;
                    g.DrawLine(p, x, cy + size / 2, x1, y0);
                    g.DrawLine(p, x + size * 0.35f, y0, x1, y0);
                    g.DrawLine(p, x1, y0, x1, y0 + size * 0.65f);
                }
                else
                {
                    float x1 = x + size;
                    g.DrawLine(p, x, cy, x1, cy);
                    g.DrawLine(p, x1 - size * 0.4f, cy - size * 0.4f, x1, cy);
                    g.DrawLine(p, x1 - size * 0.4f, cy + size * 0.4f, x1, cy);
                }
            }
        }

        public static void Pill(Graphics g, string text, Font f, Color color, int x, int cy, out int width)
        {
            int padX = Dpi.S(7), h = Dpi.S(18);
            int tw = Txt.Width(text, f);
            width = tw + padX * 2;
            Rectangle r = new Rectangle(x, cy - h / 2, width, h);
            Fill(g, r, h / 2f, Color.FromArgb(38, color));
            Txt.DrawIn(g, text, f, color, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ======================================================================
    //  Controls
    // ======================================================================
    class Canvas : Control
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
    }

    // Double-buffered panel for custom painted areas.
    class Pane : Panel
    {
        public Pane()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
    }

    enum ButtonKind { Primary, Secondary, Link, Window, Close }
    enum Glyph { None, Minimize, Close, Arrow, External }

    class FlatButton : Canvas
    {
        public ButtonKind Kind;
        public Glyph Glyph;
        bool hover, down;

        public FlatButton(ButtonKind kind, string text)
        {
            Kind = kind;
            Text = text;
            Cursor = Cursors.Hand;
            Font = kind == ButtonKind.Link ? Fonts.UiMedium(9f) : Fonts.UiMedium(9.5f);
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // two quick clicks = two clicks
        }

        // Link buttons size themselves to their text.
        public void FitToText()
        {
            int w = Txt.Width(Text, Font) + (Glyph != Glyph.None ? Dpi.S(16) : 0) + Dpi.S(2);
            Width = w;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        // Some runtimes still report a fast second click as a double-click: treat it as a click.
        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); OnClick(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parentBg = BackColor;
            g.Clear(parentBg);
            Rectangle r = ClientRectangle;
            float rad = Dpi.S(6);
            Color text = Theme.Text;

            switch (Kind)
            {
                case ButtonKind.Primary:
                    if (!Enabled) { Shapes.Fill(g, r, rad, Theme.SurfaceHi); text = Theme.Muted; }
                    else
                    {
                        Shapes.Fill(g, r, rad, down ? Theme.AccentDown : (hover ? Theme.AccentHover : Theme.Accent));
                        // A hairline of light along the top edge gives the button some body.
                        using (Pen p = new Pen(Color.FromArgb(down ? 0 : 40, 255, 255, 255)))
                            g.DrawLine(p, r.X + rad, r.Y + 1, r.Right - rad - 1, r.Y + 1);
                        text = Color.White;
                    }
                    break;
                case ButtonKind.Secondary:
                    Shapes.Card(g, r, rad, !Enabled ? Theme.Surface : (down ? Theme.Surface : (hover ? Theme.SurfaceHi : Theme.Surface)),
                        hover && Enabled ? Theme.LineHi : Theme.Line);
                    text = Enabled ? Theme.Text : Theme.Muted;
                    break;
                case ButtonKind.Link:
                    text = !Enabled ? Theme.Muted : (hover ? Theme.Text : Theme.AccentText);
                    break;
                case ButtonKind.Window:
                    if (hover) Shapes.Fill(g, r, rad, down ? Theme.Surface : Theme.SurfaceHi);
                    text = hover ? Theme.Text : Theme.Muted;
                    break;
                case ButtonKind.Close:
                    if (hover) Shapes.Fill(g, r, rad, down ? Color.FromArgb(160, 36, 28) : Color.FromArgb(196, 43, 28));
                    text = hover ? Color.White : Theme.Muted;
                    break;
            }

            if (Glyph == Glyph.Minimize || Glyph == Glyph.Close)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = r.Width / 2f, cy = r.Height / 2f, h = Dpi.S(5);
                using (Pen p = new Pen(text, Math.Max(1f, Dpi.Scale * 1.1f)))
                {
                    if (Glyph == Glyph.Minimize) g.DrawLine(p, cx - h, cy, cx + h, cy);
                    else { g.DrawLine(p, cx - h, cy - h, cx + h, cy + h); g.DrawLine(p, cx - h, cy + h, cx + h, cy - h); }
                }
                return;
            }

            if (Kind == ButtonKind.Link)
            {
                int th = Txt.Height(Font);
                int y = (r.Height - th) / 2;
                Txt.Draw(g, Text, Font, text, 0, y);
                if (Glyph == Glyph.Arrow || Glyph == Glyph.External)
                {
                    int tw = Txt.Width(Text, Font);
                    float size = Dpi.S(Glyph == Glyph.External ? 7 : 8);
                    float shift = hover && Glyph == Glyph.Arrow ? Dpi.S(2) : 0;
                    Shapes.Arrow(g, tw + Dpi.S(6) + shift, r.Height / 2f + Dpi.S(1), size, text, Glyph == Glyph.External);
                }
                return;
            }

            Txt.DrawIn(g, Text, Font, text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Header tab: text, accent underline when active, optional dot.
    class TabButton : Canvas
    {
        bool active, hover, dot;
        public bool Active { get { return active; } set { active = value; Invalidate(); } }
        public bool Dot { get { return dot; } set { dot = value; Invalidate(); } }

        public TabButton(string text)
        {
            Text = text;
            Font = Fonts.UiMedium(9.5f);
            Cursor = Cursors.Hand;
            BackColor = Theme.Chrome;
            SetStyle(ControlStyles.StandardDoubleClick, false);
        }

        public int PreferredWidth { get { return Txt.Width(Text, Font) + Dpi.S(28) + (dot ? Dpi.S(10) : 0); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Color c = active ? Theme.Text : (hover ? Theme.TextDim : Theme.Muted);
            int tw = Txt.Width(Text, Font), th = Txt.Height(Font);
            int x = Dpi.S(14), y = (Height - th) / 2;
            Txt.Draw(g, Text, Font, c, x, y);
            if (dot) Shapes.Dot(g, x + tw + Dpi.S(7), y + Dpi.S(4), Dpi.S(6), Theme.AccentText);
        }

        // Where this tab's underline sits, in the tab's own coordinates.
        public Rectangle UnderlineBounds { get { return new Rectangle(Dpi.S(14), 0, Txt.Width(Text, Font), 0); } }
    }

    // The accent line under the active tab. It glides to the new tab (ease-out) instead of jumping.
    class TabIndicator : Canvas
    {
        float x, w, fromX, fromW, toX, toW;
        readonly Stopwatch clock = new Stopwatch();
        readonly Timer timer = new Timer { Interval = 15 };
        const float Duration = 260f;

        public TabIndicator()
        {
            BackColor = Theme.Chrome;
            timer.Tick += delegate {
                float t = Math.Min(1f, clock.ElapsedMilliseconds / Duration);
                float e = 1f - (float)Math.Pow(1f - t, 4);    // ease-out quart
                x = fromX + (toX - fromX) * e;
                w = fromW + (toW - fromW) * e;
                if (t >= 1f) timer.Stop();
                Invalidate();
            };
        }

        public void MoveTo(int nx, int nw, bool animate)
        {
            if (!animate || w <= 0) { x = toX = nx; w = toW = nw; timer.Stop(); Invalidate(); return; }
            fromX = x; fromW = w; toX = nx; toW = nw;
            clock.Restart();
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (w > 0) Shapes.Fill(e.Graphics, new RectangleF(x, 0, w, Height + 2), 1, Theme.AccentText);
        }
    }

    // Page change, drawn inside the window: the old page fades out and drifts a few pixels
    // away while the new one fades in from the other side. Both pictures are ready before
    // the click, and each frame is two GDI AlphaBlend calls (the same blit Windows uses for
    // its own fades), so it stays smooth on slow machines.
    class PageTransition : Canvas
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int dx, int dy, int dw, int dh,
            IntPtr src, int sx, int sy, int sw, int sh, int blendFunction);

        Bitmap from, to;
        IntPtr hFrom, hTo, dcFrom, dcTo, oldFrom, oldTo;
        bool gdi;
        int dir;
        readonly Stopwatch clock = new Stopwatch();
        readonly Timer timer = new Timer { Interval = 10 };
        Action done;
        const float Duration = 230f;

        public PageTransition()
        {
            timer.Tick += delegate {
                if (clock.ElapsedMilliseconds >= Duration) Finish();
                else { Invalidate(); Update(); }
            };
        }

        public bool Running { get { return timer.Enabled; } }

        // from/to stay owned by the caller.
        public void Start(Bitmap fromBmp, Bitmap toBmp, int direction, Action onDone)
        {
            Release();
            from = fromBmp; to = toBmp; dir = direction; done = onDone;
            gdi = false;
            try
            {
                hFrom = from.GetHbitmap(); hTo = to.GetHbitmap();
                dcFrom = CreateCompatibleDC(IntPtr.Zero); dcTo = CreateCompatibleDC(IntPtr.Zero);
                oldFrom = SelectObject(dcFrom, hFrom); oldTo = SelectObject(dcTo, hTo);
                gdi = dcFrom != IntPtr.Zero && dcTo != IntPtr.Zero;
            }
            catch { Release(); gdi = false; }
            clock.Restart();
            timer.Start();
            Invalidate();
            Update();
        }

        public void Finish()
        {
            if (!timer.Enabled) return;
            timer.Stop();
            Release();
            from = to = null;
            Action d = done; done = null;
            if (d != null) d();
        }

        void Release()
        {
            try
            {
                if (dcFrom != IntPtr.Zero) { SelectObject(dcFrom, oldFrom); DeleteDC(dcFrom); }
                if (dcTo != IntPtr.Zero) { SelectObject(dcTo, oldTo); DeleteDC(dcTo); }
                if (hFrom != IntPtr.Zero) DeleteObject(hFrom);
                if (hTo != IntPtr.Zero) DeleteObject(hTo);
            }
            catch { }
            dcFrom = dcTo = hFrom = hTo = IntPtr.Zero;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (from == null || to == null) return;
            float t = Math.Min(1f, clock.ElapsedMilliseconds / Duration);
            float tOut = Math.Min(1f, t / 0.45f);                    // old page leaves quickly
            float tIn = Math.Max(0f, (t - 0.15f) / 0.85f);
            float eOut = 1f - (1f - tOut) * (1f - tOut);
            float eIn = 1f - (1f - tIn) * (1f - tIn) * (1f - tIn);   // new page settles in
            float shift = Dpi.S(14);
            int xOut = (int)Math.Round(-dir * shift * eOut), xIn = (int)Math.Round(dir * shift * (1f - eIn));
            int aOut = (int)(255 * (1f - eOut)), aIn = (int)(255 * eIn);

            if (gdi)
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (aOut > 0) AlphaBlend(hdc, xOut, 0, from.Width, from.Height, dcFrom, 0, 0, from.Width, from.Height, aOut << 16);
                    if (aIn > 0) AlphaBlend(hdc, xIn, 0, to.Width, to.Height, dcTo, 0, 0, to.Width, to.Height, aIn << 16);
                    return;
                }
                catch { gdi = false; }
                finally { g.ReleaseHdc(hdc); }
            }
            // Fallback (no msimg32): GDI+ blend.
            DrawFaded(g, from, aOut / 255f, xOut);
            DrawFaded(g, to, aIn / 255f, xIn);
        }

        static void DrawFaded(Graphics g, Bitmap b, float alpha, int dx)
        {
            if (alpha <= 0.01f) return;
            if (alpha >= 0.99f) { g.DrawImageUnscaled(b, dx, 0); return; }
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(b, new Rectangle(dx, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }

    // A TextBox that can skip its background wipe (WM_ERASEBKGND) while a page is revealed
    // after the tab animation: it then draws its text straight over the identical picture,
    // instead of blanking first (which showed as the text blinking).
    class EditBox : TextBox
    {
        public static bool NoErase;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014 && NoErase) { m.Result = (IntPtr)1; return; }   // WM_ERASEBKGND
            base.WndProc(ref m);
        }
    }

    // Pictures of pages for the tab animation.
    //  Capture: the page on screen, exact pixels (BitBlt from the window).
    //  Render:  a page that isn't on screen. On Windows the controls draw themselves
    //           (DrawToBitmap / WM_PRINT), so text boxes look exactly like the real ones;
    //           Mono's DrawToBitmap skips children, so there we paint them one by one.
    static class PageShots
    {
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        static readonly bool Mono = Type.GetType("Mono.Runtime") != null;
        static readonly ShotPainter painter = new ShotPainter();

        public static Bitmap Capture(Control c)
        {
            Form f = c.FindForm();
            if (f == null || !f.IsHandleCreated || c.Width <= 0 || c.Height <= 0) return null;
            Rectangle area = f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
            Bitmap bmp = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                bool ok;
                using (Graphics wg = Graphics.FromHwnd(f.Handle))
                using (Graphics bg = Graphics.FromImage(bmp))
                {
                    IntPtr src = wg.GetHdc(), dst = bg.GetHdc();
                    try { ok = BitBlt(dst, 0, 0, area.Width, area.Height, src, area.X, area.Y, 0x00CC0020); }
                    finally { bg.ReleaseHdc(dst); wg.ReleaseHdc(src); }
                }
                if (ok) return bmp;
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.PointToScreen(area.Location), Point.Empty, area.Size);
                return bmp;
            }
            catch { bmp.Dispose(); return null; }
        }

        public static Bitmap Render(Control page)
        {
            int w = Math.Max(1, page.Width), h = Math.Max(1, page.Height);
            bool was = page.Visible;
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                if (!was) { page.SendToBack(); page.Visible = true; }   // behind the visible page: never seen
                if (!Mono)
                {
                    page.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                    // Text boxes don't always draw their text for DrawToBitmap (multi-line ones
                    // often come out empty), which made the text pop in after the tab animation.
                    // Draw their visible lines ourselves, at the positions the boxes report.
                    using (Graphics g = Graphics.FromImage(bmp)) DrawTextBoxes(page, page, g);
                }
                else
                {
                    using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Theme.Bg); painter.RenderInto(page, g, Point.Empty); }
                }
            }
            catch { }
            finally { if (!was) page.Visible = false; }
            return bmp;
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);
        const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_LINEINDEX = 0xBB, EM_LINELENGTH = 0xC1, EM_POSFROMCHAR = 0xD6, EM_GETLINECOUNT = 0xBA;

        static void DrawTextBoxes(Control root, Control c, Graphics g)
        {
            foreach (Control ch in c.Controls)
            {
                if (!ch.Visible) continue;
                TextBox tb = ch as TextBox;
                if (tb != null && tb.IsHandleCreated) { try { DrawTextBox(root, tb, g); } catch { } }
                else if (ch.HasChildren) DrawTextBoxes(root, ch, g);
            }
        }

        static void DrawTextBox(Control root, TextBox tb, Graphics g)
        {
            Rectangle r = root.RectangleToClient(tb.RectangleToScreen(tb.ClientRectangle));
            GraphicsState st = g.Save();
            g.SetClip(r);
            using (SolidBrush b = new SolidBrush(tb.BackColor)) g.FillRectangle(b, r);
            TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                | TextFormatFlags.PreserveGraphicsClipping;
            string text = tb.Text;
            bool hidden = tb.UseSystemPasswordChar || tb.PasswordChar != '\0';
            if (text.Length > 0)
            {
                int lines = tb.Multiline ? (int)SendMessage(tb.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero) : 1;
                int first = tb.Multiline ? (int)SendMessage(tb.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;
                for (int i = first; i < lines; i++)
                {
                    int idx = tb.Multiline ? (int)SendMessage(tb.Handle, EM_LINEINDEX, (IntPtr)i, IntPtr.Zero) : 0;
                    if (idx < 0 || idx > text.Length) break;
                    int len = tb.Multiline ? (int)SendMessage(tb.Handle, EM_LINELENGTH, (IntPtr)idx, IntPtr.Zero) : text.Length;
                    long pos = (long)SendMessage(tb.Handle, EM_POSFROMCHAR, (IntPtr)idx, IntPtr.Zero);
                    if (pos == -1) continue;
                    int x = (short)(pos & 0xFFFF), y = (short)((pos >> 16) & 0xFFFF);
                    if (y > tb.ClientSize.Height) break;
                    string line = text.Substring(idx, Math.Min(len, text.Length - idx));
                    if (hidden) line = new string('\u25CF', line.Length);
                    TextRenderer.DrawText(g, line, tb.Font, new Point(r.X + x, r.Y + y), tb.ForeColor, f);
                }
            }
            g.Restore(st);
        }

        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Paints a control and everything in it immediately (RDW_UPDATENOW | RDW_ALLCHILDREN).
        public static void PaintNow(Control c)
        {
            try { if (c.IsHandleCreated) RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x0100 | 0x0080); }
            catch { c.Refresh(); }
        }

        // End of a page animation: takes the animation layer away without Windows wiping what
        // it uncovers (SWP_DEFERERASE), then lets the real page paint over the last frame
        // with text boxes skipping their own wipe. Nothing blanks, so nothing blinks.
        public static void Reveal(Control overlay, Control page)
        {
            EditBox.NoErase = true;
            try
            {
                try
                {
                    if (overlay.IsHandleCreated)
                        SetWindowPos(overlay.Handle, IntPtr.Zero, 0, 0, 0, 0,
                            0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080 | 0x2000);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|HIDEWINDOW|DEFERERASE
                }
                catch { }
                overlay.Visible = false;
                PaintNow(page);
            }
            finally { EditBox.NoErase = false; }
        }

        class ShotPainter : Control
        {
            public void RenderInto(Control c, Graphics g, Point at)
            {
                Rectangle local = new Rectangle(0, 0, c.Width, c.Height);
                GraphicsState st = g.Save();
                g.TranslateTransform(at.X, at.Y);
                g.IntersectClip(local);
                TextBox tb = c as TextBox;
                if (tb != null)
                {
                    using (SolidBrush b = new SolidBrush(tb.BackColor)) g.FillRectangle(b, local);
                    string t = tb.UseSystemPasswordChar || tb.PasswordChar != '\0' ? new string('\u25CF', tb.Text.Length) : tb.Text;
                    TextRenderer.DrawText(g, t, tb.Font, local, tb.ForeColor,
                        TextFormatFlags.NoPrefix | (tb.Multiline ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine)
                        | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
                }
                else
                {
                    PaintEventArgs pe = new PaintEventArgs(g, local);   // not disposed: g belongs to the caller
                    InvokePaintBackground(c, pe);
                    InvokePaint(c, pe);
                }
                g.Restore(st);
                for (int i = c.Controls.Count - 1; i >= 0; i--)
                {
                    Control ch = c.Controls[i];
                    if (!ch.Visible || ch.Width <= 0 || ch.Height <= 0) continue;
                    RenderInto(ch, g, new Point(at.X + ch.Left, at.Y + ch.Top));
                }
            }
        }
    }

    // Window edge: rounded corners on Windows 11, and no border line on any version.
    static class WindowEdge
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // No visible border: the window's own edge is the border.
        public static void Apply(IntPtr hwnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }
            // Windows 11 draws a 1px system border round every window; DWMWA_COLOR_NONE removes it.
            try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(hwnd, 34, ref none, 4); } catch { }
        }
    }

    // Thin progress bar with a smoothed value.
    class ProgressLine : Canvas
    {
        float target, shown;
        readonly Timer anim = new Timer { Interval = 15 };

        public ProgressLine()
        {
            anim.Tick += delegate {
                shown += (target - shown) * 0.2f;
                if (Math.Abs(target - shown) < 0.002f) { shown = target; anim.Stop(); }
                Invalidate();
            };
        }

        public float Value
        {
            get { return target; }
            set
            {
                target = Math.Max(0f, Math.Min(1f, value));
                if (target < shown) shown = target;
                if (!anim.Enabled) anim.Start();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            float rad = Height / 2f;
            Shapes.Fill(g, new RectangleF(0, 0, Width, Height), rad, Theme.Line);
            float w = Width * shown;
            if (w >= Height) Shapes.Fill(g, new RectangleF(0, 0, w, Height), rad, Theme.AccentText);
        }
    }

    // Base for scrollable painted views: wheel, thin overlay scrollbar, thumb drag.
    abstract class ScrollCanvas : Canvas
    {
        protected int scrollY;
        int contentH;
        bool overBar, dragging;
        int dragStartY, dragStartScroll;
        public int FadeColorAlpha = 255;

        protected ScrollCanvas()
        {
            SetStyle(ControlStyles.Selectable, true);
        }

        protected int MaxScroll { get { return Math.Max(0, contentH - Height); } }

        protected void SetContentHeight(int h)
        {
            contentH = h;
            scrollY = Math.Max(0, Math.Min(scrollY, MaxScroll));
            Invalidate();
        }

        public void ScrollTo(int y)
        {
            scrollY = Math.Max(0, Math.Min(y, MaxScroll));
            Invalidate();
        }

        public void ScrollToEnd() { ScrollTo(int.MaxValue); }

        protected abstract void PaintContent(Graphics g);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            PaintContent(g);

            // Soft fades where content runs past the edges.
            int fade = Dpi.S(14);
            if (scrollY > 0)
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, -1, Width, fade + 1), BackColor, Color.FromArgb(0, BackColor), LinearGradientMode.Vertical))
                    g.FillRectangle(b, 0, 0, Width, fade);
            if (scrollY < MaxScroll)
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, Height - fade - 1, Width, fade + 1), Color.FromArgb(0, BackColor), BackColor, LinearGradientMode.Vertical))
                    g.FillRectangle(b, 0, Height - fade, Width, fade);

            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty)
                Shapes.Fill(g, thumb, thumb.Width / 2f, overBar || dragging ? Theme.LineHi : Theme.Line);
        }

        Rectangle Thumb()
        {
            if (MaxScroll <= 0 || contentH <= 0) return Rectangle.Empty;
            int pad = Dpi.S(4);
            int w = overBar || dragging ? Dpi.S(6) : Dpi.S(4);
            int track = Height - pad * 2;
            int th = Math.Max(Dpi.S(32), track * Height / contentH);
            int ty = pad + (int)((track - th) * (scrollY / (float)MaxScroll));
            return new Rectangle(Width - pad - w, ty, w, th);
        }

        bool InBar(int x) { return MaxScroll > 0 && x >= Width - Dpi.S(14); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(scrollY - e.Delta * Dpi.S(60) / 120);
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            // Wheel scrolling needs focus on older Windows; don't steal it from a text box.
            Form f = FindForm();
            if (f != null && !(f.ActiveControl is TextBox)) Focus();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && InBar(e.X))
            {
                Rectangle t = Thumb();
                if (e.Y < t.Top) ScrollTo(scrollY - Height + Dpi.S(40));
                else if (e.Y > t.Bottom) ScrollTo(scrollY + Height - Dpi.S(40));
                else { dragging = true; dragStartY = e.Y; dragStartScroll = scrollY; Capture = true; }
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Rectangle t = Thumb();
                int track = Height - Dpi.S(8) - t.Height;
                if (track > 0) ScrollTo(dragStartScroll + (int)((e.Y - dragStartY) * MaxScroll / (float)track));
                return;
            }
            bool over = InBar(e.X);
            if (over != overBar) { overBar = over; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; Capture = false; Invalidate(); return; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (overBar) { overBar = false; Invalidate(); }
            base.OnMouseLeave(e);
        }
    }

    // ----------------------------------------------------------------------
    //  Wrapped rich text: runs of (text, font, color) laid out into lines.
    // ----------------------------------------------------------------------
    class Run
    {
        public string Text; public Font Font; public Color Color;
        public Run(string t, Font f, Color c) { Text = t; Font = f; Color = c; }
    }

    class Frag
    {
        public string Text; public Font Font; public Color Color; public int X, Y;
    }

    static class TextFlow
    {
        // Lays runs out from (0,0) within maxW. Returns the height used.
        public static int Wrap(List<Run> runs, int maxW, int lineH, List<Frag> outFrags)
        {
            int x = 0, y = 0;
            int start = outFrags.Count;
            bool lineHasContent = false;
            foreach (Run r in runs)
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                int space = Txt.SpaceWidth(r.Font);
                int textH = Txt.Height(r.Font);
                int dy = (lineH - textH) / 2;
                foreach (Match m in Regex.Matches(r.Text, @"(\s*)(\S+)|(\s+)$"))
                {
                    string lead = m.Groups[1].Value;
                    string word = m.Groups[2].Value;
                    if (word.Length == 0)
                    {
                        // trailing whitespace of a run: keep the gap before the next run
                        if (lineHasContent) x += space;
                        continue;
                    }
                    if (lead.Length > 0 && lineHasContent) x += space;
                    int w = Txt.Width(word, r.Font);
                    if (lineHasContent && x + w > maxW)
                    {
                        x = 0; y += lineH; lineHasContent = false;
                    }
                    // Words of the same style on the same line are drawn as one string, so the
                    // gaps are the font's own spaces (drawing word by word left uneven gaps on Windows).
                    Frag last = outFrags.Count > start ? outFrags[outFrags.Count - 1] : null;
                    if (last != null && lineHasContent && lead.Length > 0 && last.Y == y + dy && last.Font == r.Font && last.Color == r.Color)
                        last.Text += " " + word;
                    else
                        outFrags.Add(new Frag { Text = word, Font = r.Font, Color = r.Color, X = x, Y = y + dy });
                    x += w;
                    lineHasContent = true;
                }
            }
            return y + lineH;
        }
    }

    // ----------------------------------------------------------------------
    //  Markdown (the subset release notes use)
    // ----------------------------------------------------------------------
    enum BlockKind { Heading, Bullet, Para, Rule }

    class Span { public string Text; public bool Bold; public bool Code; }

    class MdBlock
    {
        public BlockKind Kind;
        public int Level;        // heading level
        public int Indent;       // bullet depth
        public string Raw = "";
        public List<Span> Spans;
        public string Section;   // new / fix / change / other (headings)
    }

    static class Markdown
    {
        public static List<MdBlock> Parse(string body, string tag)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrEmpty(body)) return blocks;
            string[] lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            MdBlock open = null;   // paragraph or bullet that a plain next line continues
            bool inFence = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                if (line.TrimStart().StartsWith("```")) { inFence = !inFence; open = null; continue; }
                if (inFence)
                {
                    blocks.Add(new MdBlock { Kind = BlockKind.Para, Raw = "`" + line.Trim() + "`" });
                    continue;
                }
                string t = line.Trim();
                if (t.Length == 0) { open = null; continue; }
                t = Regex.Replace(t, @"<!--.*?-->", "").Trim();
                if (t.Length == 0) continue;

                Match m;
                if ((m = Regex.Match(t, @"^(#{1,6})\s+(.*?)\s*#*$")).Success)
                {
                    blocks.Add(new MdBlock { Kind = BlockKind.Heading, Level = m.Groups[1].Length, Raw = m.Groups[2].Value });
                    open = null; continue;
                }
                if (Regex.IsMatch(t, @"^(-\s*){3,}$|^(\*\s*){3,}$|^(_\s*){3,}$"))
                {
                    blocks.Add(new MdBlock { Kind = BlockKind.Rule });
                    open = null; continue;
                }
                // "**New**" or "**Fixes:**" on its own line reads as a section heading.
                if ((m = Regex.Match(t, @"^(\*\*|__)([^*_]{1,40}?)(\*\*|__)\s*:?\s*$")).Success)
                {
                    blocks.Add(new MdBlock { Kind = BlockKind.Heading, Level = 3, Raw = m.Groups[2].Value.TrimEnd(':', ' ') });
                    open = null; continue;
                }
                if ((m = Regex.Match(line, @"^(\s*)([-*+•]|\d+[.)])\s+(.*)$")).Success)
                {
                    int lead = m.Groups[1].Value.Replace("\t", "    ").Length;
                    open = new MdBlock { Kind = BlockKind.Bullet, Indent = Math.Min(3, lead / 2), Raw = m.Groups[3].Value.Trim() };
                    blocks.Add(open);
                    continue;
                }
                if (t.StartsWith(">")) t = t.TrimStart('>', ' ');
                if (open != null) { open.Raw += " " + t; continue; }
                open = new MdBlock { Kind = BlockKind.Para, Raw = t };
                blocks.Add(open);
            }

            // A title line that only repeats the version ("MidnightEssentials v9.0.2") is
            // already shown in the header.
            for (int i = 0; i < blocks.Count && i < 2; i++)
            {
                MdBlock b = blocks[i];
                if (b.Kind == BlockKind.Rule) continue;
                string plain = Regex.Replace(b.Raw, @"[*_`#]", "").Trim();
                bool isTitle = Regex.IsMatch(plain, @"^(MidnightEssentials)?\s*[vV]?\d+(\.\d+)+\s*$", RegexOptions.IgnoreCase)
                    || (b.Kind == BlockKind.Heading && b.Level <= 2 && plain.IndexOf("MidnightEssentials", StringComparison.OrdinalIgnoreCase) >= 0);
                if ((b.Kind == BlockKind.Heading || b.Kind == BlockKind.Para) && isTitle) { blocks.RemoveAt(i); i--; }
                else break;
            }

            string section = "other";
            foreach (MdBlock b in blocks)
            {
                b.Spans = Inline(b.Raw);
                if (b.Kind == BlockKind.Heading)
                {
                    section = SectionOf(b.Raw);
                    b.Section = section;
                }
                else b.Section = section;
            }
            return blocks;
        }

        public static string SectionOf(string heading)
        {
            string h = heading.ToLowerInvariant();
            if (Regex.IsMatch(h, @"\bfix|bug")) return "fix";
            if (Regex.IsMatch(h, @"\bnew\b|add|feature")) return "new";
            if (Regex.IsMatch(h, @"chang|improv|updat|tweak|remov")) return "change";
            return "other";
        }

        // **bold**, __bold__, *em*, _em_, `code`, [text](url), ![img](url), <tags>, \escapes
        public static List<Span> Inline(string s)
        {
            var spans = new List<Span>();
            s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
            s = Regex.Replace(s, @"<(https?://[^>]+)>", "$1");
            s = Regex.Replace(s, @"</?[a-zA-Z][^>]*>", "");
            s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");

            var sb = new StringBuilder();
            bool bold = false, code = false;
            Action flush = () => {
                if (sb.Length > 0) { spans.Add(new Span { Text = sb.ToString(), Bold = bold, Code = code }); sb.Clear(); }
            };
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && "*_`#[]()\\-".IndexOf(s[i + 1]) >= 0) { sb.Append(s[++i]); continue; }
                if (c == '`') { flush(); code = !code; continue; }
                if (code) { sb.Append(c); continue; }
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c) { flush(); bold = !bold; i++; continue; }
                if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(s[i - 1]) || i + 1 >= s.Length || !char.IsLetterOrDigit(s[i + 1]))))
                {
                    // single-star / underscore emphasis: shown as plain text
                    bool opener = i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]);
                    bool closer = i > 0 && !char.IsWhiteSpace(s[i - 1]);
                    if (opener || closer) continue;
                }
                sb.Append(c);
            }
            flush();

            // "Module: what changed" -> the module name stands out.
            if (spans.Count > 0 && !spans[0].Bold && !spans[0].Code)
            {
                Match m = Regex.Match(spans[0].Text, @"^([A-Z][\w+&'/().,\- ]{0,38}?):\s+");
                if (m.Success && !m.Groups[1].Value.Contains(". "))
                {
                    string rest = spans[0].Text.Substring(m.Length);
                    spans[0] = new Span { Text = m.Groups[1].Value + ":", Bold = true };
                    spans.Insert(1, new Span { Text = " " + rest });
                }
            }
            return spans;
        }
    }

    // ======================================================================
    //  GitHub
    // ======================================================================
    class Release
    {
        public string Tag, Title, Body, HtmlUrl, ZipUrl;
        public long ZipSize;
        public DateTime Published;   // UTC
        List<MdBlock> parsed;
        public List<MdBlock> Blocks { get { return parsed ?? (parsed = Markdown.Parse(Body, Tag)); } }
    }

    // Minimal JSON reader (objects, arrays, strings, numbers, true/false/null).
    static class Json
    {
        public static object Parse(string s) { int i = 0; object v = Value(s, ref i); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("expected :");
                    i++;
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("expected , or }");
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("expected , or ]");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException("bad JSON value");
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }
    }

    static class GitHub
    {
        public const string Repo = "NotOpti/MidnightEssentials";
        public const string AssetName = "MidnightEssentials.zip";
        public const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
        public const string LatestDownload = "https://github.com/" + Repo + "/releases/latest/download/" + AssetName;
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string UA = "MidnightEssentials-Updater";

        public static string AppData
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MidnightEssentials Updater"); }
        }

        static string CacheFile(string name) { return Path.Combine(AppData, name); }

        public static string ReadCache(string name)
        {
            try { string p = CacheFile(name); if (File.Exists(p)) return File.ReadAllText(p); } catch { }
            return null;
        }

        public static void WriteCache(string name, string text)
        {
            try { Directory.CreateDirectory(AppData); File.WriteAllText(CacheFile(name), text); } catch { }
        }

        public static WebClient NewClient()
        {
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = UA;
            return wc;
        }

        public class ReleaseResult { public List<Release> Releases; public bool FromCache; public string Error; }

        // All published releases, newest first. Uses an ETag so unchanged lists don't count
        // against GitHub's hourly limit, and falls back to the last good copy when offline.
        public static async Task<ReleaseResult> FetchReleases()
        {
            var res = new ReleaseResult();
            string testFile = Environment.GetEnvironmentVariable("ME_UPDATER_RELEASES");
            if (!string.IsNullOrEmpty(testFile) && File.Exists(testFile))
            {
                res.Releases = ParseReleases(File.ReadAllText(testFile));
                return res;
            }

            string cached = ReadCache("releases.json");
            string etag = ReadCache("releases.etag");
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ReleasesApi);
                req.UserAgent = UA;
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                if (cached != null && !string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag.Trim();
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)await req.GetResponseAsync(); }
                catch (WebException wex)
                {
                    resp = wex.Response as HttpWebResponse;
                    if (resp == null) throw;
                }
                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
                    {
                        res.Releases = ParseReleases(cached);
                        return res;
                    }
                    if ((int)resp.StatusCode >= 400)
                        throw new Exception((int)resp.StatusCode == 403 ? "GitHub's hourly limit was reached" : "GitHub answered " + (int)resp.StatusCode);
                    string json;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = await sr.ReadToEndAsync();
                    res.Releases = ParseReleases(json);
                    WriteCache("releases.json", json);
                    WriteCache("releases.etag", resp.Headers[HttpResponseHeader.ETag] ?? "");
                    return res;
                }
            }
            catch (Exception ex)
            {
                res.Error = Friendly(ex);
                if (cached != null)
                {
                    try { res.Releases = ParseReleases(cached); res.FromCache = true; } catch { res.Releases = null; }
                }
                return res;
            }
        }

        public static string Friendly(Exception ex)
        {
            WebException w = ex as WebException;
            if (w != null)
            {
                switch (w.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.ProxyNameResolutionFailure:
                        return "no internet connection";
                    case WebExceptionStatus.Timeout:
                        return "GitHub took too long to answer";
                    case WebExceptionStatus.SecureChannelFailure:
                    case WebExceptionStatus.TrustFailure:
                        return "a secure connection to GitHub couldn't be made";
                }
            }
            return ex.Message.TrimEnd('.');
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) ? v as string : null;
        }

        static bool B(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static List<Release> ParseReleases(string json)
        {
            var list = new List<Release>();
            var arr = Json.Parse(json) as List<object>;
            if (arr == null) throw new Exception("unexpected reply from GitHub");
            foreach (object o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || B(d, "draft") || B(d, "prerelease")) continue;
                var r = new Release
                {
                    Tag = (S(d, "tag_name") ?? "").TrimStart('v', 'V'),
                    Title = S(d, "name"),
                    Body = S(d, "body") ?? "",
                    HtmlUrl = S(d, "html_url"),
                };
                if (r.Tag.Length == 0) continue;
                DateTime pub;
                if (DateTime.TryParse(S(d, "published_at") ?? S(d, "created_at") ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out pub))
                    r.Published = pub;
                object assets;
                if (d.TryGetValue("assets", out assets) && assets is List<object>)
                {
                    string exact = null, anyZip = null; long exactSize = 0, anySize = 0;
                    foreach (object ao in (List<object>)assets)
                    {
                        var a = ao as Dictionary<string, object>;
                        if (a == null) continue;
                        string name = S(a, "name") ?? "", url = S(a, "browser_download_url");
                        object sz; long size = a.TryGetValue("size", out sz) && sz is double ? (long)(double)sz : 0;
                        if (url == null) continue;
                        // MidnightEssentials.zip if present (any case), else any .zip:
                        // GitHub renames uploads (spaces become dots).
                        if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) { exact = url; exactSize = size; }
                        else if (anyZip == null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { anyZip = url; anySize = size; }
                    }
                    r.ZipUrl = exact ?? anyZip;
                    r.ZipSize = exact != null ? exactSize : anySize;
                }
                list.Add(r);
            }
            list.Sort((a, b) => {
                int c = Versions.Compare(b.Tag, a.Tag);
                return c != 0 ? c : b.Published.CompareTo(a.Published);
            });
            return list;
        }

        // ------------------------------------------------------------------
        //  The folders a release ships, read from the zip's index only.
        //  A zip keeps its file list at the very end, so two small range
        //  requests (64 KB tail, then the list itself) are enough.
        // ------------------------------------------------------------------
        public static async Task<List<string>> FetchManifest(Release r)
        {
            if (r == null || r.ZipUrl == null) return null;
            string key = "manifest-" + Regex.Replace(r.Tag, @"[^\w.\-]", "_") + "-" + r.ZipSize + ".txt";
            string cached = ReadCache(key);
            if (cached != null)
            {
                var c = new List<string>();
                foreach (string l in cached.Split('\n')) if (l.Trim().Length > 0) c.Add(l.Trim());
                if (c.Count > 0) return c;
            }
            List<ZipIndexEntry> index = await FetchIndex(r);
            if (index == null) return null;
            var names = new List<string>();
            foreach (ZipIndexEntry en in index) names.Add(en.Name);
            List<string> tops = Installer.TopFolders(names);
            if (tops == null || tops.Count == 0) return null;
            WriteCache(key, string.Join("\n", tops));
            return tops;
        }

        // Every file in a release zip with its CRC32 and size, read from the zip's index only
        // (a zip keeps its file list at the very end: two small range requests, no full download).
        public static async Task<List<ZipIndexEntry>> FetchIndex(Release r)
        {
            if (r == null || r.ZipUrl == null) return null;
            string key = "index-" + Regex.Replace(r.Tag, @"[^\w.\-]", "_") + "-" + r.ZipSize + ".txt";
            string cached = ReadCache(key);
            if (cached != null)
            {
                var c = new List<ZipIndexEntry>();
                foreach (string l in cached.Split('\n'))
                {
                    string[] f = l.Split('\t');
                    if (f.Length == 3) c.Add(new ZipIndexEntry { Crc = uint.Parse(f[0]), Size = long.Parse(f[1]), Name = f[2] });
                }
                if (c.Count > 0) return c;
            }
            try
            {
                // Explicit from-to ranges only (suffix ranges are handled differently by some runtimes).
                long size = r.ZipSize;
                if (size <= 0)
                {
                    RangeResult probe = await GetRange(new Uri(r.ZipUrl), 0, 1);
                    if (probe == null) return null;
                    size = probe.Total;
                }
                long tailFrom = Math.Max(0, size - 65536);
                RangeResult tail = await GetRange(new Uri(r.ZipUrl), tailFrom, size - tailFrom);
                if (tail == null || tail.Start != tailFrom) return null;
                byte[] t = tail.Data;
                long tailStart = tail.Start;
                int eocd = -1;
                for (int i = t.Length - 22; i >= 0; i--)
                    if (t[i] == 0x50 && t[i + 1] == 0x4b && t[i + 2] == 0x05 && t[i + 3] == 0x06) { eocd = i; break; }
                if (eocd < 0) return null;
                long cdSize = BitConverter.ToUInt32(t, eocd + 12);
                long cdOffset = BitConverter.ToUInt32(t, eocd + 16);
                if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || cdSize > 16 * 1024 * 1024) return null;  // zip64: skip
                byte[] cd;
                int cdPos;
                if (cdOffset >= tailStart) { cd = t; cdPos = (int)(cdOffset - tailStart); }
                else
                {
                    RangeResult cdr = await GetRange(new Uri(r.ZipUrl), cdOffset, cdSize);
                    if (cdr == null || cdr.Start != cdOffset) return null;
                    cd = cdr.Data; cdPos = 0;
                }
                var list = new List<ZipIndexEntry>();
                int p = cdPos;
                while (p + 46 <= cd.Length && cd[p] == 0x50 && cd[p + 1] == 0x4b && cd[p + 2] == 0x01 && cd[p + 3] == 0x02)
                {
                    int nl = BitConverter.ToUInt16(cd, p + 28), xl = BitConverter.ToUInt16(cd, p + 30), cl = BitConverter.ToUInt16(cd, p + 32);
                    if (p + 46 + nl > cd.Length) break;
                    list.Add(new ZipIndexEntry
                    {
                        Crc = BitConverter.ToUInt32(cd, p + 16),
                        Size = BitConverter.ToUInt32(cd, p + 24),
                        Name = Encoding.UTF8.GetString(cd, p + 46, nl),
                    });
                    p += 46 + nl + xl + cl;
                }
                if (list.Count == 0) return null;
                var sb = new StringBuilder();
                foreach (ZipIndexEntry en in list) sb.Append(en.Crc).Append('\t').Append(en.Size).Append('\t').Append(en.Name).Append('\n');
                WriteCache(key, sb.ToString());
                return list;
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        public static string LastError;

        class RangeResult { public byte[] Data; public long Start, Total; }

        static async Task<RangeResult> GetRange(Uri uri, long from, long count)
        {
            for (int hop = 0; hop < 6; hop++)
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
                req.UserAgent = UA;
                req.AllowAutoRedirect = false;   // follow by hand so the Range header survives
                req.Timeout = 15000;
                req.AddRange(from, from + count - 1);
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)await req.GetResponseAsync(); }
                catch (WebException wex) { resp = wex.Response as HttpWebResponse; if (resp == null) throw; }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string loc = resp.Headers[HttpResponseHeader.Location];
                        if (string.IsNullOrEmpty(loc)) return null;
                        uri = new Uri(uri, loc);
                        continue;
                    }
                    if (code != 206) return null;   // no range support: don't pull the whole zip
                    string cr = resp.Headers["Content-Range"] ?? "";
                    Match crm = Regex.Match(cr, @"bytes\s+(\d+)-(\d+)/(\d+)");
                    if (!crm.Success) return null;
                    long start = long.Parse(crm.Groups[1].Value), total = long.Parse(crm.Groups[3].Value);
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        await s.CopyToAsync(ms);
                        return new RangeResult { Data = ms.ToArray(), Start = start, Total = total };
                    }
                }
            }
            return null;
        }
    }

    class ZipIndexEntry { public string Name; public uint Crc; public long Size; }

    // The modern Explorer-style folder picker (IFileOpenDialog with FOS_PICKFOLDERS): wide,
    // resizable, with the navigation pane, like other apps. Falls back to the old tree dialog.
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCoClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(); void SetFileTypeIndex(); void GetFileTypeIndex(); void Advise(); void Unadvise();
            void SetOptions(uint fos); void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem psi); void SetFolder(IShellItem psi);
            void GetFolder(); void GetCurrentSelection();
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(); void GetParent();
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(); void Compare();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        // Returns the chosen folder, or null if cancelled.
        public static string Pick(IWin32Window owner, string title, string okLabel, string start)
        {
            try
            {
                IFileOpenDialog d = (IFileOpenDialog)new FileOpenDialogCoClass();
                d.SetOptions(0x20 | 0x40 | 0x800);            // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                d.SetTitle(title);
                if (okLabel != null) d.SetOkButtonLabel(okLabel);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    try
                    {
                        IShellItem folder;
                        SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                        d.SetFolder(folder);
                    }
                    catch { }
                }
                int hr = d.Show(owner != null ? owner.Handle : IntPtr.Zero);
                if (hr != 0) return null;                     // cancelled
                IShellItem result;
                d.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000, out path);  // SIGDN_FILESYSPATH
                return path;
            }
            catch (Exception)
            {
                using (FolderBrowserDialog f = new FolderBrowserDialog())
                {
                    f.Description = title;
                    f.ShowNewFolderButton = false;
                    if (Directory.Exists(start)) f.SelectedPath = start;
                    return f.ShowDialog(owner) == DialogResult.OK ? f.SelectedPath : null;
                }
            }
        }
    }

    static class Versions
    {
        public static int Compare(string a, string b)
        {
            if (a == null || b == null) return a == b ? 0 : (a == null ? -1 : 1);
            string[] pa = a.TrimStart('v', 'V').Split('.'), pb = b.TrimStart('v', 'V').Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(Regex.Match(pa[i], "\\d+").Value, out x);
                if (i < pb.Length) int.TryParse(Regex.Match(pb[i], "\\d+").Value, out y);
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }
    }

    static class Fmt
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static string Date(DateTime utc) { return utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("MMM d, yyyy", En); }
        public static string LongDate(DateTime utc) { return utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("MMMM d, yyyy", En); }

        public static string Ago(DateTime utc)
        {
            if (utc == DateTime.MinValue) return "";
            int days = (int)(DateTime.Now.Date - utc.ToLocalTime().Date).TotalDays;
            if (days <= 0) return "today";
            if (days == 1) return "yesterday";
            if (days < 7) return days + " days ago";
            if (days < 30) { int w = days / 7; return w == 1 ? "last week" : w + " weeks ago"; }
            if (days < 365) { int m = Math.Max(1, days / 30); return m == 1 ? "last month" : m + " months ago"; }
            return "over a year ago";
        }

        public static string Size(long bytes)
        {
            if (bytes <= 0) return "";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }

    // ======================================================================
    //  Install
    // ======================================================================
    static class Installer
    {
        public static bool LooksLikeOurs(string name)
        {
            return string.Equals(name, "MidnightEssentials", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ME_", StringComparison.OrdinalIgnoreCase);
        }

        // The addon folders in a zip listing: at the top, or inside one wrapper folder.
        public static List<string> TopFolders(IEnumerable<string> entryNames)
        {
            string prefix;
            var all = new List<string>(entryNames);
            if (!FindPrefix(all, out prefix)) return null;
            var tops = new List<string>();
            foreach (string raw in all)
            {
                string n = raw.Replace('\\', '/');
                if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = n.Substring(prefix.Length);
                int slash = rel.IndexOf('/');
                if (slash <= 0) continue;
                string top = rel.Substring(0, slash);
                if (LooksLikeOurs(top) && !tops.Exists(x => string.Equals(x, top, StringComparison.OrdinalIgnoreCase))) tops.Add(top);
            }
            return tops;
        }

        public static bool FindPrefix(List<string> names, out string prefix)
        {
            foreach (string raw in names)
            {
                string n = raw.Replace('\\', '/');
                int k = n.IndexOf("MidnightEssentials/MidnightEssentials.toc", StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && (k == 0 || n[k - 1] == '/')) { prefix = n.Substring(0, k); return true; }
            }
            prefix = null;
            return false;
        }

        public static List<string> InstalledFolders(string addOns)
        {
            var list = new List<string>();
            try
            {
                foreach (string dir in Directory.GetDirectories(addOns))
                {
                    string name = Path.GetFileName(dir);
                    if (LooksLikeOurs(name)) list.Add(name);
                }
            }
            catch { }
            return list;
        }

        public static string InstalledVersion(string addOns)
        {
            try
            {
                string toc = Path.Combine(addOns, "MidnightEssentials", "MidnightEssentials.toc");
                if (!File.Exists(toc)) return null;
                foreach (string line in File.ReadAllLines(toc))
                    if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(11).Trim();
            }
            catch { }
            return null;
        }

        // Unzips only MidnightEssentials folders into a fresh staging folder.
        public static List<string> Unpack(string zipPath, string staging)
        {
            TryDelete(staging);
            Directory.CreateDirectory(staging);
            string stagingFull = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var tops = new List<string>();
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                var names = new List<string>();
                foreach (ZipArchiveEntry en in zip.Entries) names.Add(en.FullName);
                string prefix;
                if (!FindPrefix(names, out prefix))
                    throw new Exception("the download is not a MidnightEssentials zip (MidnightEssentials.toc not found).");

                foreach (ZipArchiveEntry en in zip.Entries)
                {
                    string n = en.FullName.Replace('\\', '/');
                    if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = n.Substring(prefix.Length);
                    if (rel.Length == 0) continue;
                    int slash = rel.IndexOf('/');
                    if (slash <= 0) continue;
                    string top = rel.Substring(0, slash);
                    if (!LooksLikeOurs(top)) continue;
                    if (!tops.Contains(top)) tops.Add(top);
                    string dest = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) continue;   // never outside staging
                    if (n.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    en.ExtractToFile(dest, true);
                }
            }
            if (!tops.Contains("MidnightEssentials"))
                throw new Exception("the download is missing the MidnightEssentials folder.");
            return tops;
        }

        public class SwapResult { public int Replaced; public List<string> Removed = new List<string>(); }

        // Every MidnightEssentials folder goes aside (so a module a release dropped doesn't
        // linger), the new ones go in; on any failure the old set is put back.
        public static SwapResult Swap(string addOns, string staging, string backup, List<string> newFolders)
        {
            TryDelete(backup);
            Directory.CreateDirectory(backup);
            var movedOut = new List<string>();
            var movedIn = new List<string>();
            var res = new SwapResult();
            try
            {
                foreach (string dir in Directory.GetDirectories(addOns))
                {
                    string name = Path.GetFileName(dir);
                    if (LooksLikeOurs(name) || newFolders.Contains(name))
                    {
                        Directory.Move(dir, Path.Combine(backup, name));
                        movedOut.Add(name);
                    }
                }
                foreach (string name in newFolders)
                {
                    Directory.Move(Path.Combine(staging, name), Path.Combine(addOns, name));
                    movedIn.Add(name);
                }
                foreach (string name in movedOut)
                {
                    if (newFolders.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) res.Replaced++;
                    else res.Removed.Add(name);
                }
                return res;
            }
            catch
            {
                foreach (string name in movedIn) TryDelete(Path.Combine(addOns, name));
                foreach (string name in movedOut)
                {
                    try { Directory.Move(Path.Combine(backup, name), Path.Combine(addOns, name)); } catch { }
                }
                throw;
            }
        }

        public static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // "ME_AuraBuffReminders" -> "Aura Buff Reminders"; PvP / QoL stay whole.
        public static string Pretty(string folder)
        {
            if (string.Equals(folder, "MidnightEssentials", StringComparison.OrdinalIgnoreCase)) return "Core";
            string s = folder.StartsWith("ME_", StringComparison.OrdinalIgnoreCase) ? folder.Substring(3) : folder;
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z][a-z])", " ");
            return s.Replace('_', ' ');
        }
    }

    // ======================================================================
    //  Views
    // ======================================================================
    enum ModState { Installed, Missing, Old }

    class ModuleItem { public string Folder, Label; public ModState State; }

    // The addon's folders as a grid of chips. Hover shows the real folder name.
    class ModuleGrid : ScrollCanvas
    {
        List<ModuleItem> items = new List<ModuleItem>();
        List<ModuleItem> old = new List<ModuleItem>();
        string when = "when you update";
        readonly List<Frag> noticeFrags = new List<Frag>();
        int noticeH, noticeTextH;
        int hover = -1;
        const int Cols = 4;

        // Leftover folders don't get squeezed into a tile: they get a full-width notice
        // under the grid, so the sentence is always complete.
        public void SetItems(List<ModuleItem> list, List<ModuleItem> leftovers, string removedWhen)
        {
            items = list; old = leftovers ?? new List<ModuleItem>(); when = removedWhen;
            Relayout();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        int ChipH { get { return Dpi.S(28); } }
        int Gap { get { return Dpi.S(6); } }
        int GridH { get { int rows = (items.Count + Cols - 1) / Cols; return rows == 0 ? 0 : rows * (ChipH + Gap) - Gap; } }
        int NoticeTop { get { return GridH + (items.Count > 0 ? Dpi.S(10) : 0); } }
        int TextX { get { return Dpi.S(40); } }

        void Relayout()
        {
            noticeFrags.Clear();
            noticeH = 0;
            if (old.Count > 0 && Width > 0)
            {
                Font b = Fonts.UiSemi(9f), r = Fonts.Ui(9f), m = Fonts.Mono(8f);
                var runs = new List<Run>();
                for (int i = 0; i < old.Count; i++)
                {
                    if (i > 0) runs.Add(new Run(i == old.Count - 1 ? " and " : ", ", r, Theme.TextDim));
                    runs.Add(new Run(old[i].Label, b, Theme.Text));
                    runs.Add(new Run(" (" + old[i].Folder + ")", m, Theme.Muted));
                }
                bool one = old.Count == 1;
                runs.Add(new Run((one ? " is" : " are") + " no longer part of MidnightEssentials and will be removed " + when + ".", r, Theme.TextDim));
                noticeTextH = TextFlow.Wrap(runs, Width - TextX - Dpi.S(16) - Dpi.S(10), Dpi.S(19), noticeFrags);
                noticeH = noticeTextH + Dpi.S(22);
            }
            SetContentHeight(noticeH > 0 ? NoticeTop + noticeH : GridH);
        }

        Rectangle ChipRect(int i)
        {
            int cw = (Width - Dpi.S(10) * 0 - Gap * (Cols - 1)) / Cols;
            int col = i % Cols, row = i / Cols;
            return new Rectangle(col * (cw + Gap), row * (ChipH + Gap) - scrollY, cw, ChipH);
        }

        protected override void PaintContent(Graphics g)
        {
            Font f = Fonts.Ui(9f), fm = Fonts.Mono(8.5f);
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = ChipRect(i);
                if (r.Bottom < 0 || r.Top > Height) continue;
                ModuleItem it = items[i];
                bool hv = i == hover;
                Shapes.Card(g, r, Dpi.S(6), hv ? Theme.SurfaceHi : Theme.Surface, hv ? Theme.LineHi : Theme.Line);

                float cx = r.X + Dpi.S(14), cy = r.Y + r.Height / 2f;
                if (it.State == ModState.Installed) Shapes.Dot(g, cx, cy, Dpi.S(7), Theme.Good);
                else Shapes.Ring(g, cx, cy, Dpi.S(7), Theme.Muted);

                int tx = r.X + Dpi.S(26);
                Color tc = it.State == ModState.Installed ? Theme.Text : Theme.Muted;
                Txt.DrawIn(g, hv ? it.Folder : it.Label, hv ? fm : f, tc,
                    new Rectangle(tx, r.Y, r.Right - Dpi.S(10) - tx, r.Height), TextFormatFlags.VerticalCenter);
            }

            // No notice: a quiet legend explains the dots (only when there's room for it).
            int ly = NoticeTop + Dpi.S(6) - scrollY;
            Font lf2 = Fonts.Ui(8.5f);
            int lh = Txt.Height(lf2);
            if (noticeH == 0 && items.Count > 0 && ly + lh <= Height)
            {
                float lcy = ly + lh / 2f;
                int lx = Dpi.S(10);
                Shapes.Dot(g, lx + Dpi.S(4), lcy, Dpi.S(7), Theme.Good);
                Txt.Draw(g, "Installed", lf2, Theme.Muted, lx + Dpi.S(14), ly);
                lx += Dpi.S(14) + Txt.Width("Installed", lf2) + Dpi.S(18);
                Shapes.Ring(g, lx + Dpi.S(4), lcy, Dpi.S(7), Theme.Muted);
                Txt.Draw(g, "Missing", lf2, Theme.Muted, lx + Dpi.S(14), ly);
                string hint = "Hover a module to see its folder name";
                Txt.Draw(g, hint, lf2, Theme.Faint, Width - Txt.Width(hint, lf2), ly);
            }

            if (noticeH > 0)
            {
                Rectangle n = new Rectangle(0, NoticeTop - scrollY, Width, noticeH);
                Shapes.Card(g, n, Dpi.S(8), Theme.Mix(BackColor, Theme.Warn, 0.07f), Theme.Mix(BackColor, Theme.Warn, 0.30f));
                // Warning mark: ring with an exclamation point, centred on the first line.
                float icx = n.X + Dpi.S(20), icy = n.Y + Dpi.S(11) + Dpi.S(19) / 2f;
                float d = Dpi.S(15);
                Shapes.Ring(g, icx, icy, d, Theme.Warn);
                using (Pen p = new Pen(Theme.Warn, Math.Max(1.4f, Dpi.Scale * 1.6f)))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLine(p, icx, icy - d * 0.24f, icx, icy + d * 0.06f);
                }
                Shapes.Dot(g, icx, icy + d * 0.25f, Math.Max(2f, Dpi.Scale * 2.2f), Theme.Warn);
                foreach (Frag fr in noticeFrags)
                    Txt.Draw(g, fr.Text, fr.Font, fr.Color, n.X + TextX + fr.X, n.Y + Dpi.S(11) + fr.Y);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < items.Count; i++) if (ChipRect(i).Contains(e.Location)) { h = i; break; }
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
    }

    // Timestamped activity log.
    class ActivityLog : ScrollCanvas
    {
        class Entry { public string Time, Text; public Color Color; public List<Frag> Frags = new List<Frag>(); public int H; }
        readonly List<Entry> entries = new List<Entry>();
        int laidOutWidth = -1;

        public void Add(string text, Color color)
        {
            entries.Add(new Entry { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Color = color });
            laidOutWidth = -1;
            Relayout();
            ScrollToEnd();
        }

        public int Count { get { return entries.Count; } }

        int Pad { get { return Dpi.S(14); } }
        int TimeW { get { return Txt.Width("00:00:00", Fonts.Mono(8.5f)) + Dpi.S(14); } }

        void Relayout()
        {
            if (Width <= 0) return;
            int y = Pad;
            int lineH = Dpi.S(19);
            int textW = Width - Pad * 2 - TimeW - Dpi.S(8);
            foreach (Entry e in entries)
            {
                if (laidOutWidth != Width || e.Frags.Count == 0)
                {
                    e.Frags.Clear();
                    e.H = TextFlow.Wrap(new List<Run> { new Run(e.Text, Fonts.Ui(9f), e.Color) }, Math.Max(Dpi.S(80), textW), lineH, e.Frags);
                }
                y += e.H + Dpi.S(4);
            }
            laidOutWidth = Width;
            SetContentHeight(y + Pad - Dpi.S(4));
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); laidOutWidth = -1; Relayout(); }

        protected override void PaintContent(Graphics g)
        {
            int y = Pad - scrollY;
            Font tf = Fonts.Mono(8.5f);
            int lineH = Dpi.S(19);
            int thH = Txt.Height(tf);
            foreach (Entry e in entries)
            {
                if (y + e.H >= 0 && y <= Height)
                {
                    Txt.Draw(g, e.Time, tf, Theme.Faint, Pad, y + (lineH - thH) / 2);
                    int x0 = Pad + TimeW;
                    foreach (Frag f in e.Frags) Txt.Draw(g, f.Text, f.Font, f.Color, x0 + f.X, y + f.Y);
                }
                y += e.H + Dpi.S(4);
            }
        }
    }

    // Release list on the left of What's New.
    class VersionList : ScrollCanvas
    {
        List<Release> releases = new List<Release>();
        string installed;
        int hover = -1, selected;
        public event EventHandler SelectedChanged;

        public VersionList() { BackColor = Theme.Bg; }

        public Release Selected { get { return selected >= 0 && selected < releases.Count ? releases[selected] : null; } }

        public void SetData(List<Release> list, string installedVersion)
        {
            string keep = Selected != null ? Selected.Tag : null;
            releases = list ?? new List<Release>();
            installed = installedVersion;
            selected = 0;
            if (keep != null) { int k = releases.FindIndex(r => r.Tag == keep); if (k >= 0) selected = k; }
            SetContentHeight(releases.Count * ItemH);
        }

        public void SetInstalled(string v) { installed = v; Invalidate(); }

        public void Select(int i)
        {
            if (i < 0 || i >= releases.Count) return;
            if (i != selected)
            {
                selected = i;
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
            int top = i * ItemH;
            if (top < scrollY) ScrollTo(top);
            else if (top + ItemH > scrollY + Height) ScrollTo(top + ItemH - Height);
            Invalidate();
        }

        int ItemH { get { return Dpi.S(54); } }

        protected override void PaintContent(Graphics g)
        {
            Font vf = Fonts.UiSemi(11f), df = Fonts.Ui(8.5f), pf = Fonts.UiMedium(7f);
            for (int i = 0; i < releases.Count; i++)
            {
                int y = i * ItemH - scrollY;
                if (y + ItemH < 0 || y > Height) continue;
                Release r = releases[i];
                Rectangle row = new Rectangle(0, y + Dpi.S(2), Width - Dpi.S(10), ItemH - Dpi.S(4));
                bool sel = i == selected, hv = i == hover;
                if (sel) Shapes.Fill(g, row, Dpi.S(7), Theme.SurfaceHi);
                else if (hv) Shapes.Fill(g, row, Dpi.S(7), Theme.Surface);
                if (sel) Shapes.Fill(g, new RectangleF(row.X, row.Y + Dpi.S(12), Dpi.S(3), row.Height - Dpi.S(24)), Dpi.S(1.5f), Theme.AccentText);

                bool newer = installed != null && Versions.Compare(r.Tag, installed) > 0;
                int x = row.X + Dpi.S(16);
                int vh = Txt.Height(vf);
                int ty = row.Y + Dpi.S(9);
                Txt.Draw(g, r.Tag, vf, sel ? Theme.Text : Theme.TextDim, x, ty);
                int after = x + Txt.Width(r.Tag, vf) + Dpi.S(8);
                int pw;
                if (installed != null && Versions.Compare(r.Tag, installed) == 0)
                    Shapes.Pill(g, "Installed", pf, Theme.Good, after, ty + vh / 2, out pw);
                else if (i == 0)
                    Shapes.Pill(g, "Latest", pf, Theme.AccentText, after, ty + vh / 2, out pw);
                else if (newer)
                    Shapes.Dot(g, after + Dpi.S(3), ty + vh / 2f, Dpi.S(6), Theme.AccentText);
                Txt.Draw(g, Fmt.Date(r.Published), df, Theme.Muted, x, ty + vh + Dpi.S(3));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = (e.Y + scrollY) / ItemH;
            if (h >= releases.Count || e.X > Width - Dpi.S(10)) h = -1;
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && hover >= 0) Select(hover);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { Select(selected + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { Select(selected - 1); e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    // One release's notes.
    class ChangelogView : ScrollCanvas
    {
        Release release;
        string installed;
        bool isLatest;
        readonly List<Item> items = new List<Item>();
        int laidOutWidth = -1;
        Rectangle githubLink;
        bool overLink;

        class Item
        {
            public BlockKind Kind; public string Section; public int Y, H, Indent, Count;
            public string Heading; public List<Frag> Frags = new List<Frag>();
        }

        public bool ShowLink = true;

        public ChangelogView() { BackColor = Theme.Surface; }

        public void SetRelease(Release r, string installedVersion, bool latest)
        {
            release = r; installed = installedVersion; isLatest = latest;
            laidOutWidth = -1;
            scrollY = 0;
            Relayout();
            Invalidate();
        }

        public void SetInstalled(string v) { installed = v; Invalidate(); }

        int Pad { get { return Dpi.S(32); } }
        int HeaderH { get { return Dpi.S(104); } }

        static Color SectionColor(string s)
        {
            switch (s)
            {
                case "new": return Theme.AccentText;
                case "fix": return Theme.Fix;
                case "change": return Theme.Warn;
                default: return Theme.Muted;
            }
        }

        static string SectionLabel(string heading)
        {
            string plain = Regex.Replace(heading, @"[*_`]", "").Trim().TrimEnd(':');
            return plain.Length > 0 ? plain : heading;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width != laidOutWidth) { laidOutWidth = -1; Relayout(); } }

        void Relayout()
        {
            items.Clear();
            if (release == null || Width <= 0) { SetContentHeight(0); return; }
            int w = Width - Pad * 2;
            int y = HeaderH;
            Font body = Fonts.Ui(9.5f), bold = Fonts.UiSemi(9.5f), code = Fonts.Mono(9f);
            int lineH = Dpi.S(21);
            Item lastHeading = null;
            BlockKind prev = BlockKind.Rule;
            bool first = true;

            foreach (MdBlock b in release.Blocks)
            {
                Item it = new Item { Kind = b.Kind, Section = b.Section, Indent = b.Indent };
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        y += first ? Dpi.S(4) : Dpi.S(26);
                        it.Heading = SectionLabel(b.Raw);
                        it.Y = y; it.H = Dpi.S(24);
                        y += it.H + Dpi.S(10);
                        lastHeading = it;
                        break;
                    case BlockKind.Rule:
                        y += Dpi.S(14);
                        it.Y = y; it.H = 1;
                        y += Dpi.S(14);
                        break;
                    case BlockKind.Bullet:
                    case BlockKind.Para:
                    {
                        if (!first && prev != b.Kind && prev != BlockKind.Heading) y += Dpi.S(8);
                        var runs = new List<Run>();
                        bool bullet = b.Kind == BlockKind.Bullet;
                        foreach (Span s in b.Spans)
                            runs.Add(new Run(s.Text, s.Code ? code : (s.Bold ? bold : body),
                                s.Code ? Theme.AccentText : (s.Bold ? Theme.Text : (bullet ? Theme.TextDim : Theme.TextDim))));
                        int indentX = bullet ? Dpi.S(18) + b.Indent * Dpi.S(18) : 0;
                        it.Y = y;
                        it.H = TextFlow.Wrap(runs, Math.Max(Dpi.S(120), w - indentX), lineH, it.Frags);
                        foreach (Frag f in it.Frags) f.X += indentX;
                        y += it.H + (bullet ? Dpi.S(6) : Dpi.S(8));
                        if (bullet && lastHeading != null && b.Indent == 0) lastHeading.Count++;
                        break;
                    }
                }
                prev = b.Kind;
                first = false;
                items.Add(it);
            }
            laidOutWidth = Width;
            SetContentHeight(y + Pad);
        }

        protected override void PaintContent(Graphics g)
        {
            if (release == null) return;
            int x0 = Pad;
            int top = -scrollY;

            // Header
            Font vf = Fonts.Display(22f), sub = Fonts.Ui(9f), pf = Fonts.UiMedium(7.5f), linkF = Fonts.UiMedium(9f);
            int vy = top + Dpi.S(26);
            int vh = Txt.Height(vf);
            Txt.Draw(g, release.Tag, vf, Theme.Text, x0, vy);
            int px = x0 + Txt.Width(release.Tag, vf) + Dpi.S(12);
            int pw;
            int pcy = vy + vh / 2 + Dpi.S(1);
            if (isLatest) { Shapes.Pill(g, "Latest", pf, Theme.AccentText, px, pcy, out pw); px += pw + Dpi.S(6); }
            if (installed != null)
            {
                int c = Versions.Compare(release.Tag, installed);
                if (c == 0) { Shapes.Pill(g, "Installed", pf, Theme.Good, px, pcy, out pw); px += pw + Dpi.S(6); }
                else if (c > 0) { Shapes.Pill(g, "New for you", pf, Theme.Warn, px, pcy, out pw); px += pw + Dpi.S(6); }
            }
            string when = Fmt.LongDate(release.Published);
            string ago = Fmt.Ago(release.Published);
            string line = when.Length > 0 ? "Released " + when + (ago.Length > 0 ? "  \u00B7  " + ago : "") : "";
            Txt.Draw(g, line, sub, Theme.Muted, x0, vy + vh + Dpi.S(6));

            // GitHub link, top right
            githubLink = Rectangle.Empty;
            if (ShowLink) {
            string lt = "View on GitHub";
            int lw = Txt.Width(lt, linkF) + Dpi.S(16);
            int lh = Txt.Height(linkF);
            githubLink = new Rectangle(Width - Pad - lw, vy + (vh - lh) / 2, lw, lh);
            Color lc = overLink ? Theme.Text : Theme.AccentText;
            Txt.Draw(g, lt, linkF, lc, githubLink.X, githubLink.Y);
            Shapes.Arrow(g, githubLink.Right - Dpi.S(8), githubLink.Y + lh / 2f + Dpi.S(1), Dpi.S(7), lc, true);
            }

            using (Pen p = new Pen(Theme.Line))
                g.DrawLine(p, x0, top + HeaderH - Dpi.S(14), Width - Pad, top + HeaderH - Dpi.S(14));

            if (release.Blocks.Count == 0)
                Txt.Draw(g, "No notes were posted for this release.", Fonts.Ui(10f), Theme.Muted, x0, top + HeaderH + Dpi.S(4));

            Font hf = Fonts.UiSemi(11f), cf = Fonts.Ui(8.5f);
            int lineH = Dpi.S(21);
            foreach (Item it in items)
            {
                int y = top + it.Y;
                if (y > Height || y + it.H < 0) continue;
                Color sc = SectionColor(it.Section);
                switch (it.Kind)
                {
                    case BlockKind.Heading:
                    {
                        int th = Txt.Height(hf);
                        Shapes.Fill(g, new RectangleF(x0, y + (it.H - Dpi.S(12)) / 2f, Dpi.S(3), Dpi.S(12)), Dpi.S(1.5f), sc);
                        Txt.Draw(g, it.Heading, hf, Theme.Text, x0 + Dpi.S(12), y + (it.H - th) / 2);
                        if (it.Count > 0)
                        {
                            int hx = x0 + Dpi.S(12) + Txt.Width(it.Heading, hf) + Dpi.S(8);
                            int ch = Txt.Height(cf);
                            Txt.Draw(g, it.Count.ToString(), cf, Theme.Faint, hx, y + (it.H - ch) / 2 + Dpi.S(1));
                        }
                        break;
                    }
                    case BlockKind.Rule:
                        using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, x0, y, Width - Pad, y);
                        break;
                    case BlockKind.Bullet:
                    {
                        float bx = x0 + Dpi.S(4) + it.Indent * Dpi.S(18);
                        float by = y + lineH / 2f;
                        if (it.Indent == 0) Shapes.Dot(g, bx + Dpi.S(2), by, Dpi.S(5), sc);
                        else Shapes.Ring(g, bx + Dpi.S(2), by, Dpi.S(4), Theme.Faint);
                        foreach (Frag f in it.Frags) Txt.Draw(g, f.Text, f.Font, f.Color, x0 + f.X, y + f.Y);
                        break;
                    }
                    case BlockKind.Para:
                        foreach (Frag f in it.Frags) Txt.Draw(g, f.Text, f.Font, f.Color, x0 + f.X, y + f.Y);
                        break;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = githubLink.Contains(e.Location);
            if (over != overLink) { overLink = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && overLink && release != null)
                MainForm.OpenUrl(release.HtmlUrl ?? GitHub.ReleasesPage);
        }
    }

    // ======================================================================
    //  Window
    // ======================================================================
    class MainForm : Form
    {
        const string AppVersion = "2.0";
        const string DefaultWowPath = @"C:\Program Files (x86)\World of Warcraft";
        const int W = 840, H = 694, TitleH = 56;

        static int S(float v) { return Dpi.S(v); }

        Image logo;
        TabButton tabUpdate, tabNews;
        TabIndicator tabLine;
        FlatButton minBtn, closeBtn;

        // Update page
        Pane updatePage, hero;
        FlatButton actionBtn, notesLink, retryLink, browseBtn;
        ProgressLine progress;
        Panel pathPanel;
        TextBox pathBox;
        Pane folderHead, modulesHead;
        ModuleGrid grid;
        Pane logFrame;
        ActivityLog activity;
        bool showingLog;
        Rectangle modulesTabRect, activityTabRect;

        // What's New page
        Pane newsPage, notesFrame;
        VersionList versions;
        ChangelogView changelog;
        FlatButton newsRetry;

        // State
        string addOnsPath, installedVersion;
        List<Release> releases;
        bool releasesLoading = true, releasesFromCache;
        string releasesError;
        List<string> manifest;          // folders the latest release ships
        bool manifestLoading;
        bool busy;
        string heroHead = "", heroSub = "";
        Color heroDot = Theme.Muted;
        string resultHead, resultSub;   // shown after an update until something changes
        Color resultDot;
        int found, total, oldCount;
        bool totalKnown;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        Release Latest { get { return releases != null && releases.Count > 0 ? releases[0] : null; } }

        public MainForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.Scale = g.DpiX / 96f;

            Text = AppTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Fonts.Ui(9.5f);
            ClientSize = new Size(S(W), S(H));
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MEUpdater.logo.png"))
                    if (s != null) logo = Image.FromStream(s);
            }
            catch { logo = null; }

            shotTimer.Tick += ShotTick;
            autoCheckTimer.Tick += delegate { AutoCheck(false); };
            autoCheckTimer.Start();
            BuildChrome();
            BuildUpdatePage();
            BuildNewsPage();
            pages.Add(updatePage);
            pages.Add(newsPage);
#if STUDIO
            studio = new PublishModule(this, updatePage.Bounds, () => addOnsPath, () => RefreshState());
            studio.Changed += delegate { RefreshTabs(); MarkShotsStale(); };
            releasesTab = new ReleasesModule(this, updatePage.Bounds, () => studio.Token);
            releasesTab.Changed += delegate { MarkShotsStale(); };
            // A release published, edited, withdrawn or restored: both release lists refresh.
            studio.Published += async delegate {
                string tag = studio.LastPublished;
                foreach (int wait in new[] { 0, 3000, 10000, 25000, 45000, 70000 })
                {
                    if (wait > 0) await Task.Delay(wait);
                    while (checking) await Task.Delay(200);
                    await CheckEverything();
                    if (Latest != null && tag != null && Versions.Compare(Latest.Tag, tag) >= 0 && releasesTab.Has(tag)) break;
                }
            };
            releasesTab.ReleasesChanged += async delegate { await LoadReleases(); };
            pages.Add(studio.PublishTab);
            pages.Add(releasesTab.Page);
            pages.Add(studio.AccountPage);
#endif
            ShowPage(Environment.GetEnvironmentVariable("ME_UPDATER_TAB") == "whatsnew" ? 1 : 0);
            MouseDown += DragWindow;

            pathBox.Text = LoadSavedPath() ?? DetectWowPath() ?? DefaultWowPath;
            RefreshState();
            Log("Updater " + AppVersion + " ready.", Theme.Muted);

            KeyDown += (s, e) => {
                if (e.KeyCode == Keys.F5 && !busy) { e.Handled = true; CheckEverything().ContinueWith(t => { }); }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                cp.Style |= 0x00020000;        // WS_MINIMIZEBOX: taskbar click minimizes/restores
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEdge.Apply(Handle);
        }

        void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y > S(TitleH)) return;
            try { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------------
        //  Layout
        // ------------------------------------------------------------------
#if STUDIO
        public const string AppTitle = "MidnightEssentials Studio";
        const string Suffix = "Studio";        // shown after the wordmark
        PublishModule studio;
        ReleasesModule releasesTab;
#else
        public const string AppTitle = "MidnightEssentials Updater";
        const string Suffix = null;
#endif
        static Font WordmarkFont { get { return Fonts.UiSemi(12.5f); } }
        static Font SuffixFont { get { return Fonts.Ui(12.5f); } }
        static int SeparatorX
        {
            get
            {
                int x = S(62) + Txt.Width("MidnightEssentials", WordmarkFont);
                if (Suffix != null) x += S(8) + Txt.Width(Suffix, SuffixFont);
                return x + S(22);
            }
        }

        void BuildChrome()
        {
            tabUpdate = new TabButton("Update");
            tabNews = new TabButton("What's new");
            tabs.Add(tabUpdate);
            tabs.Add(tabNews);
#if STUDIO
            tabs.Add(new TabButton("Publish"));
            tabs.Add(new TabButton("Releases"));
            tabs.Add(new TabButton("Account"));
#endif
            for (int i = 0; i < tabs.Count; i++)
            {
                int idx = i;
                tabs[i].Click += delegate { ShowPage(idx); };
                Controls.Add(tabs[i]);
            }
            LayoutTabs();

            int uh = Math.Max(2, S(2));
            tabLine = new TabIndicator();
            tabLine.SetBounds(tabs[0].Left, S(TitleH) - uh, S(W - 100) - tabs[0].Left, uh);
            Controls.Add(tabLine);
            tabLine.BringToFront();

            minBtn = new FlatButton(ButtonKind.Window, "") { Glyph = Glyph.Minimize, BackColor = Theme.Chrome };
            minBtn.SetBounds(S(W - 92), S(10), S(36), S(36));
            minBtn.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(minBtn);
            closeBtn = new FlatButton(ButtonKind.Close, "") { Glyph = Glyph.Close, BackColor = Theme.Chrome };
            closeBtn.SetBounds(S(W - 50), S(10), S(36), S(36));
            closeBtn.Click += delegate {
#if STUDIO
                if (studio.Busy && MessageBox.Show(this, "A release is being published. Close anyway?", AppTitle,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
#endif
                Close();
            };
            Controls.Add(closeBtn);
        }

        // Tabs sit side by side after the separator; widths follow their text and dots.
        void LayoutTabs()
        {
            int x = SeparatorX + S(10);
            foreach (TabButton t in tabs) { t.SetBounds(x, 0, t.PreferredWidth, S(TitleH)); x += t.Width; }
        }

        void RefreshTabs()
        {
            tabNews.Dot = Latest != null && installedVersion != null && Versions.Compare(installedVersion, Latest.Tag) < 0;
#if STUDIO
            tabs[4].Dot = studio.NeedsAttention;
#endif
            LayoutTabs();
            if (current >= 0) { Rectangle u = tabs[current].UnderlineBounds; tabLine.MoveTo(tabs[current].Left + u.X - tabLine.Left, u.Width, false); }
        }

        void BuildUpdatePage()
        {
            int pad = 28, cw = W - pad * 2;
            updatePage = new Pane();
            updatePage.SetBounds(0, S(TitleH + 1), S(W), S(H - TitleH - 2));
            Controls.Add(updatePage);

            // Hero
            hero = new Pane { BackColor = Theme.Bg };
            hero.SetBounds(S(pad), S(24), S(cw), S(142));
            hero.Paint += PaintHero;
            updatePage.Controls.Add(hero);

            actionBtn = new FlatButton(ButtonKind.Primary, "Update") { BackColor = Theme.Surface };
            actionBtn.Font = Fonts.UiSemi(10f);
            actionBtn.SetBounds(S(cw - 24 - 208), S(34), S(208), S(46));
            actionBtn.Click += StartUpdate;
            hero.Controls.Add(actionBtn);

            notesLink = new FlatButton(ButtonKind.Link, "What's new") { Glyph = Glyph.Arrow, BackColor = Theme.Surface };
            notesLink.SetBounds(S(26), S(98), S(200), S(22));
            notesLink.Click += delegate { ShowPage(1); };
            hero.Controls.Add(notesLink);

            retryLink = new FlatButton(ButtonKind.Link, "Try again") { BackColor = Theme.Surface };
            retryLink.SetBounds(S(26), S(98), S(100), S(22));
            retryLink.FitToText();
            retryLink.Visible = false;
            retryLink.Click += async delegate { await LoadReleases(); };
            hero.Controls.Add(retryLink);

            progress = new ProgressLine { BackColor = Theme.Surface, Visible = false };
            progress.SetBounds(S(26), S(110), S(cw - 26 - 24 - 208 - 32), S(4));
            hero.Controls.Add(progress);

            // Folder
            int y = 24 + 142 + 26;
            folderHead = new Pane();
            folderHead.SetBounds(S(pad), S(y), S(cw), S(20));
            folderHead.Paint += PaintFolderHead;
            updatePage.Controls.Add(folderHead);

            pathPanel = new Pane { BackColor = Theme.Surface };
            pathPanel.SetBounds(S(pad), S(y + 28), S(cw - 112), S(40));
            pathPanel.Paint += (s, e) => {
                e.Graphics.Clear(Theme.Bg);
                bool focus = pathBox != null && pathBox.Focused;
                Shapes.Card(e.Graphics, pathPanel.ClientRectangle, S(6), Theme.Surface, focus ? Theme.Mix(Theme.Accent, Theme.Line, 0.25f) : Theme.Line);
            };
            pathBox = new EditBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Fonts.Ui(9.5f),
            };
            pathBox.SetBounds(S(14), (pathPanel.Height - Txt.Height(pathBox.Font)) / 2, pathPanel.Width - S(28), S(20));
            pathBox.TextChanged += delegate { resultHead = null; RefreshState(); };
            pathBox.GotFocus += delegate { pathPanel.Invalidate(); };
            pathBox.LostFocus += delegate { pathPanel.Invalidate(); };
            pathPanel.Controls.Add(pathBox);
            pathPanel.Click += delegate { pathBox.Focus(); };
            updatePage.Controls.Add(pathPanel);

            browseBtn = new FlatButton(ButtonKind.Secondary, "Browse");
            browseBtn.SetBounds(S(pad + cw - 100), S(y + 28), S(100), S(40));
            browseBtn.Click += Browse;
            updatePage.Controls.Add(browseBtn);

            // Modules / Activity
            y += 28 + 40 + 28;
            modulesHead = new Pane();
            modulesHead.SetBounds(S(pad), S(y), S(cw), S(22));
            modulesHead.Paint += PaintModulesHead;
            modulesHead.MouseClick += (s, e) => {
                if (modulesTabRect.Contains(e.Location)) SetLogVisible(false);
                else if (activityTabRect.Contains(e.Location)) SetLogVisible(true);
            };
            modulesHead.MouseMove += (s, e) => {
                modulesHead.Cursor = modulesTabRect.Contains(e.Location) || activityTabRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            };
            updatePage.Controls.Add(modulesHead);

            int gy = y + 34;
            int gh = (H - TitleH - 2) - gy - 26;
            grid = new ModuleGrid();
            grid.SetBounds(S(pad), S(gy), S(cw), S(gh));
            updatePage.Controls.Add(grid);

            logFrame = new Pane { Visible = false };
            logFrame.SetBounds(S(pad), S(gy), S(cw), S(gh));
            logFrame.Paint += (s, e) => {
                e.Graphics.Clear(Theme.Bg);
                Shapes.Card(e.Graphics, logFrame.ClientRectangle, S(8), Theme.Surface, Theme.Line);
            };
            activity = new ActivityLog { BackColor = Theme.Surface };
            activity.SetBounds(1, S(2), logFrame.Width - 2, logFrame.Height - S(4));
            logFrame.Controls.Add(activity);
            updatePage.Controls.Add(logFrame);
        }

        void BuildNewsPage()
        {
            newsPage = new Pane();
            newsPage.SetBounds(0, S(TitleH + 1), S(W), S(H - TitleH - 2));
            newsPage.Paint += PaintNewsPage;
            Controls.Add(newsPage);

            int pad = 24;
            int top = 24;
            int listW = 188;
            int listH = (H - TitleH - 2) - top - pad;

            versions = new VersionList();
            versions.SetBounds(S(pad - 4), S(top + 26), S(listW), S(listH - 26));
            versions.SelectedChanged += delegate { ShowSelectedNotes(); };
            newsPage.Controls.Add(versions);

            notesFrame = new Pane();
            int fx = pad + listW + 12;
            notesFrame.SetBounds(S(fx), S(top), S(W - fx - pad), S(listH));
            notesFrame.Paint += (s, e) => {
                e.Graphics.Clear(Theme.Bg);
                Shapes.Card(e.Graphics, notesFrame.ClientRectangle, S(10), Theme.Surface, Theme.Line);
                if (changelog.Visible) return;
                // Loading / error state.
                string msg = releasesLoading ? "Loading release notes\u2026"
                    : (releases == null ? "Couldn't load the release notes." : "No releases yet.");
                string sub = releasesLoading ? "" : (releases == null ? (releasesError ?? "") : "");
                Font f = Fonts.UiSemi(12f);
                int tw = Txt.Width(msg, f);
                int cy = notesFrame.Height / 2 - S(30);
                Txt.Draw(e.Graphics, msg, f, releasesLoading ? Theme.Muted : Theme.Text, (notesFrame.Width - tw) / 2, cy);
                if (sub.Length > 0)
                {
                    Font sf = Fonts.Ui(9.5f);
                    Txt.DrawIn(e.Graphics, sub, sf, Theme.Muted, new Rectangle(S(24), cy + S(30), notesFrame.Width - S(48), S(20)), TextFormatFlags.HorizontalCenter);
                }
            };
            newsPage.Controls.Add(notesFrame);

            changelog = new ChangelogView { Visible = false };
            changelog.SetBounds(1, S(4), notesFrame.Width - 2, notesFrame.Height - S(8));
            notesFrame.Controls.Add(changelog);

            newsRetry = new FlatButton(ButtonKind.Secondary, "Try again") { Visible = false, BackColor = Theme.Surface };
            newsRetry.SetBounds((notesFrame.Width - S(120)) / 2, notesFrame.Height / 2 + S(30), S(120), S(36));
            newsRetry.Click += async delegate { await LoadReleases(); };
            notesFrame.Controls.Add(newsRetry);
        }

        // Pages and their tabs, in tab order: Update, What's new (+ Publish, Account in Studio).
        readonly List<Pane> pages = new List<Pane>();
        readonly List<TabButton> tabs = new List<TabButton>();
        int current = -1;
        PageTransition transition;
        readonly Dictionary<Control, Bitmap> pageShots = new Dictionary<Control, Bitmap>();
        readonly HashSet<Control> staleShots = new HashSet<Control>();
        bool onNews { get { return current >= 0 && pages[current] == newsPage; } }

        public void ShowPage(int index)
        {
            if (index < 0 || index >= pages.Count || index == current) return;
            bool animate = current >= 0 && IsHandleCreated && Visible && WindowState != FormWindowState.Minimized
                && Environment.GetEnvironmentVariable("ME_UPDATER_NOANIM") != "1";
            if (transition != null && transition.Running) transition.Finish();
            int from = current;
            current = index;
            for (int i = 0; i < tabs.Count; i++) tabs[i].Active = i == index;
            Rectangle u = tabs[index].UnderlineBounds;
            tabLine.MoveTo(tabs[index].Left + u.X - tabLine.Left, u.Width, animate);

            Pane toPage = pages[index];
            Action finished = () => { if (toPage == newsPage && changelog.Visible) changelog.Focus(); };
            if (from < 0) { foreach (Pane p in pages) p.Visible = p == toPage; finished(); return; }
            Pane fromPage = pages[from];

            Bitmap fromShot = null, toShot = null;
            if (animate)
            {
                // The page being left, exactly as it is on screen; kept for the trip back.
                fromShot = PageShots.Capture(fromPage);
                if (fromShot != null) { SetShot(fromPage, fromShot); staleShots.Remove(fromPage); }
                if (!pageShots.TryGetValue(toPage, out toShot) || toShot == null || staleShots.Contains(toPage))
                {
                    RefreshShot(toPage);
                    pageShots.TryGetValue(toPage, out toShot);
                }
            }

            if (fromShot == null || toShot == null)
            {
                foreach (Pane p in pages) p.Visible = p == toPage;
                finished();
                return;
            }

            if (transition == null)
            {
                transition = new PageTransition();
                transition.Bounds = updatePage.Bounds;
                Controls.Add(transition);
            }
            transition.Visible = true;
            transition.BringToFront();
            tabLine.BringToFront();
            transition.Start(fromShot, toShot, index > from ? 1 : -1, () => {
                // The real page goes in under the last (identical) frame, then the overlay goes.
                foreach (Pane p in pages) p.Visible = p == toPage;
                PageShots.Reveal(transition, toPage);
                finished();
            });
        }

        void SetShot(Control page, Bitmap bmp)
        {
            Bitmap old;
            if (pageShots.TryGetValue(page, out old) && old != null && old != bmp) old.Dispose();
            pageShots[page] = bmp;
        }

        // Draws a page that isn't on screen into its cached picture.
        void RefreshShot(Pane page)
        {
            if (current >= 0 && page == pages[current]) return;
            SetShot(page, PageShots.Render(page));
            staleShots.Remove(page);
        }

        // Every hidden page gets its picture ready before the first click (called during the splash).
        public void PrepareShots()
        {
            foreach (Pane p in pages) if (current < 0 || p != pages[current]) RefreshShot(p);
        }

        // Something changed: pictures of hidden pages are re-drawn once things settle
        // (never on every keystroke).
        readonly Timer shotTimer = new Timer { Interval = 600 };

        public void MarkShotsStale()
        {
            foreach (Pane p in pages) if (current < 0 || p != pages[current]) staleShots.Add(p);
            shotTimer.Stop();
            shotTimer.Start();
        }

        void ShotTick(object sender, EventArgs e)
        {
            shotTimer.Stop();
            if (staleShots.Count == 0 || busy || !Visible || WindowState == FormWindowState.Minimized) return;
            if (transition != null && transition.Running) { shotTimer.Start(); return; }
            foreach (Pane p in new List<Pane>(pages))
                if (staleShots.Contains(p) && p != pages[current]) RefreshShot(p);
        }

        void SetLogVisible(bool on)
        {
            showingLog = on;
            logFrame.Visible = on;
            grid.Visible = !on;
            modulesHead.Invalidate();
            if (on) activity.ScrollToEnd();
        }

        // ------------------------------------------------------------------
        //  Painting
        // ------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Chrome)) g.FillRectangle(b, 0, 0, ClientSize.Width, S(TitleH));
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, 0, S(TitleH), ClientSize.Width, S(TitleH));

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int ls = S(30);
            if (logo != null) g.DrawImage(logo, S(22), (S(TitleH) - ls) / 2, ls, ls);

            Font wf = WordmarkFont;
            int th = Txt.Height(wf);
            int x = S(62), y = (S(TitleH) - th) / 2;
            // One string, so the word never splits ("Midnight Essentials"): drawn twice and
            // clipped where the colour changes. Measuring "Midnight" alone runs a little wide
            // on Windows, which only moves the colour change into the t-E gap.
            const string word = "MidnightEssentials";
            int split = x + Txt.Width("Midnight", wf);
            TextFormatFlags wflags = Txt.Base | TextFormatFlags.PreserveGraphicsClipping;
            GraphicsState wst = g.Save();
            g.SetClip(new Rectangle(0, 0, split, S(TitleH)));
            TextRenderer.DrawText(g, word, wf, new Point(x, y), Color.FromArgb(0x8B, 0x45, 0xFF), wflags);
            g.SetClip(new Rectangle(split, 0, ClientSize.Width, S(TitleH)));
            TextRenderer.DrawText(g, word, wf, new Point(x, y), Color.FromArgb(0xB2, 0x66, 0xFF), wflags);
            g.Restore(wst);
            if (Suffix != null) Txt.Draw(g, Suffix, SuffixFont, Theme.Muted, x + Txt.Width(word, wf) + S(8), y);

            int sepX = SeparatorX;
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, sepX, S(18), sepX, S(TitleH - 18));


        }

        void PaintHero(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            Shapes.Card(g, hero.ClientRectangle, S(10), Theme.Surface, Theme.Line);

            Font hf = Fonts.Display(19f), sf = Fonts.Ui(9.5f), nf = Fonts.Ui(8f);
            int x = S(26);
            int hh = Txt.Height(hf);
            int hy = S(30);
            // Neutral headline; only problems take a colour.
            Color hc = heroDot == Theme.Bad || heroDot == Theme.Warn ? heroDot : Theme.Text;
            Txt.DrawIn(g, heroHead, hf, hc, new Rectangle(x, hy, actionBtn.Left - x - S(32), hh + S(4)), 0);
            Txt.DrawIn(g, heroSub, sf, Theme.TextDim, new Rectangle(x, hy + hh + S(10), actionBtn.Left - x - S(32), S(20)), 0);

            string note = "Your settings are kept \u2014 they live in WTF.";
            int nw = Txt.Width(note, nf);
            int ncx = actionBtn.Left + actionBtn.Width / 2;
            Txt.Draw(g, note, nf, Theme.Muted, ncx - nw / 2, actionBtn.Bottom + S(12));
        }

        void PaintFolderHead(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            Font f = Fonts.UiMedium(9f), sf = Fonts.Ui(8.5f);
            Txt.Draw(g, "Game folder", f, Theme.TextDim, 0, 0);
            string st; Color c;
            if (addOnsPath != null) { st = "AddOns folder found"; c = Theme.Good; }
            else { st = "No _retail_\\Interface\\AddOns here"; c = Theme.Bad; }
            int w = Txt.Width(st, sf);
            int ty = (Txt.Height(f) - Txt.Height(sf)) / 2;
            Txt.Draw(g, st, sf, Theme.Muted, folderHead.Width - w, ty);
            Shapes.Dot(g, folderHead.Width - w - S(10), ty + Txt.Height(sf) / 2f + 0.5f, S(6), c);
        }

        void PaintModulesHead(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            Font f = Fonts.UiMedium(9f), sf = Fonts.Ui(8.5f);
            int th = Txt.Height(f);
            string a = "Modules", b = "Activity";
            int aw = Txt.Width(a, f), bw = Txt.Width(b, f);
            Txt.Draw(g, a, f, showingLog ? Theme.Muted : Theme.TextDim, 0, 0);
            Txt.Draw(g, b, f, showingLog ? Theme.TextDim : Theme.Muted, aw + S(20), 0);
            modulesTabRect = new Rectangle(0, 0, aw, th + S(6));
            activityTabRect = new Rectangle(aw + S(20), 0, bw, th + S(6));
            Rectangle act = showingLog ? activityTabRect : modulesTabRect;
            Shapes.Fill(g, new RectangleF(act.X, th + S(5), act.Width, Math.Max(2, S(2))), 1, Theme.AccentText);
            if (activity != null && activity.Count > 1 && !showingLog)
                Shapes.Dot(g, activityTabRect.Right + S(6), S(4), S(5), Theme.Faint);

            string count;
            Color cc = Theme.Muted;
            if (addOnsPath == null) count = "";
            else if (totalKnown)
            {
                count = found + " of " + total + " installed";
                if (oldCount > 0) count += "  \u00B7  " + oldCount + (oldCount == 1 ? " old folder" : " old folders") + " to remove";
            }
            else if (manifestLoading) count = found + " installed  \u00B7  checking the latest release\u2026";
            else count = found + " installed";
            int cw = Txt.Width(count, sf);
            Txt.Draw(g, count, sf, cc, modulesHead.Width - cw, (th - Txt.Height(sf)) / 2);
        }

        void PaintNewsPage(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            Font f = Fonts.UiMedium(9f), sf = Fonts.Ui(8.5f);
            Txt.Draw(g, "Releases", f, Theme.TextDim, S(24), S(24));
            if (releasesFromCache)
                Txt.Draw(g, "offline copy", sf, Theme.Warn, S(24) + Txt.Width("Releases", f) + S(8), S(24) + S(1));
        }

        // ------------------------------------------------------------------
        //  State
        // ------------------------------------------------------------------
        void RefreshState()
        {
            if (busy) return;
            addOnsPath = ResolveAddOns(pathBox.Text);
            installedVersion = addOnsPath != null ? Installer.InstalledVersion(addOnsPath) : null;
            if (addOnsPath != null) SavePath(pathBox.Text.Trim());

            // Modules: the latest release's folders, checked against AddOns.
            var items = new List<ModuleItem>();
            var leftovers = new List<ModuleItem>();
            List<string> installed = addOnsPath != null ? Installer.InstalledFolders(addOnsPath) : new List<string>();
            List<string> expected = manifest ?? ReadInstalledManifest();
            totalKnown = expected != null && addOnsPath != null;
            found = 0; total = 0; oldCount = 0;
            if (addOnsPath != null)
            {
                if (expected != null)
                {
                    foreach (string f in expected)
                    {
                        bool has = installed.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
                        if (has) found++;
                        items.Add(new ModuleItem { Folder = f, Label = Installer.Pretty(f), State = has ? ModState.Installed : ModState.Missing });
                    }
                    total = expected.Count;
                    foreach (string f in installed)
                        if (!expected.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                        {
                            oldCount++;
                            leftovers.Add(new ModuleItem { Folder = f, Label = Installer.Pretty(f), State = ModState.Old });
                        }
                }
                else
                {
                    foreach (string f in installed) items.Add(new ModuleItem { Folder = f, Label = Installer.Pretty(f), State = ModState.Installed });
                    found = total = installed.Count;
                }
            }
            items.Sort((a, b) => {
                if (a.Folder == "MidnightEssentials") return -1;
                if (b.Folder == "MidnightEssentials") return 1;
                if (a.State == ModState.Old && b.State != ModState.Old) return 1;
                if (b.State == ModState.Old && a.State != ModState.Old) return -1;
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            bool updateWaiting = Latest != null && installedVersion != null && Versions.Compare(installedVersion, Latest.Tag) < 0;
            grid.SetItems(items, leftovers, updateWaiting || installedVersion == null ? "when you update" : "when you reinstall");

            UpdateHero();
            folderHead.Invalidate();
            modulesHead.Invalidate();
            versions.SetInstalled(installedVersion);
            changelog.SetInstalled(installedVersion);
            RefreshTabs();
            MarkShotsStale();
        }

        void UpdateHero()
        {
            Release latest = Latest;
            string lv = latest != null ? latest.Tag : null;
            string meta = latest != null ? Join("released " + Fmt.Ago(latest.Published), Fmt.Size(latest.ZipSize)) : "";
            bool showNotes = false;
            actionBtn.Kind = ButtonKind.Primary;

            if (resultHead != null)
            {
                SetHero(resultHead, resultSub, resultDot);
                actionBtn.Enabled = addOnsPath != null;
                actionBtn.Text = installedVersion != null && lv != null && Versions.Compare(installedVersion, lv) >= 0 ? "Reinstall" : (lv != null ? "Update to " + lv : "Update");
                actionBtn.Kind = ButtonKind.Secondary;
            }
            else if (addOnsPath == null)
            {
                SetHero("Choose your game folder", "Pick the World of Warcraft folder \u2014 the one with _retail_ in it.", Theme.Bad);
                actionBtn.Enabled = false;
                actionBtn.Text = lv != null ? "Install " + lv : "Install";
            }
            else if (latest == null && releasesLoading)
            {
                SetHero("Checking for updates\u2026", installedVersion != null ? "MidnightEssentials " + installedVersion + " is installed" : "MidnightEssentials isn't installed yet", Theme.Muted);
                actionBtn.Enabled = false;
                actionBtn.Text = "Update";
            }
            else if (latest == null)
            {
                SetHero("Couldn't reach GitHub", (releasesError == "no internet connection" ? "Check your internet connection. " : "") + "You can still try to install the latest release.", Theme.Warn);
                actionBtn.Enabled = true;
                actionBtn.Text = installedVersion == null ? "Install latest" : "Update to latest";
            }
            else if (installedVersion == null)
            {
                SetHero("Ready to install", Join("MidnightEssentials " + lv, meta), Theme.AccentText);
                actionBtn.Text = "Install " + lv;
                actionBtn.Enabled = true;
                showNotes = true;
            }
            else if (Versions.Compare(installedVersion, lv) < 0)
            {
                SetHero("Update available", Join(installedVersion + " installed", lv + " " + meta), Theme.AccentText);
                actionBtn.Text = "Update to " + lv;
                actionBtn.Enabled = true;
                showNotes = true;
            }
            else if (Versions.Compare(installedVersion, lv) > 0)
            {
                SetHero("Newer than the current release",
                    installedVersion + " installed  \u00B7  the current release is " + lv, Theme.Muted);
                actionBtn.Text = "Install " + lv;
                actionBtn.Kind = ButtonKind.Secondary;
                actionBtn.Enabled = true;
            }
            else if (totalKnown && found < total)
            {
                int miss = total - found;
                SetHero("Some modules are missing", miss + (miss == 1 ? " folder isn't" : " folders aren't") + " in AddOns. A repair puts them back.", Theme.Warn);
                actionBtn.Text = "Repair " + installedVersion;
                actionBtn.Enabled = true;
            }
            else if (totalKnown && oldCount > 0)
            {
                SetHero("You're up to date", "An old module folder is left over. Reinstall to clear it.", Theme.Good);
                actionBtn.Text = "Reinstall " + installedVersion;
                actionBtn.Enabled = true;
            }
            else
            {
                SetHero("You're up to date", Join("MidnightEssentials " + installedVersion, "released " + Fmt.Ago(latest.Published)), Theme.Good);
                actionBtn.Text = "Reinstall";
                actionBtn.Kind = ButtonKind.Secondary;
                actionBtn.Enabled = true;
            }
            if (releasesFromCache && latest != null && resultHead == null) heroSub = Join(heroSub, "offline");

            notesLink.Text = lv != null ? "What's new in " + lv : "What's new";
            notesLink.FitToText();
            notesLink.Visible = showNotes && !busy;
            retryLink.Visible = !busy && latest == null && !releasesLoading;
            actionBtn.Invalidate();
            hero.Invalidate();
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + "  \u00B7  " + b;
        }

        static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1).TrimEnd('.'); }

        void SetHero(string head, string sub, Color dot)
        {
            heroHead = head; heroSub = sub; heroDot = dot;
            hero.Invalidate();
        }

        // Startup work, run while the splash screen is up. report(progress 0..1, status).
        public Task Preload(Action<float, string> report)
        {
#if STUDIO
            return Task.WhenAll(LoadReleases(report), studio.Preload(), releasesTab.Reload());
#else
            return LoadReleases(report);
#endif
        }

        public void RunTestHooks()
        {
            if (Environment.GetEnvironmentVariable("ME_UPDATER_AUTORUN") == "1") StartUpdate(null, EventArgs.Empty);
        }

        // ------------------------------------------------------------------
        //  Staying current while the window is open: a quiet check when the window comes back
        //  to the front (at most once a minute) and every 5 minutes. Unchanged lists cost
        //  nothing: GitHub answers 304 to the saved ETag, which doesn't count against its limit.
        // ------------------------------------------------------------------
        DateTime lastCheckUtc = DateTime.MinValue;
        bool checking;
        readonly Timer autoCheckTimer = new Timer { Interval = 5 * 60 * 1000 };

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            AutoCheck(true);
        }

        async void AutoCheck(bool onFocus)
        {
            if (busy || checking || releasesLoading || !Visible) return;
            if (onFocus && (DateTime.UtcNow - lastCheckUtc).TotalSeconds < 60) return;
            await CheckEverything();
        }

        async Task CheckEverything()
        {
            await LoadReleases(null, true);
#if STUDIO
            await Task.WhenAll(releasesTab.Reload(true), studio.RefreshLastRelease());
#endif
            MarkShotsStale();
        }

        async Task LoadReleases(Action<float, string> report = null, bool quiet = false)
        {
            if (checking) return;
            checking = true;
            lastCheckUtc = DateTime.UtcNow;
            string before = Latest != null ? Latest.Tag : null;
            try
            {
                if (!quiet)
                {
                    releasesLoading = true;
                    releasesError = null;
                    newsRetry.Visible = false;
                    notesFrame.Invalidate();
                    UpdateHero();
                }

                GitHub.ReleaseResult res = await GitHub.FetchReleases();
                if (report != null) report(0.7f, null);
                // A quiet check that fails keeps what's on screen; nothing to report.
                if (quiet && (res.Error != null || res.Releases == null) && releases != null) return;
                if (quiet && busy) return;           // never swap the list under a running update
                releasesLoading = false;
                releases = res.Releases;
                releasesFromCache = res.FromCache;
                releasesError = res.Error;
                if (res.Error != null)
                    Log(res.FromCache ? "GitHub couldn't be reached (" + res.Error + "); showing the last saved release list."
                                      : "Couldn't check GitHub for releases (" + res.Error + ").", Theme.Warn);
                else if (Latest != null && before == null)
                    Log("Latest release on GitHub: " + Latest.Tag + ".", Theme.TextDim);
                else if (Latest != null && Latest.Tag != before)
                    Log("New release on GitHub: " + Latest.Tag + ".", Theme.AccentText);
                if (quiet && Latest != null && Latest.Tag == before && manifest != null)
                {
                    // Nothing new: refresh the lists quietly, keep everything else.
                    versions.SetData(releases, installedVersion);
                    ShowSelectedNotes();
                    RefreshState();
                    return;
                }

            versions.SetData(releases, installedVersion);
            ShowSelectedNotes();
            newsPage.Invalidate();
            RefreshState();

            // Which folders the latest release ships (for the module count).
            Release latest = Latest;
            if (latest != null)
            {
                manifestLoading = true;
                modulesHead.Invalidate();
                List<string> m = await GitHub.FetchManifest(latest);
                manifestLoading = false;
                if (m != null) manifest = m;
                else Log("Couldn't read the folder list of " + latest.Tag + "; counting what's installed instead." + (GitHub.LastError != null ? " (" + GitHub.LastError + ")" : ""), Theme.Muted);
                RefreshState();
            }
            if (report != null) report(1f, "Ready");
            }
            finally { checking = false; }
        }

        void ShowSelectedNotes()
        {
            Release r = versions.Selected;
            bool has = r != null;
            changelog.Visible = has;
            newsRetry.Visible = !has && !releasesLoading && releases == null;
            if (has) changelog.SetRelease(r, installedVersion, Latest != null && r.Tag == Latest.Tag);
            notesFrame.Invalidate();
        }

        // ------------------------------------------------------------------
        //  Folder handling
        // ------------------------------------------------------------------
        static string ResolveAddOns(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            root = root.Trim().Trim('"').TrimEnd('\\', '/');
            string[] candidates = {
                Path.Combine(root, "_retail_", "Interface", "AddOns"),
                Path.Combine(root, "Interface", "AddOns"),
                root,
            };
            foreach (string c in candidates)
            {
                try
                {
                    if (Directory.Exists(c) && string.Equals(Path.GetFileName(c), "AddOns", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                catch { }
            }
            return null;
        }

        void Browse(object sender, EventArgs e)
        {
            string start = Directory.Exists(pathBox.Text.Trim()) ? pathBox.Text.Trim() : @"C:\Program Files (x86)";
            string picked = FolderPicker.Pick(this, "Choose your World of Warcraft folder (the one with _retail_ in it)", "Select folder", start);
            if (picked != null) pathBox.Text = picked;
        }

        static string DetectWowPath()
        {
            string[] keys = {
                @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
            };
            foreach (string k in keys)
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(k))
                    {
                        string p = rk == null ? null : rk.GetValue("InstallPath") as string;
                        if (string.IsNullOrEmpty(p)) continue;
                        p = p.TrimEnd('\\', '/');
                        if (string.Equals(Path.GetFileName(p), "_retail_", StringComparison.OrdinalIgnoreCase))
                            p = Path.GetDirectoryName(p);
                        if (Directory.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }

        static string SettingsFile { get { return Path.Combine(GitHub.AppData, "settings.txt"); } }

        static string LoadSavedPath()
        {
            try { if (File.Exists(SettingsFile)) { string p = File.ReadAllText(SettingsFile).Trim(); if (p.Length > 0) return p; } } catch { }
            return null;
        }

        static void SavePath(string p)
        {
            try { Directory.CreateDirectory(GitHub.AppData); File.WriteAllText(SettingsFile, p); } catch { }
        }

        // The folder list of the last install: a fallback count when GitHub can't be read.
        List<string> ReadInstalledManifest()
        {
            string t = GitHub.ReadCache("installed-folders.txt");
            if (t == null) return null;
            string[] lines = t.Split('\n');
            if (lines.Length < 2 || installedVersion == null || lines[0].Trim() != installedVersion) return null;
            var list = new List<string>();
            for (int i = 1; i < lines.Length; i++) if (lines[i].Trim().Length > 0) list.Add(lines[i].Trim());
            return list.Count > 0 ? list : null;
        }

        // ------------------------------------------------------------------
        //  Update: download -> check -> unpack beside AddOns -> swap folders
        // ------------------------------------------------------------------
        async void StartUpdate(object sender, EventArgs e)
        {
            if (addOnsPath == null || busy) return;
            resultHead = null;
            SetBusy(true);
            Release target = Latest;
            bool fresh = installedVersion == null;
            int vs = target != null && installedVersion != null ? Versions.Compare(installedVersion, target.Tag) : -1;
            bool downgrade = vs > 0;
            string verb = fresh || downgrade ? "Installing" : (vs == 0 ? "Reinstalling" : "Updating to");
            string tv = target != null ? target.Tag : "the latest release";
            SetHero(verb + " " + tv, "Starting\u2026", Theme.AccentText);
            try
            {
                await RunUpdate(target);
                string ver = Installer.InstalledVersion(addOnsPath) ?? tv;
                resultHead = (fresh || downgrade ? "Installed " : "Updated to ") + ver;
                resultSub = "Start the game, or type /reload if it's already running.";
                resultDot = Theme.Good;
            }
            catch (UnauthorizedAccessException)
            {
                Log("Windows blocked access to the AddOns folder. Close the updater, right-click it and choose \"Run as administrator\".", Theme.Bad);
                resultHead = "Windows blocked the update";
                resultSub = "Right-click the updater and choose \"Run as administrator\". Nothing was changed.";
                resultDot = Theme.Bad;
                progress.Value = 0f;
            }
            catch (Exception ex)
            {
                Log("Update failed: " + ex.Message, Theme.Bad);
                Log("Your installed MidnightEssentials was left as it was.", Theme.Muted);
                resultHead = "Update failed";
                resultSub = Cap(GitHub.Friendly(ex)) + ". Nothing was changed.";
                resultDot = Theme.Bad;
                progress.Value = 0f;
            }
            finally
            {
                SetBusy(false);
                RefreshState();
            }
        }

        void Step(string text, float p)
        {
            heroSub = text;
            hero.Invalidate();
            if (p >= 0) progress.Value = p;
        }

        async Task RunUpdate(Release target)
        {
            string addOns = addOnsPath;
            string interfaceDir = Path.GetDirectoryName(addOns);
            string staging = Path.Combine(interfaceDir, ".me_update_staging");
            string backup = Path.Combine(interfaceDir, ".me_update_backup");
            string tempDir = Path.Combine(Path.GetTempPath(), "MidnightEssentials Updater");
            string zipPath = Path.Combine(tempDir, GitHub.AssetName);

            try { if (Process.GetProcessesByName("Wow").Length > 0) Log("World of Warcraft is running: type /reload in game after the update.", Theme.Warn); } catch { }

            // 1. Download (or a local zip for testing: ME_UPDATER_ZIP).
            string localZip = Environment.GetEnvironmentVariable("ME_UPDATER_ZIP");
            Directory.CreateDirectory(tempDir);
            if (!string.IsNullOrEmpty(localZip) && File.Exists(localZip))
            {
                File.Copy(localZip, zipPath, true);
                Log("Using local zip: " + localZip, Theme.Muted);
            }
            else
            {
                string url = target != null && target.ZipUrl != null ? target.ZipUrl : GitHub.LatestDownload;
                Log("Downloading MidnightEssentials" + (target != null ? " " + target.Tag : "") + "\u2026", Theme.Text);
                Step("Downloading\u2026", 0f);
                using (WebClient wc = GitHub.NewClient())
                {
                    wc.DownloadProgressChanged += (s, e) => {
                        long totalBytes = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : (target != null ? target.ZipSize : 0);
                        if (totalBytes > 0)
                            Step("Downloading  \u00B7  " + (e.BytesReceived / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)
                                + " of " + Fmt.Size(totalBytes), 0.75f * e.BytesReceived / totalBytes);
                    };
                    await wc.DownloadFileTaskAsync(new Uri(url), zipPath);
                }
                long got = new FileInfo(zipPath).Length;
                if (target != null && target.ZipSize > 0 && got != target.ZipSize)
                    throw new Exception("the download was incomplete (" + Fmt.Size(got) + " of " + Fmt.Size(target.ZipSize) + ")");
                Log("Downloaded " + Fmt.Size(got) + ".", Theme.TextDim);
            }

            // 2. Check + unpack next to AddOns (same drive, so the swap is instant).
            Step("Unpacking\u2026", 0.78f);
            List<string> newFolders = await Task.Run(() => Installer.Unpack(zipPath, staging));
            Log("The download has " + newFolders.Count + " MidnightEssentials folders.", Theme.TextDim);
            Step("Replacing " + newFolders.Count + " folders\u2026", 0.92f);

            // 3. Swap: old folders aside, new ones in; on any failure put the old back.
            Installer.SwapResult sw = await Task.Run(() => Installer.Swap(addOns, staging, backup, newFolders));
            foreach (string old in sw.Removed) Log("Removed " + old + " (no longer part of MidnightEssentials).", Theme.TextDim);
            Step("Cleaning up\u2026", 1f);

            // 4. Clean up, and remember what this version installed.
            await Task.Run(() => {
                Installer.TryDelete(staging); Installer.TryDelete(backup);
                try { File.Delete(zipPath); } catch { }
            });
            string ver = Installer.InstalledVersion(addOns) ?? (target != null ? target.Tag : "");
            if (ver.Length > 0) GitHub.WriteCache("installed-folders.txt", ver + "\n" + string.Join("\n", newFolders));
            if (target != null && manifest == null) manifest = newFolders;

            Log("Done. MidnightEssentials " + ver + " is installed (" + newFolders.Count + " folders).", Theme.Good);
        }

        void SetBusy(bool on)
        {
            busy = on;
            actionBtn.Enabled = !on;
            if (on) actionBtn.Text = "Working\u2026";
            browseBtn.Enabled = !on;
            pathBox.ReadOnly = on;
            progress.Visible = on;
            if (on) { progress.Value = 0f; notesLink.Visible = false; retryLink.Visible = false; SetLogVisible(true); }
            else progress.Value = 0f;
            hero.Invalidate();
        }

        void Log(string text, Color color)
        {
            activity.Add(text, color);
            modulesHead.Invalidate();
        }
    }

    // ======================================================================
    //  Splash: crest + wordmark animate in, the bar follows the real startup work
    //  (release list, folder list), then it cross-fades into the main window.
    // ======================================================================
    class SplashForm : Form
    {
        static readonly Color MidnightPurple = Color.FromArgb(0x8B, 0x45, 0xFF);
        static readonly Color EssentialsPurple = Color.FromArgb(0xB2, 0x66, 0xFF);

        readonly Image logo;
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Stopwatch clock = new Stopwatch();   // starts once the main window is built
        long lastTick;
        float target, shown;
        string status;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static int S(float v) { return Dpi.S(v); }

        public SplashForm() : this("MidnightEssentials Updater", "Starting the updater\u2026") { }

        public SplashForm(string title, string startStatus)
        {
            status = startStatus;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Dpi.Scale = g.DpiX / 96f;
            Text = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ClientSize = new Size(S(440), S(280));
            // Centred by hand on the screen under the mouse (where the exe was opened).
            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("MEUpdater.logo.png"))
                    if (st != null) logo = Image.FromStream(st);
            }
            catch { logo = null; }
            timer.Tick += delegate {
                long now = clock.ElapsedMilliseconds;
                float dt = Math.Min(0.05f, (now - lastTick) / 1000f);
                lastTick = now;
                // Ease toward the reported progress; quick, but never a jump.
                shown += (target - shown) * Math.Min(1f, dt * 9f);
                if (target >= 1f && 1f - shown < 0.004f) shown = 1f;
                Invalidate();
            };
            timer.Start();
        }

        protected override CreateParams CreateParams
        {
            get { return base.CreateParams; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowEdge.Apply(Handle);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { timer.Stop(); base.OnFormClosed(e); }

        public void Report(float p, string s)
        {
            if (p > target) target = Math.Min(1f, p);
            if (s != null) status = s;
        }

        // The animation's clock starts after the heavy setup, so no frames get skipped.
        public void StartAnimation() { clock.Restart(); lastTick = 0; }

        public bool Full { get { return shown >= 1f; } }
        public long ElapsedMs { get { return clock.ElapsedMilliseconds; } }

        static float Ease(float t) { t = Math.Max(0f, Math.Min(1f, t)); return 1f - (1f - t) * (1f - t) * (1f - t); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            float t = clock.IsRunning ? clock.ElapsedMilliseconds / 1000f : 0f;
            float aLogo = Ease(t / 0.5f);
            float aWord = Ease((t - 0.15f) / 0.5f);
            float aBar  = Ease((t - 0.3f) / 0.4f);
            int w = ClientSize.Width, h = ClientSize.Height;
            int cx = w / 2;

            int logoSize = S(76);
            float logoCy = S(92);

            // Crest: fades in and settles from 92% to full size.
            if (logo != null && aLogo > 0)
            {
                float sc = 0.92f + 0.08f * aLogo;
                float sz = logoSize * sc;
                RectangleF dst = new RectangleF(cx - sz / 2, logoCy - sz / 2, sz, sz);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix { Matrix33 = aLogo };
                    ia.SetColorMatrix(cm);
                    g.DrawImage(logo, Rectangle.Round(dst), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
                }
            }

            // Wordmark: one string, coloured like the addon's title, rising slightly as it fades in.
            if (aWord > 0)
            {
                Font wf = Fonts.UiSemi(17f);
                const string word = "MidnightEssentials";
                int ww = Txt.Width(word, wf);
                int x = cx - ww / 2;
                int y = (int)(logoCy + logoSize / 2f + S(18) + S(8) * (1f - aWord));
                int split = x + Txt.Width("Midnight", wf);
                TextFormatFlags fl = Txt.Base | TextFormatFlags.PreserveGraphicsClipping;
                GraphicsState st = g.Save();
                g.SetClip(new Rectangle(0, 0, split, h));
                TextRenderer.DrawText(g, word, wf, new Point(x, y), Theme.Mix(Theme.Bg, MidnightPurple, aWord), fl);
                g.SetClip(new Rectangle(split, 0, w, h));
                TextRenderer.DrawText(g, word, wf, new Point(x, y), Theme.Mix(Theme.Bg, EssentialsPurple, aWord), fl);
                g.Restore(st);
            }

            // Loading bar + status.
            if (aBar > 0)
            {
                int bw = S(200), bh = Math.Max(3, S(3));
                int bx = cx - bw / 2, by = h - S(58);
                Shapes.Fill(g, new RectangleF(bx, by, bw, bh), bh / 2f, Theme.Mix(Theme.Bg, Theme.Line, aBar));
                float fw = bw * shown;
                if (fw >= bh)
                {
                    RectangleF fr = new RectangleF(bx, by, fw, bh);
                    Shapes.Fill(g, fr, bh / 2f, Theme.Mix(Theme.Bg, MidnightPurple, aBar));
                }
                Font sf = Fonts.Ui(8.5f);
                int sw = Txt.Width(status, sf);
                Txt.Draw(g, status, sf, Theme.Mix(Theme.Bg, Theme.Muted, aBar), cx - sw / 2, by + S(14));
            }


        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware(); } catch { }
            // GitHub only speaks TLS 1.2+; older .NET Framework defaults do not include it.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var ctx = new ApplicationContext();
#if STUDIO
            var splash = new SplashForm(MainForm.AppTitle, "Starting the studio\u2026");
#else
            var splash = new SplashForm();
#endif
            bool revealed = false;
            splash.FormClosed += delegate { if (!revealed) ctx.ExitThread(); };
            splash.Shown += async delegate {
                await Task.Delay(30);                       // let the empty splash paint first
                Fonts.Ui(9f);                               // load the embedded fonts
                MainForm main = new MainForm();             // build the whole UI up front
                splash.StartAnimation();
                splash.Report(0.2f, null);
                Task load = main.Preload((p, s) => splash.Report(p, s));
                // Never hold the user longer than ~4 s on a slow connection: the main window
                // shows its own "Checking for updates" state if the check is still running.
                await Task.WhenAny(load, Task.Delay(4000));
                if (!load.IsCompleted) splash.Report(1f, null);
                while (splash.ElapsedMs < 1200 || !splash.Full) await Task.Delay(15);
                await Task.Delay(120);
                if (splash.IsDisposed) return;

                // Cross-fade into the main window, centred where the splash was.
                revealed = true;
                main.FormClosed += delegate { ctx.ExitThread(); };
                Rectangle sb = splash.Bounds;
                main.StartPosition = FormStartPosition.Manual;
                main.Location = new Point(sb.X + sb.Width / 2 - main.Width / 2, sb.Y + sb.Height / 2 - main.Height / 2);
                Rectangle wa = Screen.FromRectangle(sb).WorkingArea;
                main.Location = new Point(Math.Max(wa.X, Math.Min(main.Left, wa.Right - main.Width)),
                                          Math.Max(wa.Y, Math.Min(main.Top, wa.Bottom - main.Height)));
                main.Opacity = 0;
                main.Show();
                main.PrepareShots();                        // What's New picture, ready before the first click
                Stopwatch fade = Stopwatch.StartNew();
                while (fade.ElapsedMilliseconds < 220)
                {
                    double f = fade.ElapsedMilliseconds / 220.0;
                    main.Opacity = f;
                    splash.Opacity = 1 - f;
                    await Task.Delay(15);
                }
                main.Opacity = 1;
                splash.Close();
                main.Activate();
                main.RunTestHooks();
            };
            splash.Show();
            Application.Run(ctx);
        }
    }
}
