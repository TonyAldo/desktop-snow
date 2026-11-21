// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class DesktopSnow
{
    [STAThread]
    static int Main(string[] args)
    {
        bool smoke = Array.IndexOf(args, "--smoke-test") >= 0;
        bool first;
        using (var instance = new System.Threading.Mutex(true, smoke ? @"Local\DesktopSnow.Smoke" : @"Local\DesktopSnow", out first))
        {
            if (!first) return 0;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            using (var app = new SnowApp(smoke))
                Application.Run(app);
        }
        return 0;
    }
}

sealed class SnowApp : ApplicationContext
{
    readonly List<SnowWindow> windows = new List<SnowWindow>();
    readonly Timer timer = new Timer { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly NotifyIcon tray;
    readonly Icon icon;
    readonly bool smoke;
    readonly ToolStripMenuItem pause = new ToolStripMenuItem("Pause snowfall");
    readonly ToolStripMenuItem settle = new ToolStripMenuItem("Let snow accumulate");
    double last;
    double layoutCheck;
    uint ownProcess = (uint)Process.GetCurrentProcess().Id;
    readonly SnowSettings settings;
    readonly ClearShortcut shortcut;
    bool fullscreenPaused;
    SnowSettingsDialog settingsDialog;
    bool clearQueued;
    int frames;
    bool paused;
    bool sweeping;
    readonly ToolStripMenuItem clear = new ToolStripMenuItem("Clear snow");

    public SnowApp(bool smoke)
    {
        this.smoke = smoke;
        settings = smoke ? new SnowSettings() : SnowSettings.Load();
        if (!smoke) shortcut = new ClearShortcut(StartSweep);
        else VerifyConfiguration();
        icon = MakeIcon();
        tray = new NotifyIcon { Text = "Desktop Snow", Icon = icon, ContextMenuStrip = menu };
        tray.DoubleClick += (s, e) => Toggle();
        pause.Click += (s, e) => Toggle();
        settle.Checked = settings.Settle;
        settle.CheckedChanged += (s, e) =>
        {
            settings.Settle = settle.Checked;
            SaveSettings();
        };
        settle.CheckOnClick = true;
        menu.Items.Add(pause);
        var weatherMenu = new ToolStripMenuItem("Weather");
        foreach (var weather in SnowWeather.Presets)
        {
            var item = new ToolStripMenuItem(weather.Name) { Checked = settings.Weather == weather };
            item.Click += (s, e) =>
            {
                if (settings.Weather == weather) return;
                settings.Preset = Array.IndexOf(SnowWeather.Presets, weather);
                foreach (ToolStripMenuItem option in weatherMenu.DropDownItems)
                    option.Checked = option == item;
                foreach (var window in windows) window.RefreshFlakes();
                SaveSettings();
            };
            weatherMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(weatherMenu);
        menu.Items.Add(settle);
        var animals = new ToolStripMenuItem("Snow animals") { Checked = settings.Animals, CheckOnClick = true };
        animals.CheckedChanged += (s, e) =>
        {
            settings.Animals = animals.Checked;
            SaveSettings();
        };
        menu.Items.Add(animals);
        if (shortcut != null && shortcut.Registered) clear.ShortcutKeyDisplayString = "Ctrl+Alt+Shift+S";
        clear.Click += (s, e) => StartSweep();
        menu.Items.Add(clear);
        menu.Items.Add("Settings…", null, (s, e) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Desktop Snow", null, (s, e) => ExitThread());
        SyncScreens();
        tray.Visible = !smoke;
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }

    static void VerifyConfiguration()
    {
        var custom = new SnowSettings { Preset = 2, Wind = 65, Accumulation = 175, Settle = false, AutoPause = false, Animals = true };
        var restored = SnowSettings.Read(custom.Write());
        if (restored.Weather != SnowWeather.Presets[2] || restored.Wind != 65
            || restored.Accumulation != 175 || restored.Settle || restored.AutoPause || !restored.Animals)
            throw new InvalidOperationException("Settings did not round-trip.");
        var legacy = SnowSettings.Read(new[] { "2", "65", "175", "False", "False" });
        if (legacy.Animals || legacy.Preset != 2 || legacy.Accumulation != 175)
            throw new InvalidOperationException("Settings from an earlier version did not load.");
        var screen = new Rectangle(-1920, 0, 1920, 1080);
        var frame = new Native.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        if (!CoversScreen(frame, screen)) throw new InvalidOperationException("A fullscreen app was missed.");
        frame.Bottom = 1032;
        if (CoversScreen(frame, screen)) throw new InvalidOperationException("A window above the taskbar was treated as fullscreen.");
    }

    void OpenSettings()
    {
        if (settingsDialog != null) return;
        using (var dialog = new SnowSettingsDialog(settings, shortcut != null && shortcut.Registered))
        {
            settingsDialog = dialog;
            try
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                dialog.Apply();
                SaveSettings();
            }
            finally { settingsDialog = null; }
        }
    }

    Rectangle GetSettingsBounds()
    {
        if (settingsDialog == null || !settingsDialog.Visible) return Rectangle.Empty;
        IntPtr handle = settingsDialog.Handle;
        Native.Rect frame;
        if (Native.DwmGetWindowAttribute(handle, 9, out frame, 16) != 0
            && !Native.GetWindowRect(handle, out frame)) return Rectangle.Empty;
        return Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
    }

    void SaveSettings()
    {
        if (smoke) return;
        try { settings.Save(); }
        catch (System.IO.IOException) { ReportSaveError(); }
        catch (UnauthorizedAccessException) { ReportSaveError(); }
    }

    void ReportSaveError()
    {
        tray.ShowBalloonTip(4000, "Desktop Snow", "Your changes apply now, but Windows could not save them.", ToolTipIcon.Warning);
    }

    static bool IsFullscreenWindow()
    {
        IntPtr active = Native.GetForegroundWindow();
        if (active == IntPtr.Zero || active == Native.GetShellWindow() || Native.IsIconic(active)) return false;
        var name = new System.Text.StringBuilder(64);
        Native.GetClassName(active, name, name.Capacity);
        switch (name.ToString())
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
                return false;
        }

        Native.Rect rect;
        if (Native.DwmGetWindowAttribute(active, 9, out rect, 16) != 0
            && !Native.GetWindowRect(active, out rect)) return false;
        return CoversScreen(rect, Screen.FromHandle(active).Bounds);
    }

    static bool CoversScreen(Native.Rect rect, Rectangle screen)
    {
        return rect.Left <= screen.Left + 2 && rect.Top <= screen.Top + 2
            && rect.Right >= screen.Right - 2 && rect.Bottom >= screen.Bottom - 2;
    }

    void Toggle()
    {
        paused = !paused;
        pause.Text = paused ? "Resume snowfall" : "Pause snowfall";
        UpdateVisibility();
    }

    void StartSweep()
    {
        if (sweeping || settingsDialog != null) return;
        if (fullscreenPaused)
        {
            clearQueued = true;
            return;
        }
        sweeping = true;
        clear.Enabled = false;
        foreach (var window in windows) window.BeginSweep(smoke);
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        bool visible = !fullscreenPaused && (!paused || sweeping);
        timer.Interval = visible ? 33 : 250;
        foreach (var window in windows)
        {
            if (visible) window.Show();
            else window.Hide();
        }
        last = clock.Elapsed.TotalSeconds;
    }

    void SyncScreens()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == windows.Count)
        {
            bool same = true;
            for (int i = 0; i < screens.Length; i++)
                same &= windows[i].ScreenBounds == screens[i].Bounds;
            if (same) return;
        }
        foreach (var window in windows) window.Dispose();
        windows.Clear();
        foreach (var screen in screens)
        {
            var window = new SnowWindow(screen.Bounds, smoke);
            windows.Add(window);
            if (sweeping) window.BeginSweep(smoke);
            if (!fullscreenPaused && (!paused || sweeping)) window.Show();
        }
    }

    void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(.08, now - last);
        last = now;
        if (now - layoutCheck >= 1)
        {
            layoutCheck = now;
            SyncScreens();
        }
        bool fullscreen = !smoke && settings.AutoPause && IsFullscreenWindow();
        if (fullscreen != fullscreenPaused)
        {
            fullscreenPaused = fullscreen;
            UpdateVisibility();
        }
        if (fullscreenPaused) return;
        if (clearQueued)
        {
            clearQueued = false;
            StartSweep();
        }
        if (paused && !sweeping) return;
        var ledges = new List<Ledge>();
        if (settle.Checked)
        {
            AddLedge(ledges, Native.FindWindow("Shell_TrayWnd", null), false);
            IntPtr secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                AddLedge(ledges, secondary, false);
            IntPtr shell = Native.GetShellWindow();
            Native.EnumWindows((handle, data) =>
            {
                uint pid;
                Native.GetWindowThreadProcessId(handle, out pid);
                if (pid != ownProcess && handle != shell && Native.IsWindowVisible(handle)
                    && !Native.IsIconic(handle) && Native.GetWindowTextLength(handle) > 0)
                    AddLedge(ledges, handle, true);
                return true;
            }, IntPtr.Zero);
        }
        if (smoke && frames < SnowWeather.Presets.Length)
        {
            settings.Preset = frames;
            settings.Animals = true;
            foreach (var window in windows)
            {
                window.RefreshFlakes();
                window.TestAnimalFrame(frames);
                if (frames == 0) window.VerifySettingsClip();
            }
        }
        if (smoke && frames == 3)
        {
            foreach (var window in windows) window.SeedTestPile();
            StartSweep();
        }
        Rectangle settingsBounds = GetSettingsBounds();
        bool finished = sweeping;
        foreach (var window in windows)
        {
            window.Frame(dt, (float)now, settings, ledges, !paused, settingsBounds);
            finished &= window.SweepFinished;
        }
        if (finished)
        {
            foreach (var window in windows)
            {
                if (smoke && window.HasDeepSnow) throw new InvalidOperationException("The sweep left snow behind.");
                window.EndSweep();
            }
            sweeping = false;
            clear.Enabled = true;
            UpdateVisibility();
        }
        if (smoke && ++frames >= 24)
        {
            if (sweeping) throw new InvalidOperationException("The sweep did not finish.");
            ExitThread();
        }
    }

    static void AddLedge(List<Ledge> ledges, IntPtr handle, bool frame)
    {
        if (handle == IntPtr.Zero) return;
        if (ledges.Exists(edge => edge.Handle == handle)) return;
        int cloaked;
        if (frame && Native.DwmGetWindowAttributeInt(handle, 14, out cloaked, 4) == 0 && cloaked != 0) return;
        Native.Rect rect;
        if ((!frame || Native.DwmGetWindowAttribute(handle, 9, out rect, 16) != 0)
            && !Native.GetWindowRect(handle, out rect)) return;
        if (rect.Right - rect.Left >= 32 && rect.Bottom - rect.Top >= 20)
            ledges.Add(new Ledge(handle, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                frame && !Native.IsZoomed(handle), frame));
    }

    static Icon MakeIcon()
    {
        using (var bitmap = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bitmap))
        using (var edge = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var disc = new SolidBrush(Color.FromArgb(32, 83, 116)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(disc, 0, 0, 32, 32);
            for (int i = 0; i < 6; i++)
            {
                g.ResetTransform();
                g.TranslateTransform(16, 16);
                g.RotateTransform(i * 60);
                g.DrawLine(edge, 0, 0, 0, -11);
                g.DrawLine(edge, -3.4f, -8.5f, 0, -5.7f);
                g.DrawLine(edge, 3.4f, -8.5f, 0, -5.7f);
            }
            var handle = bitmap.GetHicon();
            try { using (var original = Icon.FromHandle(handle)) return (Icon)original.Clone(); }
            finally { Native.DestroyIcon(handle); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            if (shortcut != null) shortcut.Dispose();
            tray.Dispose();
            menu.Dispose();
            icon.Dispose();
            foreach (var window in windows) window.Dispose();
        }
        base.Dispose(disposing);
    }
}

sealed class Ledge
{
    public readonly IntPtr Handle;
    public readonly Rectangle Bounds;
    public readonly bool Collects, Moves;

    public Ledge(IntPtr handle, Rectangle bounds, bool collects, bool moves)
    {
        Handle = handle;
        Bounds = bounds;
        Collects = collects;
        Moves = moves;
    }
}

sealed partial class SnowWindow : NativeWindow, IDisposable
{
    sealed class Flake
    {
        public float X, Y, Speed, Size, Phase, Flutter;
        public int Sprite;

    }

    const int BinWidth = 5;

    sealed class Snowbank
    {
        public Rectangle Edge;
        public float[] Heights;
        public IntPtr Handle;
        public bool Seen;
        public int Direction;
        public int Motion;
        public float LastMove, LastShake = -10;
        public readonly List<Rectangle> Covered = new List<Rectangle>();
        public bool Occupied;

        public Snowbank(Rectangle edge)
        {
            Edge = edge;
            Heights = new float[(edge.Width + BinWidth - 1) / BinWidth];
        }
    }

    sealed class FallingSnow
    {
        public float X, Y, Vx, Vy, Radius;
        public Snowbank Surface;
    }

    readonly List<FallingSnow> loose = new List<FallingSnow>();
    readonly List<Snowbank> banks = new List<Snowbank>();
    readonly SolidBrush snow = new SolidBrush(Color.FromArgb(245, 244, 251, 255));
    readonly SolidBrush ice = new SolidBrush(Color.FromArgb(225, 168, 209, 234));
    readonly SolidBrush orange = new SolidBrush(Color.FromArgb(255, 223, 111, 48));
    readonly SolidBrush amber = new SolidBrush(Color.FromArgb(255, 255, 210, 94));
    readonly SolidBrush tire = new SolidBrush(Color.FromArgb(255, 30, 43, 56));
    readonly SolidBrush glass = new SolidBrush(Color.FromArgb(255, 115, 190, 222));
    bool sweeping;
    bool testSweep;
    int routeIndex, clearedBins;
    float travel, mound;
    readonly List<Snowbank> route = new List<Snowbank>();
    public bool SweepFinished { get; private set; }
    readonly Snowbank emptyRunway;

    public Rectangle ScreenBounds { get; private set; }
    readonly Random random = new Random(Guid.NewGuid().GetHashCode());
    readonly List<Flake> flakes = new List<Flake>();
    readonly Bitmap[] sprites = new Bitmap[6];
    readonly SolidBrush[] shadows = new SolidBrush[16];
    readonly Native.Point position;
    readonly Native.Size size;
    IntPtr dc, dib, previous;
    Bitmap bitmap;
    Graphics canvas;

    public SnowWindow(Rectangle bounds, bool smoke)
    {
        ScreenBounds = bounds;
        position = smoke ? new Native.Point(-32000, -32000) : new Native.Point(bounds.X, bounds.Y);
        size = new Native.Size(bounds.Width, bounds.Height);
        emptyRunway = new Snowbank(new Rectangle(0, size.Height - 1, size.Width, 1));
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "Desktop Snow Overlay",
                X = position.X, Y = position.Y, Width = size.Width, Height = size.Height,
                Style = unchecked((int)0x80000000),
                ExStyle = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | 0x00000008
            });
            dc = Native.CreateCompatibleDC(IntPtr.Zero);
            var info = new Native.BitmapInfo
            {
                HeaderSize = 40, Width = size.Width, Height = -size.Height, Planes = 1, Bits = 32
            };
            IntPtr bits;
            dib = Native.CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || dib == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            previous = Native.SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception();
            bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
            canvas = Graphics.FromImage(bitmap);
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.InterpolationMode = InterpolationMode.Bilinear;
            for (int i = 0; i < sprites.Length; i++) sprites[i] = MakeFlake(i);
            for (int i = 0; i < shadows.Length; i++)
            {
                shadows[i] = new SolidBrush(Color.FromArgb(i * 6, 38, 69, 92));
            }
        }
        catch { Dispose(); throw; }
    }

    public void Show()
    {
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        Native.ShowWindow(Handle, 4);
    }

    public void Hide() { Native.ShowWindow(Handle, 0); }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; }
        if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; }
        base.WndProc(ref message);
    }

    static Bitmap MakeFlake(int variant)
    {
        const int size = 24;
        var sprite = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        var shape = new Random(47 + variant);
        using (var g = Graphics.FromImage(sprite))
        using (var path = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outline = new PointF[11];
            for (int i = 0; i < outline.Length; i++)
            {
                double angle = i * Math.PI * 2 / outline.Length;
                double radius = 7 + shape.NextDouble() * 3;
                outline[i] = new PointF(12 + (float)(Math.Cos(angle) * radius),
                    12 + (float)(Math.Sin(angle) * radius * .9));
            }
            path.AddClosedCurve(outline, .52f);
            bool distant = variant < 2;
            bool near = variant >= 4;
            using (var shade = new SolidBrush(Color.FromArgb(distant ? 38 : 78, 85, 111, 130)))
            {
                g.TranslateTransform(.65f, 1.1f);
                g.FillPath(shade, path);
                g.ResetTransform();
            }
            using (var fluff = new PathGradientBrush(path))
            {
                fluff.CenterPoint = new PointF(10, 10);
                fluff.CenterColor = Color.FromArgb(distant ? 130 : 246, 255, 255, 255);
                fluff.SurroundColors = new[] { Color.FromArgb(near ? 46 : 120, 216, 233, 245) };
                fluff.Blend = new Blend
                {
                    Positions = new[] { 0f, .35f, .75f, 1f },
                    Factors = new[] { 0f, .54f, .94f, 1f }
                };
                g.FillPath(fluff, path);
            }
            if (near)
            {
                using (var highlight = new SolidBrush(Color.FromArgb(120, Color.White)))
                {
                    g.FillEllipse(highlight, 7, 7, 6, 5);
                    g.FillEllipse(highlight, 12, 10, 4, 3);
                }
            }
        }
        return sprite;
    }

    public void RefreshFlakes() { flakes.Clear(); }

    void Reset(Flake flake, bool scatter, SnowWeather weather)
    {
        flake.X = (float)random.NextDouble() * size.Width;
        flake.Y = scatter ? (float)random.NextDouble() * size.Height : -5 - random.Next(80);
        double depth = random.NextDouble();
        flake.Sprite = random.Next(2) + (depth < .44 ? 0 : depth < .93 ? 2 : 4);
        flake.Size = weather.Size * (1.7f + (float)Math.Pow(depth, 2.6) * 6.4f);
        flake.Speed = 18 + flake.Size * 6.6f + random.Next(15);
        flake.Phase = (float)random.NextDouble() * 6.28f;
        flake.Flutter = .75f + (float)random.NextDouble() * 2.4f;
    }

    public bool HasDeepSnow
    {
        get { return banks.Exists(bank => Array.Exists(bank.Heights, h => h > .48f)); }
    }

    public void SeedTestPile()
    {
        var edge = new Rectangle(0, size.Height - 35, size.Width, 1);
        var handle = new IntPtr(-2);
        TrackBank(handle, edge, null, true, 1);
        var bank = banks.Find(item => item.Handle == handle);
        bank.Heights[0] = 4;
        bank.Heights[bank.Heights.Length - 1] = 9;
        bank.Occupied = true;
        edge.Offset(16, 0);
        TrackBank(handle, edge, null, true, 1.03f);
        if (!bank.Occupied || loose.Count != 0)
            throw new InvalidOperationException("Snow should move with a steadily dragged window.");
        edge.Offset(-13, 0);
        TrackBank(handle, edge, null, true, 1.07f);
        if (loose.Count == 0 || bank.Heights[0] >= 4)
            throw new InvalidOperationException("Shaking a window did not dislodge its snow.");
        var second = new Snowbank(new Rectangle(0, size.Height - 70, size.Width, 1));
        second.Heights[second.Heights.Length - 1] = 7;
        second.Occupied = true;
        banks.Add(second);

        // A falling clump should transfer snow to the lower edge.
        var clump = new FallingSnow { X = 35, Y = second.Edge.Top - 8, Vy = 180, Radius = 3 };
        if (!AdvanceClump(clump, .05f, 0, true) || clump.Surface != second || second.Heights[7] <= 0)
            throw new InvalidOperationException("Dislodged snow did not collect on a lower ledge.");
    }

    public void BeginSweep(bool smoke)
    {
        sweeping = true;
        testSweep = smoke;
        SweepFinished = false;
        routeIndex = 0;
        ResetPlowTrack();
        route.Clear();
        foreach (var bank in banks)
            if (bank.Occupied) route.Add(bank);
        route.Sort((left, right) => right.Edge.Top.CompareTo(left.Edge.Top));
        if (route.Count == 0) route.Add(emptyRunway);
    }

    void ResetPlowTrack()
    {
        travel = 0;
        clearedBins = 0;
        mound = 0;
    }

    public void EndSweep()
    {
        sweeping = false;
        route.Clear();
        loose.Clear();
    }

    void SyncBanks(List<Ledge> ledges, bool settle, float time)
    {
        foreach (var bank in banks) bank.Seen = false;
        if (settle)
        {
            var covers = new List<Rectangle>();
            bool bottomTaskbar = false;
            foreach (Ledge ledge in ledges)
            {
                Rectangle bounds = ledge.Bounds;
                bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
                if (!bounds.IntersectsWith(new Rectangle(0, 0, size.Width, size.Height))) continue;
                if (!ledge.Moves && bounds.Left <= 0 && bounds.Right >= size.Width && bounds.Bottom >= size.Height)
                    bottomTaskbar = true;
                if ((ledge.Collects || !ledge.Moves) && bounds.Top >= 6 && bounds.Top < size.Height)
                    TrackBank(ledge.Handle, new Rectangle(bounds.Left, bounds.Top, bounds.Width, 1), covers, ledge.Moves, time);
                covers.Add(bounds);
            }
            if (!bottomTaskbar)
                TrackBank(IntPtr.Zero, new Rectangle(0, size.Height - 1, size.Width, 1), null, false, time);
        }
        banks.RemoveAll(bank => !bank.Seen);
    }

    void TrackBank(IntPtr handle, Rectangle edge, List<Rectangle> covers, bool moves, float time)
    {
        var bank = banks.Find(item => item.Handle == handle);
        if (bank == null)
        {
            bank = new Snowbank(edge) { Handle = handle };
            banks.Add(bank);
        }
        int dx = edge.X - bank.Edge.X;
        int dy = edge.Y - bank.Edge.Y;
        if (moves && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
        {
            int direction = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dx) : 2 * Math.Sign(dy);
            bool reversed = direction == -bank.Direction && time - bank.LastMove < .25f && bank.Motion >= 7;
            if (!sweeping && reversed && time - bank.LastShake > .12f)
            {
                Shed(bank, dx, dy);
                bank.LastShake = time;
            }
            bank.Motion = bank.Direction == direction && time - bank.LastMove < .20f
                ? Math.Min(100, bank.Motion + Math.Abs(dx) + Math.Abs(dy)) : Math.Abs(dx) + Math.Abs(dy);
            bank.Direction = direction;
            bank.LastMove = time;
        }
        int count = (edge.Width + BinWidth - 1) / BinWidth;
        if (count != bank.Heights.Length) Array.Resize(ref bank.Heights, count);
        bank.Edge = edge;
        bank.Seen = true;
        bank.Covered.Clear();
        if (covers != null)
        {
            var strip = new Rectangle(edge.Left, edge.Top - 38, edge.Width, 40);
            foreach (var cover in covers)
                if (cover.IntersectsWith(strip)) bank.Covered.Add(cover);
        }
    }

    void Shed(Snowbank bank, int dx, int dy)
    {
        if (!bank.Occupied) return;
        for (int i = 0; i < bank.Heights.Length; i++)
        {
            float height = bank.Heights[i];
            if (height < .2f) continue;
            int x = bank.Edge.Left + i * BinWidth;
            bank.Heights[i] *= .20f + (float)random.NextDouble() * .23f;
            if (bank.Heights[i] < .45f) bank.Heights[i] = 0;
            if (x < -10 || x > size.Width || loose.Count >= 900 || Covered(bank, x, bank.Edge.Top - height)) continue;
            loose.Add(new FallingSnow {
                X = x, Y = bank.Edge.Top - height,
                Vx = Math.Max(-200, Math.Min(200, dx * 11)) + random.Next(-38, 39),
                Vy = Math.Max(-100, Math.Min(55, dy * 8)) - random.Next(5, 50),
                Radius = Math.Min(7, 1.5f + height * .37f)
            });
        }
        bank.Occupied = Array.Exists(bank.Heights, height => height > 0);
    }

    static bool Covered(Snowbank bank, float x, float y)
    {
        foreach (var cover in bank.Covered)
            if (cover.Contains((int)x, (int)y)) return true;
        return false;
    }

    void DrawBank(Snowbank bank)
    {
        if (!bank.Occupied) return;
        var edge = bank.Edge;
        var heights = bank.Heights;
        int first = Math.Max(0, -edge.Left / BinWidth - 2);
        int end = Math.Min(heights.Length, (size.Width - edge.Left) / BinWidth + 2);
        using (var path = new GraphicsPath())
        {
            for (int i = first; i < end;)
            {
                while (i < end && heights[i] < .20f) i++;
                if (i == end) break;
                int start = i;
                var ridge = new List<PointF>();
                ridge.Add(new PointF(edge.Left + start * BinWidth, edge.Top));
                while (i < end && heights[i] >= .20f)
                {
                    float previous = i > 0 ? heights[i - 1] : 0;
                    float next = i + 1 < heights.Length ? heights[i + 1] : 0;
                    float rounded = (previous + heights[i] * 2 + next) / 4;
                    ridge.Add(new PointF(edge.Left + i * BinWidth + 2.5f, edge.Top - rounded));
                    i++;
                }
                ridge.Add(new PointF(Math.Min(edge.Right, edge.Left + i * BinWidth), edge.Top));
                path.StartFigure();
                path.AddCurve(ridge.ToArray(), .38f);
                path.CloseFigure();
            }
            if (path.PointCount == 0) return;
            var state = canvas.Save();
            foreach (var cover in bank.Covered) canvas.ExcludeClip(cover);
            using (var fill = new LinearGradientBrush(
                new Rectangle(0, edge.Top - 24, 1, 25), Color.White,
                Color.FromArgb(231, 208, 224, 237), LinearGradientMode.Vertical))
            using (var rim = new Pen(Color.FromArgb(78, 116, 149, 173), .72f))
            {
                canvas.FillPath(fill, path);
                canvas.DrawPath(rim, path);
                canvas.SetClip(path, CombineMode.Intersect);
                using (var glint = new SolidBrush(Color.FromArgb(155, Color.White)))
                {
                    for (int i = first; i < end; i++)
                    {
                        if (heights[i] < 3) continue;
                        int noise = unchecked((i + 31) * 73856093);
                        float x = edge.Left + i * BinWidth + ((noise >> 12) & 3);
                        float y = edge.Top - heights[i] * (.28f + ((noise >> 20) & 7) * .054f);
                        canvas.FillEllipse(glint, x, y, 1.3f, .9f);
                    }
                }
            }
            canvas.Restore(state);
        }
    }

    Snowbank AdvancePlow(float dt)
    {
        while (routeIndex < route.Count)
        {
            var track = route[routeIndex];
            int width = Math.Min(size.Width, track.Edge.Right) - Math.Max(0, track.Edge.Left);
            if (width > 0 && (track == emptyRunway || banks.Contains(track)))
            {
                int ahead = Math.Min(track.Heights.Length - 1, clearedBins + 2);
                float resistance = Math.Min(1, (track.Heights[ahead] + mound * .25f) / 23f);
                float speed = testSweep ? (width + 65) * 5.5f : 210 - 115 * resistance;
                travel += speed * dt;
                int left = Math.Max(0, track.Edge.Left);
                float blade = left - 6 + travel;
                bool done = travel >= width + 65;
                float removed = 0;
                while (clearedBins < track.Heights.Length
                    && (done || track.Edge.Left + clearedBins * BinWidth <= blade))
                {
                    float height = track.Heights[clearedBins];
                    float dust = Math.Min(height, .26f + .17f * (clearedBins % 7) / 6f);
                    track.Heights[clearedBins++] = dust;
                    removed += height - dust;
                }
                mound = Math.Min(29, mound * (float)Math.Exp(-dt * 3.4f) + removed * .27f);
                if (!done) return track;
                track.Occupied = Array.Exists(track.Heights, height => height >= .20f);
            }
            routeIndex++;
            ResetPlowTrack();
        }
        SweepFinished = true;
        return null;
    }

    void DrawPlow(Snowbank bank)
    {
        var edge = bank.Edge;
        int left = Math.Max(0, edge.Left);
        int right = Math.Min(size.Width, edge.Right);
        float blade = left - 6 + travel;
        var state = canvas.Save();
        canvas.SetClip(new Rectangle(left, edge.Top - 40, right - left, 44), CombineMode.Intersect);
        foreach (var cover in bank.Covered) canvas.ExcludeClip(cover);
        float x = blade - 56;
        float y = edge.Top;
        canvas.FillEllipse(shadows[12], x + 2, y - 3, 56, 5);
        canvas.FillRectangle(tire, x + 6, y - 14, 42, 5);
        canvas.FillPolygon(orange, new[] {
            new PointF(x + 4, y - 18), new PointF(x + 12, y - 21),
            new PointF(x + 13, y - 30), new PointF(x + 31, y - 30),
            new PointF(x + 36, y - 20), new PointF(x + 47, y - 19),
            new PointF(x + 49, y - 12), new PointF(x + 4, y - 12)
        });
        canvas.FillPolygon(glass, new[] {
            new PointF(x + 16, y - 27), new PointF(x + 29, y - 27),
            new PointF(x + 33, y - 20), new PointF(x + 16, y - 20)
        });
        canvas.FillRectangle(tire, x + 11, y - 32, 22, 2);
        canvas.FillRectangle(tire, x + 39, y - 25, 2, 7);
        canvas.FillRectangle(amber, x + 22, y - 34, 5, 2);
        canvas.FillRectangle(snow, x + 45, y - 18, 3, 2);
        DrawWheel(x + 15, y - 8, 8, blade);
        DrawWheel(x + 39, y - 6, 6, blade);
        canvas.FillRectangle(tire, blade - 11, y - 8, 7, 3);
        canvas.FillPolygon(ice, new[] {
            new PointF(blade - 9, y - 15), new PointF(blade - 4, y - 16),
            new PointF(blade - 2, y - 9), new PointF(blade + 1, y - 4),
            new PointF(blade + 4, y - 1), new PointF(blade - 3, y),
            new PointF(blade - 7, y - 5)
        });
        if (mound >= .55f)
        {
            float height = 2 + mound * .65f;
            float width = 5 + mound * .94f;
            using (var pushed = new GraphicsPath())
            {
                pushed.AddBezier(blade - 1, y, blade + 1, y - height * 1.5f,
                    blade + width * .45f, y - height, blade + width, y);
                pushed.CloseFigure();
                canvas.FillPath(ice, pushed);
            }
            canvas.FillEllipse(snow, blade + width * .13f, y - height * .78f, width * .44f, height * .44f);
            for (int i = 0; i < 5; i++)
            {
                float phase = (travel * .031f + i * .23f) % 1;
                canvas.FillEllipse(snow, blade + phase * (12 + i * 2),
                    y - 5 - (float)Math.Sin(phase * Math.PI) * height, 1.5f, 1.5f);
            }
        }
        canvas.Restore(state);
    }

    void DrawWheel(float x, float y, float radius, float distance)
    {
        canvas.FillEllipse(tire, x - radius, y - radius, radius * 2, radius * 2);
        canvas.FillEllipse(ice, x - radius * .53f, y - radius * .53f, radius * 1.06f, radius * 1.06f);
        for (int i = 0; i < 4; i++)
        {
            double angle = distance / radius + i * Math.PI / 2;
            float lugX = x + radius * .34f * (float)Math.Cos(angle);
            float lugY = y + radius * .34f * (float)Math.Sin(angle);
            canvas.FillEllipse(tire, lugX - .7f, lugY - .7f, 1.4f, 1.4f);
        }
    }

    Snowbank FindLanding(float center, float before, float after, out int bin)
    {
        Snowbank landing = null;
        bin = 0;
        float highest = float.MaxValue;
        foreach (var bank in banks)
        {
            if (center < bank.Edge.Left || center >= bank.Edge.Right) continue;
            int index = (int)(center - bank.Edge.Left) / BinWidth;
            float top = bank.Edge.Top - bank.Heights[index];
            if (before >= top || after < top || top >= highest || Covered(bank, center, top)) continue;
            landing = bank;
            bin = index;
            highest = top;
        }
        return landing;
    }

    static void Deposit(Snowbank bank, int bin, float amount)
    {
        if (amount <= 0) return;
        float limit = Math.Min(24, bank.Edge.Top - 2);
        for (int offset = -4; offset <= 4; offset++)
        {
            int index = bin + offset;
            if (index < 0 || index >= bank.Heights.Length) continue;
            float weight = 1 - Math.Abs(offset) / 5f;
            bank.Heights[index] = Math.Min(limit, bank.Heights[index] + amount * weight * weight);
        }
        bank.Occupied = true;
    }

    bool AdvanceClump(FallingSnow clump, float dt, float breeze, bool catchesSnow)
    {
        float oldBottom = clump.Y + clump.Radius;
        clump.X += clump.Vx * dt;
        var bank = clump.Surface;
        float center = clump.X + clump.Radius * .5f;
        if (bank != null && catchesSnow && banks.Contains(bank)
            && center >= bank.Edge.Left && center < bank.Edge.Right && !Covered(bank, center, bank.Edge.Top - 2))
        {
            int index = (int)(center - bank.Edge.Left) / BinWidth;
            clump.Vx *= (float)Math.Exp(-4.5f * dt);
            float shed = Math.Min(clump.Radius, dt * (3.8f + Math.Abs(clump.Vx) * .026f));
            Deposit(bank, index, shed * .8f);
            clump.Radius -= shed;
            clump.Y = bank.Edge.Top - bank.Heights[index] - clump.Radius;
            return clump.Radius > .55f;
        }

        clump.Surface = null;
        clump.Vy += 360 * dt;
        clump.Vx += (breeze * .32f - clump.Vx * .65f) * dt;
        clump.Y += clump.Vy * dt;
        int bin = 0;
        bank = catchesSnow ? FindLanding(center, oldBottom, clump.Y + clump.Radius, out bin) : null;
        if (bank != null)
        {
            Deposit(bank, bin, clump.Radius * .44f);
            clump.Radius *= .66f;
            clump.Y = bank.Edge.Top - bank.Heights[bin] - clump.Radius;
            clump.Vy = 0;
            clump.Surface = bank;
        }
        return clump.Radius > .55f && clump.Y <= size.Height + 12 && clump.X > -120 && clump.X < size.Width + 120;
    }

    void BeginFrame(Rectangle settingsBounds)
    {
        canvas.ResetClip();
        canvas.Clear(Color.Transparent);
        Rectangle covered = Rectangle.Intersect(ScreenBounds, settingsBounds);
        if (covered.IsEmpty) return;
        covered.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
        canvas.ExcludeClip(covered);
    }

    public void VerifySettingsClip()
    {
        Rectangle dialog = new Rectangle(ScreenBounds.Left + 8, ScreenBounds.Top + 8, 32, 32);
        BeginFrame(dialog);
        var bank = new Snowbank(new Rectangle(0, 30, 64, 1)) { Occupied = true };
        for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 12;
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(22, 24).A != 0 || bitmap.GetPixel(48, 24).A == 0)
            throw new InvalidOperationException("A snowbank did not respect the settings dialog bounds.");

        BeginFrame(Rectangle.Empty);
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(22, 24).A == 0)
            throw new InvalidOperationException("The snowbank did not reappear after settings closed.");
    }

    public void Frame(float dt, float time, SnowSettings settings, List<Ledge> ledges, bool falling,
        Rectangle settingsBounds)
    {
        var weather = settings.Weather;
        int target = Math.Max(25, Math.Min(1300,
            (int)((double)size.Width * size.Height / 2073600 * weather.Density)));
        if (flakes.Count > target) flakes.RemoveRange(target, flakes.Count - target);
        while (flakes.Count < target)
        {
            var flake = new Flake();
            Reset(flake, true, weather);
            flakes.Add(flake);
        }
        if (!sweeping || !testSweep) SyncBanks(ledges, settings.Settle, time);
        BeginFrame(settingsBounds);
        float wind = weather.Wind * settings.Wind / 100f;
        float deposit = weather.Deposit * settings.Accumulation / 100f;
        float breeze = (7 + 13 * (float)Math.Sin(time * .34f)) * wind;
        if (falling)
        foreach (var flake in flakes)
        {
            float oldBottom = flake.Y + flake.Size;
            float flutter = time * flake.Flutter + flake.Phase;
            float eddy = (float)Math.Sin(time * .58f + flake.Y * .005f + flake.X * .002f);
            flake.X += (breeze + 19 * eddy * wind
                + (6 + flake.Size * 1.5f) * (float)Math.Sin(flutter)) * dt;
            flake.Y += flake.Speed * (1 + .14f * (float)Math.Cos(flutter * .73f)) * dt;
            if (flake.X < -10) flake.X += size.Width + 20;
            if (flake.X > size.Width + 10) flake.X -= size.Width + 20;

            int bin = 0;
            var landing = !sweeping && deposit > 0 ? FindLanding(flake.X + flake.Size / 2,
                oldBottom, flake.Y + flake.Size, out bin) : null;
            if (landing != null)
            {
                Deposit(landing, bin, (.42f + flake.Size * .22f) * deposit);
                Reset(flake, false, weather);
                continue;
            }
            if (flake.Y > size.Height)
            {
                Reset(flake, false, weather);
                continue;
            }
            if (flake.Y < -10) continue;
            float width = flake.Size * (.89f + .17f * (float)Math.Cos(flutter));
            float padding = flake.Size * .20f;
            canvas.DrawImage(sprites[flake.Sprite], flake.X - padding, flake.Y - padding,
                width + padding * 2, flake.Size + padding * 2);
        }
        var activeTrack = sweeping ? AdvancePlow(dt) : null;
        foreach (var bank in banks) DrawBank(bank);
        if (activeTrack != null) DrawPlow(activeTrack);
        for (int i = loose.Count - 1; i >= 0; i--)
        {
            var clump = loose[i];
            if (!AdvanceClump(clump, dt, breeze, !sweeping && settings.Settle))
            {
                loose.RemoveAt(i);
                continue;
            }
            canvas.FillEllipse(ice, clump.X, clump.Y, clump.Radius + 1, clump.Radius);
            canvas.FillEllipse(snow, clump.X, clump.Y - .6f, clump.Radius, clump.Radius * .77f);
        }
        DrawAnimals(dt, falling && !sweeping && settings.Animals);
        canvas.Flush(FlushIntention.Sync);
        var dst = position;
        var extent = size;
        var src = new Native.Point(0, 0);
        var blend = new Native.Blend { Alpha = 255, Format = 1 };
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref extent, dc, ref src, 0, ref blend, 2))
            throw new System.ComponentModel.Win32Exception();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
        if (canvas != null) canvas.Dispose();
        DisposeAnimals();
        snow.Dispose();
        ice.Dispose();
        orange.Dispose();
        amber.Dispose();
        tire.Dispose();
        glass.Dispose();
        if (bitmap != null) bitmap.Dispose();
        foreach (var sprite in sprites) if (sprite != null) sprite.Dispose();
        foreach (var shadow in shadows) if (shadow != null) shadow.Dispose();
        if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
        if (dib != IntPtr.Zero) Native.DeleteObject(dib);
        if (dc != IntPtr.Zero) Native.DeleteDC(dc);
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point
    {
        public int X, Y;
        public Point(int x, int y) { X = x; Y = y; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Size
    {
        public int Width, Height;
        public Size(int width, int height) { Width = width; Height = height; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] public struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint Colors, ImportantColors, ColorTable;
    }

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);
    public delegate bool WindowVisitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr window);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr window, int attribute, out int value, int bytes);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int bytes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string type, string caption);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string type, string caption);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, ref Point position,
        ref Size size, IntPtr source, ref Point start, uint colorKey, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
}

