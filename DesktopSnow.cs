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
    readonly HashSet<IntPtr> pendingMinimizations = new HashSet<IntPtr>();
    readonly Native.WindowEvent windowMinimizing;
    readonly IntPtr minimizeHook;
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
    IntPtr movingWindow, attachmentWindow, frameInsetOwner;
    Padding frameInsets;
    uint frameInsetDpi;
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
        if (!smoke)
        {
            windowMinimizing = OnWindowMinimizing;
            minimizeHook = Native.SetWinEventHook(Native.WindowMinimizeStart, Native.WindowMinimizeStart,
                IntPtr.Zero, windowMinimizing, 0, 0, 2); // Out-of-context, skip our own process.
        }
        timer.Start();
    }

    static void VerifyConfiguration()
    {
        if (!IsPopupClass("NotifyIconOverflowWindow") || !IsPopupClass("TopLevelWindowForOverflowXamlIsland")
            || !IsPopupClass("tooltips_class32") || IsPopupClass("CabinetWClass")
            || !IsRoundedPopupClass("XamlExplorerHostIslandWindow") || !IsRoundedPopupClass("TaskListThumbnailWnd")
            || IsRoundedPopupClass("CabinetWClass")
            || !IsPopupStyle(0x08000080) || IsPopupStyle(0))
            throw new InvalidOperationException("Temporary window classification is incorrect.");
        var custom = new SnowSettings { Preset = 2, Wind = 65, Accumulation = 175, Settle = false, AutoPause = false, Animals = true };
        var restored = SnowSettings.Read(custom.Write());
        if (restored.Weather != SnowWeather.Presets[2] || restored.Wind != 65
            || restored.Accumulation != 175 || restored.Settle || restored.AutoPause || !restored.Animals)
            throw new InvalidOperationException("Settings did not round-trip.");
        var legacy = SnowSettings.Read(new[] { "2", "65", "175", "False", "False" });
        if (legacy.Animals || legacy.Preset != 2 || legacy.Accumulation != 175)
            throw new InvalidOperationException("Settings from an earlier version did not load.");
        var raw = new Rectangle(-1600, 30, 900, 600);
        var visible = Rectangle.FromLTRB(raw.Left + 8, raw.Top + 8, raw.Right - 8, raw.Bottom - 8);
        Padding insets;
        if (!TryFrameInsets(raw, visible, 96, out insets))
            throw new InvalidOperationException("The visible frame inset was not measured.");
        raw.Offset(23, 15);
        var moved = ApplyFrameInsets(raw, insets, 96, 96);
        var scaled = ApplyFrameInsets(raw, insets, 96, 144);
        if (moved.Top != visible.Top + 15 || moved.Left != visible.Left + 23
            || scaled.Top != raw.Top + 12 || scaled.Right != raw.Right - 12)
            throw new InvalidOperationException("The lagging frame was not aligned with the live rectangle.");
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
            var window = new SnowWindow(screen.Bounds, smoke, ReadDragInsets);
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
        movingWindow = settings.Settle && !smoke ? Native.GetMovingWindow() : IntPtr.Zero;
        attachmentWindow = settings.Settle
            ? (movingWindow != IntPtr.Zero ? movingWindow : Native.GetForegroundWindow())
            : IntPtr.Zero;
        int interval = movingWindow == IntPtr.Zero ? 33 : 15;
        if (timer.Interval != interval) timer.Interval = interval;
        var ledges = new List<Ledge>();
        if (settings.Settle || settings.Animals)
        {
            AddLedge(ledges, Native.FindWindow("Shell_TrayWnd", null), false);
            IntPtr secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                AddLedge(ledges, secondary, false);
        }
        IntPtr shell = Native.GetShellWindow();
        bool trackWindows = settings.Settle || settings.Animals;
        var className = new System.Text.StringBuilder(128);
        Native.EnumWindows((handle, data) =>
        {
            if (handle == shell || !Native.IsWindowVisible(handle) || Native.IsIconic(handle)) return true;
            Native.GetClassName(handle, className, className.Capacity);
            string name = className.ToString();
            bool rounded = IsRoundedPopupClass(name);
            if (rounded || IsPopupClass(name))
            {
                bool hoverLabel = name == "tooltips_class32" || name == "XamlExplorerHostIslandWindow"
                    || name == "Xaml_WindowedPopupClass";
                AddLedge(ledges, handle, true, true, rounded, hoverLabel ? name : null);
                return true;
            }
            if (!trackWindows || Native.GetWindowTextLength(handle) == 0) return true;
            uint pid;
            Native.GetWindowThreadProcessId(handle, out pid);
            if (pid != ownProcess)
            {
                bool transient = IsPopupStyle(Native.GetWindowLong(handle, Native.ExtendedStyle));
                AddLedge(ledges, handle, true, transient);
            }
            return true;
        }, IntPtr.Zero);
        if (smoke && frames < SnowWeather.Presets.Length)
        {
            settings.Preset = frames;
            settings.Animals = true;
            foreach (var window in windows)
            {
                window.RefreshFlakes();
                if (frames == 0)
                {
                    window.VerifySettingsClip();
                    window.VerifyWindowAttachment();
                    window.VerifyDragStrip();
                    window.VerifyMinimizedSnow();
                    window.VerifyTaskbarClip();
                    window.VerifyTaskbarShadow();
                    window.VerifyPopupClip();
                    window.VerifyNativePopupOrder();
                    window.VerifyTrayTooltipClip();
                    window.VerifyAnimalClip();
                }
                window.TestAnimalFrame(frames);
            }
        }
        if (smoke && frames == 3)
        {
            foreach (var window in windows) window.SeedTestPile();
            StartSweep();
        }
        ReleaseMinimizingWindows(ledges, (float)now);
        Rectangle settingsBounds = GetSettingsBounds();
        bool finished = sweeping;
        foreach (var window in windows)
        {
            window.Frame(dt, (float)now, settings, ledges, !paused, settingsBounds, movingWindow);
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

    void OnWindowMinimizing(IntPtr hook, uint eventId, IntPtr handle, int objectId, int childId,
        uint sourceThread, uint time)
    {
        if (handle != IntPtr.Zero && objectId == 0 && childId == 0 && settings.Settle)
            pendingMinimizations.Add(handle);
    }

    void ReleaseMinimizingWindows(List<Ledge> ledges, float time)
    {
        if (pendingMinimizations.Count == 0) return;
        var handles = new HashSet<IntPtr>(pendingMinimizations);
        pendingMinimizations.Clear();
        if (!settings.Settle) return;
        // Minimize can be announced before the animation updates the window bounds.
        ledges.RemoveAll(ledge => handles.Contains(ledge.Handle));
        foreach (var handle in handles)
            foreach (var window in windows) window.DropMinimizedSnow(handle, time);
    }

    Padding? ReadDragInsets(IntPtr handle)
    {
        if (handle != frameInsetOwner) return null;
        return ScaleFrameInsets(frameInsets, frameInsetDpi, Native.WindowDpi(handle));
    }

    static bool IsPopupClass(string className)
    {
        switch (className)
        {
            case "NotifyIconOverflowWindow":
            case "TopLevelWindowForOverflowXamlIsland":
            case "tooltips_class32":
            case "#32768":
                return true;
            default:
                return false;
        }
    }

    static bool IsRoundedPopupClass(string className)
    {
        switch (className)
        {
            case "tooltips_class32":
            case "XamlExplorerHostIslandWindow":
            case "Xaml_WindowedPopupClass":
            case "TopLevelWindowForOverflowXamlIsland":
            case "TaskListThumbnailWnd":
                return true;
            default:
                return false;
        }
    }

    static bool IsPopupStyle(int styles)
    {
        const int ToolWindow = 0x80;
        const int NoActivate = 0x08000000;
        return (styles & (ToolWindow | NoActivate)) == (ToolWindow | NoActivate);
    }

    void AddLedge(List<Ledge> ledges, IntPtr handle, bool frame, bool transient = false, bool rounded = false,
        string hoverClass = null)
    {
        if (handle == IntPtr.Zero) return;
        if (ledges.Exists(edge => edge.Handle == handle)) return;
        int cloaked;
        if (frame && Native.DwmGetWindowAttributeInt(handle, 14, out cloaked, 4) == 0 && cloaked != 0) return;
        Rectangle bounds;
        if (!TryReadLedgeBounds(handle, frame, frame && !transient && handle == attachmentWindow, out bounds)) return;
        if (bounds.Width < (transient ? 1 : 32) || bounds.Height < (transient ? 1 : 20)) return;

        Rectangle? popupClip = null;
        if (hoverClass != null)
        {
            var screen = Screen.FromRectangle(bounds);
            if (screen.WorkingArea.Bottom < screen.Bounds.Bottom)
            {
                uint dpi = Native.WindowDpi(handle);
                var tray = Rectangle.FromLTRB(screen.Bounds.Left, screen.WorkingArea.Bottom,
                    screen.Bounds.Right, screen.Bounds.Bottom);
                bool nativeTooltip = hoverClass == "tooltips_class32";
                Point? hoverPoint = handle != Native.GetForegroundWindow() ? Cursor.Position : (Point?)null;
                popupClip = TrayTooltipBody(bounds, tray, dpi, nativeTooltip, hoverPoint);
            }
        }
        ledges.Add(new Ledge(handle, bounds, frame && !transient && !Native.IsZoomed(handle), frame,
            transient, rounded, transient && (Native.GetWindowLong(handle, Native.ExtendedStyle) & Native.TopmostStyle) != 0,
            popupClip));
    }

    bool TryReadLedgeBounds(IntPtr handle, bool frame, bool trackMotion, out Rectangle bounds)
    {
        if (trackMotion && handle == movingWindow && frameInsetOwner == handle)
        {
            Native.Rect current;
            if (Native.GetWindowRect(handle, out current))
            {
                bounds = ApplyFrameInsets(current.ToRectangle(), frameInsets, frameInsetDpi, Native.WindowDpi(handle));
                if (!bounds.IsEmpty) return true;
            }
        }
        Native.Rect visible = new Native.Rect();
        bool hasVisible = frame && Native.DwmGetWindowAttribute(handle, 9, out visible, 16) == 0
            && visible.Right > visible.Left && visible.Bottom > visible.Top;
        bounds = hasVisible ? visible.ToRectangle() : Rectangle.Empty;
        if (!trackMotion && hasVisible) return true;

        Native.Rect raw;
        if (!Native.GetWindowRect(handle, out raw)) return hasVisible;
        var live = raw.ToRectangle();
        if (!hasVisible) bounds = live;
        if (!trackMotion) return true;

        uint dpi = Native.WindowDpi(handle);
        bool moving = handle == movingWindow;

        Padding insets;
        if (hasVisible && TryFrameInsets(live, bounds, dpi, out insets))
        {
            frameInsetOwner = handle;
            frameInsets = insets;
            frameInsetDpi = dpi;
        }
        else if (!moving && frameInsetOwner == handle && hasVisible)
        {
            frameInsetOwner = IntPtr.Zero;
        }
        return true;
    }

    static bool TryFrameInsets(Rectangle raw, Rectangle visible, uint dpi, out Padding insets)
    {
        insets = new Padding(visible.Left - raw.Left, visible.Top - raw.Top,
            raw.Right - visible.Right, raw.Bottom - visible.Bottom);
        float limit = 32 * dpi / 96f;
        return raw.Contains(visible) && insets.Left <= limit && insets.Top <= limit
            && insets.Right <= limit && insets.Bottom <= limit;
    }

    static Rectangle ApplyFrameInsets(Rectangle raw, Padding insets, uint oldDpi, uint dpi)
    {
        return DragLayer.InsetFrame(raw, ScaleFrameInsets(insets, oldDpi, dpi));
    }

    static Padding ScaleFrameInsets(Padding insets, uint oldDpi, uint dpi)
    {
        float scale = (float)dpi / oldDpi;
        return new Padding((int)Math.Round(insets.Left * scale), (int)Math.Round(insets.Top * scale),
            (int)Math.Round(insets.Right * scale), (int)Math.Round(insets.Bottom * scale));
    }

    internal static Rectangle TrayTooltipBody(Rectangle bounds, Rectangle tray, uint dpi,
        bool nativeTooltip, Point? hoverPoint)
    {
        float scale = (dpi == 0 ? 96 : dpi) / 96f;
        if (bounds.Height < 28 * scale || bounds.Width < 44 * scale
            || bounds.Bottom < tray.Top - 18 * scale || bounds.Bottom > tray.Top + 4 * scale)
            return bounds;

        // A shell host can draw either a tooltip or a flyout. Keep the small-label rule
        // and use the cursor over the label's tray anchor to recognize taller status labels.
        if (!nativeTooltip && (bounds.Height > 76 * scale || bounds.Width < bounds.Height * 1.7f))
        {
            if (!hoverPoint.HasValue || !tray.Contains(hoverPoint.Value)
                || hoverPoint.Value.X < bounds.Left - 10 * scale || hoverPoint.Value.X > bounds.Right + 10 * scale
                || bounds.Height > 200 * scale || bounds.Width > 420 * scale)
                return bounds;
        }

        int side = (int)Math.Round(4 * scale);
        int bottom = (int)Math.Round(8 * scale);
        return Rectangle.FromLTRB(bounds.Left + side, bounds.Top, bounds.Right - side, bounds.Bottom - bottom);
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
            if (minimizeHook != IntPtr.Zero) Native.UnhookWinEvent(minimizeHook);
            GC.KeepAlive(windowMinimizing);
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
    public readonly Rectangle Bounds, ClipBounds;
    public readonly bool Collects, Moves, Transient, RoundedPopup, TopmostPopup;

    public Ledge(IntPtr handle, Rectangle bounds, bool collects, bool moves, bool transient = false,
        bool roundedPopup = false, bool topmostPopup = false, Rectangle? clipBounds = null)
    {
        Handle = handle;
        Bounds = bounds;
        ClipBounds = clipBounds ?? bounds;
        Collects = collects;
        Moves = moves;
        Transient = transient;
        RoundedPopup = roundedPopup;
        TopmostPopup = topmostPopup;
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
        public Point MotionOrigin;
        public float LastMove, LastShake = -10;
        public bool Taskbar, WindowLedge;
        public readonly List<Rectangle> Covered = new List<Rectangle>();
        public bool Occupied;
        public readonly List<SnowBreak> Breaks = new List<SnowBreak>();

        public Snowbank(Rectangle edge)
        {
            Edge = edge;
            MotionOrigin = edge.Location;
            Heights = new float[(edge.Width + BinWidth - 1) / BinWidth];
        }
    }

    struct SnowBreak
    {
        public int First, Count;
        public float Due, Vx, Vy;
    }

    sealed class FallingSnow
    {
        public float X, Y, Vx, Vy, Radius, Delay;
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
    bool popupAdjustedOrder;
    readonly HashSet<IntPtr> windowsInFront = new HashSet<IntPtr>();
    const int ShadowRadius = 23;
    const int ShadowDepth = 32;
    readonly List<Rectangle> shadowCasters = new List<Rectangle>();
    Rectangle shadowStrip;
    Bitmap shadowBitmap;
    int[] shadowPixels;
    TextureBrush shadowBrush;
    Graphics canvas;
    readonly Func<IntPtr, Padding?> readDragInsets;
    DragLayer dragStrip;
    IntPtr bitmapBits;
    IntPtr failedDragTarget;

    public SnowWindow(Rectangle bounds, bool smoke, Func<IntPtr, Padding?> readDragInsets)
    {
        this.readDragInsets = readDragInsets;
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
            dib = Native.CreateDIBSection(dc, ref info, 0, out bitmapBits, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || dib == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            previous = Native.SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception();
            bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bitmapBits);
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
        Native.SetWindowPos(Handle, Native.TopmostWindow, 0, 0, 0, 0, Native.KeepGeometryAndFocus);
        popupAdjustedOrder = false;
        Native.ShowWindow(Handle, 4);
    }

    public void Hide()
    {
        HideDragStrip();
        Native.ShowWindow(Handle, 0);
    }

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
        if (bank.Breaks.Count == 0 || bank.Heights[bank.Heights.Length - 1] != 9)
            throw new InvalidOperationException("The whole snowbank broke free in a single frame.");
        TrackBank(handle, edge, null, true, 1.32f);
        if (bank.Breaks.Count != 0 || bank.Heights[bank.Heights.Length - 1] >= 9)
            throw new InvalidOperationException("The delayed clump did not finish releasing.");
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
        {
            bank.Breaks.Clear();
            if (bank.Occupied) route.Add(bank);
        }
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

    List<Rectangle> CollectWindowBounds(List<Ledge> ledges)
    {
        var windowBounds = new List<Rectangle>();
        foreach (Ledge ledge in ledges)
        {
            if (!ledge.Moves || ledge.Transient || !ledge.Bounds.IntersectsWith(ScreenBounds)) continue;
            var bounds = ledge.Bounds;
            bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
            windowBounds.Add(bounds);
        }
        return windowBounds;
    }

    void SyncBanks(List<Ledge> ledges, List<Rectangle> windowBounds, bool settle, float time)
    {
        foreach (var bank in banks) bank.Seen = false;
        if (settle)
        {
            var covers = new List<Rectangle>();
            bool bottomTaskbar = false;
            foreach (Ledge ledge in ledges)
            {
                if (ledge.Transient) continue;
                Rectangle bounds = ledge.Bounds;
                bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
                if (!bounds.IntersectsWith(new Rectangle(0, 0, size.Width, size.Height))) continue;
                if (!ledge.Moves && bounds.Left <= 0 && bounds.Right >= size.Width && bounds.Bottom >= size.Height)
                    bottomTaskbar = true;
                if ((ledge.Collects || !ledge.Moves) && bounds.Top >= 6 && bounds.Top < size.Height)
                    TrackBank(ledge.Handle, new Rectangle(bounds.Left, bounds.Top, bounds.Width, 1),
                        ledge.Moves ? covers : windowBounds, ledge.Moves, time);
                covers.Add(bounds);
            }
            if (!bottomTaskbar)
                TrackBank(IntPtr.Zero, new Rectangle(0, size.Height - 1, size.Width, 1), null, false, time);
        }
        if (settle)
            foreach (var bank in banks)
                if (!bank.Seen && bank.WindowLedge && Native.IsIconic(bank.Handle)) DropSnowbank(bank, time);
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
        int dx = edge.X - bank.MotionOrigin.X;
        int dy = edge.Y - bank.MotionOrigin.Y;
        if (moves && (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3))
        {
            int direction = Math.Abs(dx) >= Math.Abs(dy) ? Math.Sign(dx) : 2 * Math.Sign(dy);
            bool reversed = direction == -bank.Direction && time - bank.LastMove < .25f && bank.Motion >= 7;
            if (!sweeping && reversed && time - bank.LastShake > .12f)
            {
                QueueSnowBreaks(bank, dx, dy, time);
                bank.LastShake = time;
            }
            bank.Motion = bank.Direction == direction && time - bank.LastMove < .20f
                ? Math.Min(100, bank.Motion + Math.Abs(dx) + Math.Abs(dy)) : Math.Abs(dx) + Math.Abs(dy);
            bank.Direction = direction;
            bank.LastMove = time;
            bank.MotionOrigin = edge.Location;
        }
        int count = (edge.Width + BinWidth - 1) / BinWidth;
        if (count != bank.Heights.Length) Array.Resize(ref bank.Heights, count);
        bank.Edge = edge;
        bank.Taskbar = !moves && handle != IntPtr.Zero;
        bank.WindowLedge = moves;
        bank.Seen = true;
        bank.Covered.Clear();
        if (covers != null)
        {
            var strip = new Rectangle(edge.Left, edge.Top - 38, edge.Width, 42);
            foreach (var cover in covers)
                if (cover.IntersectsWith(strip)) bank.Covered.Add(cover);
        }
        if (!sweeping) ReleaseSnowBreaks(bank, time);
    }

    public void DropMinimizedSnow(IntPtr handle, float time)
    {
        var bank = banks.Find(item => item.Handle == handle && item.WindowLedge);
        if (bank == null) return;
        DropSnowbank(bank, time);
        banks.Remove(bank);
    }

    void DropSnowbank(Snowbank bank, float time)
    {
        bank.Breaks.Clear();
        QueueSnowBreaks(bank, 0, 0, time, true);
        ReleaseSnowBreaks(bank, time, true);
    }

    void QueueSnowBreaks(Snowbank bank, int dx, int dy, float time, bool entireBank = false)
    {
        if (!bank.Occupied || bank.Breaks.Count != 0) return;
        for (int i = 0; i < bank.Heights.Length;)
        {
            if (bank.Heights[i] < (entireBank ? .2f : 1f))
            {
                i++;
                continue;
            }
            int count = Math.Min(random.Next(4, 13), bank.Heights.Length - i);
            bank.Breaks.Add(new SnowBreak
            {
                First = i, Count = count,
                Due = bank.Breaks.Count == 0 ? time : time + .025f + (float)random.NextDouble() * .19f,
                Vx = Math.Max(-175, Math.Min(175, dx * 11)),
                Vy = Math.Max(-80, Math.Min(85, dy * 8))
            });
            i += count + (entireBank ? 0 : random.Next(2, 10));
        }
    }

    void ReleaseSnowBreaks(Snowbank bank, float time, bool entireBank = false)
    {
        bool changed = false;
        for (int i = bank.Breaks.Count - 1; i >= 0; i--)
        {
            var piece = bank.Breaks[i];
            if (!entireBank && piece.Due > time) continue;
            bank.Breaks.RemoveAt(i);
            int end = Math.Min(bank.Heights.Length, piece.First + piece.Count);
            float mass = 0, moment = 0, peak = 0;
            for (int bin = piece.First; bin < end; bin++)
            {
                float height = bank.Heights[bin];
                float removed = entireBank ? height : height * (.37f + (float)random.NextDouble() * .32f);
                bank.Heights[bin] -= removed;
                mass += removed;
                moment += removed * bin;
                peak = Math.Max(peak, height);
            }
            if (mass <= .3f) continue;
            changed = true;

            float x = bank.Edge.Left + (moment / mass + .5f) * BinWidth;
            float y = bank.Edge.Top - peak * .7f;
            if (x < -10 || x > size.Width + 10 || Covered(bank, x, y)) continue;
            int fragments = Math.Min(4, 1 + (int)(mass / 13));
            for (int part = 0; part < fragments && loose.Count < 900; part++)
            {
                float span = piece.Count * BinWidth * .65f;
                loose.Add(new FallingSnow
                {
                    X = x + ((float)random.NextDouble() - .5f) * span,
                    Y = y + random.Next(-4, 5),
                    Vx = piece.Vx + random.Next(-75, 76),
                    Vy = entireBank ? random.Next(8, 49) : piece.Vy + random.Next(-40, 37),
                    Delay = entireBank ? Math.Max(0, piece.Due - time) : 0,
                    Radius = Math.Min(7, (1.2f + peak * .29f) * (.45f + (float)random.NextDouble() * .55f))
                });
            }
        }
        if (changed) bank.Occupied = Array.Exists(bank.Heights, height => height >= .2f);
    }

    static bool Covered(Snowbank bank, float x, float y)
    {
        foreach (var cover in bank.Covered)
            if (cover.Contains((int)x, (int)y)) return true;
        return false;
    }

    TextureBrush TaskbarShadow(Rectangle edge, List<Rectangle> windowBounds)
    {
        if (windowBounds == null || windowBounds.Count == 0) return null;
        int left = Math.Max(0, edge.Left);
        int width = Math.Min(size.Width, edge.Right) - left;
        if (width <= 0) return null;
        var strip = new Rectangle(left, edge.Top - ShadowDepth + 2, width, ShadowDepth);
        var reach = Rectangle.Inflate(strip, ShadowRadius, ShadowRadius);
        var casters = new List<Rectangle>();
        foreach (var bounds in windowBounds)
            if (bounds.Top < edge.Top && bounds.IntersectsWith(reach)) casters.Add(bounds);
        if (casters.Count == 0) return null;

        if (shadowBrush != null && shadowStrip == strip && SameRectangles(shadowCasters, casters))
            return shadowBrush;

        if (shadowBrush != null) shadowBrush.Dispose();
        if (shadowBitmap == null || shadowBitmap.Width != width)
        {
            if (shadowBitmap != null) shadowBitmap.Dispose();
            shadowBitmap = new Bitmap(width, ShadowDepth, PixelFormat.Format32bppPArgb);
            shadowPixels = new int[width * ShadowDepth];
        }
        else Array.Clear(shadowPixels, 0, shadowPixels.Length);

        const int radiusSquared = ShadowRadius * ShadowRadius;
        foreach (var bounds in casters)
        {
            int start = Math.Max(strip.Left, bounds.Left - ShadowRadius);
            int end = Math.Min(strip.Right, bounds.Right + ShadowRadius);
            for (int y = strip.Top; y < strip.Bottom; y++)
            {
                int dy = Math.Max(0, Math.Max(bounds.Top - y, y - bounds.Bottom));
                if (dy >= ShadowRadius) continue;
                int row = (y - strip.Top) * width;
                for (int x = start; x < end; x++)
                {
                    int dx = Math.Max(0, Math.Max(bounds.Left - x, x - bounds.Right));
                    int distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared >= radiusSquared) continue;
                    float falloff = 1 - distanceSquared / (float)radiusSquared;
                    int alpha = (int)(47 * falloff * falloff + .5f);
                    int index = row + x - strip.Left;
                    // Use the strongest contribution where two window shadows overlap.
                    if (alpha <= (int)((uint)shadowPixels[index] >> 24)) continue;
                    shadowPixels[index] = (alpha << 24) | ((alpha * 36 / 255) << 16)
                        | ((alpha * 53 / 255) << 8) | (alpha * 77 / 255);
                }
            }
        }

        var data = shadowBitmap.LockBits(new Rectangle(0, 0, width, ShadowDepth),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(shadowPixels, 0, data.Scan0, shadowPixels.Length); }
        finally { shadowBitmap.UnlockBits(data); }
        shadowStrip = strip;
        shadowCasters.Clear();
        shadowCasters.AddRange(casters);
        shadowBrush = new TextureBrush(shadowBitmap, WrapMode.Clamp);
        shadowBrush.TranslateTransform(strip.Left, strip.Top);
        return shadowBrush;
    }

    static bool SameRectangles(List<Rectangle> first, List<Rectangle> second)
    {
        if (first.Count != second.Count) return false;
        for (int i = 0; i < first.Count; i++)
            if (first[i] != second[i]) return false;
        return true;
    }

    void DrawBank(Snowbank bank, List<Rectangle> windowBounds = null)
    {
        if (!bank.Occupied) return;
        var edge = bank.Edge;
        var heights = bank.Heights;
        // Seat the drift inside the visible title-bar edge so the antialiased rim has contact.
        int contact = bank.WindowLedge ? 2 : 0;
        int baseY = edge.Top + contact;
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
                ridge.Add(new PointF(edge.Left + start * BinWidth, baseY));
                while (i < end && heights[i] >= .20f)
                {
                    float previous = i > 0 ? heights[i - 1] : 0;
                    float next = i + 1 < heights.Length ? heights[i + 1] : 0;
                    float rounded = (previous + heights[i] * 2 + next) / 4;
                    ridge.Add(new PointF(edge.Left + i * BinWidth + 2.5f, edge.Top - rounded));
                    i++;
                }
                ridge.Add(new PointF(Math.Min(edge.Right, edge.Left + i * BinWidth), baseY));
                path.StartFigure();
                path.AddCurve(ridge.ToArray(), .38f);
                path.CloseFigure();
            }
            if (path.PointCount == 0) return;
            var state = canvas.Save();
            foreach (var cover in bank.Covered) canvas.ExcludeClip(cover);
            using (var fill = new LinearGradientBrush(
                new Rectangle(0, edge.Top - 24, 1, 25 + contact), Color.White,
                Color.FromArgb(231, 208, 224, 237), LinearGradientMode.Vertical))
            using (var rim = new Pen(Color.FromArgb(78, 116, 149, 173), .72f))
            {
                canvas.FillPath(fill, path);
                canvas.DrawPath(rim, path);
                var shadow = bank.Taskbar ? TaskbarShadow(edge, windowBounds) : null;
                if (shadow != null) canvas.FillPath(shadow, path);
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
        if (clump.Delay > 0)
        {
            clump.Delay -= dt;
            if (clump.Delay > 0) return true;
            dt = -clump.Delay;
            clump.Delay = 0;
        }
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

    void ReadWindowsInFront()
    {
        windowsInFront.Clear();
        Native.EnumWindows((handle, data) =>
        {
            if (handle == Handle) return false;
            windowsInFront.Add(handle);
            return true;
        }, IntPtr.Zero);
    }

    void RestoreOverlayOrder()
    {
        Native.SetWindowPos(Handle, Native.TopmostWindow, 0, 0, 0, 0, Native.KeepGeometryAndFocus);
        popupAdjustedOrder = false;
    }

    void UpdatePopupOrder(List<Ledge> ledges)
    {
        bool hasPopup = false;
        IntPtr lowestTopmostPopup = IntPtr.Zero;
        foreach (var ledge in ledges)
        {
            if (!ledge.Transient || !ledge.Bounds.IntersectsWith(ScreenBounds)) continue;
            hasPopup = true;
            if (ledge.TopmostPopup) lowestTopmostPopup = ledge.Handle;
        }

        if (lowestTopmostPopup == IntPtr.Zero && popupAdjustedOrder) RestoreOverlayOrder();
        if (!hasPopup)
        {
            windowsInFront.Clear();
            return;
        }

        // Some shell surfaces already render above the overlay irrespective of WS_EX_TOPMOST.
        ReadWindowsInFront();
        if (lowestTopmostPopup == IntPtr.Zero || windowsInFront.Contains(lowestTopmostPopup)) return;
        if (!Native.IsWindowVisible(lowestTopmostPopup)
            || (Native.GetWindowLong(lowestTopmostPopup, Native.ExtendedStyle) & Native.TopmostStyle) == 0
            || !Native.SetWindowPos(Handle, lowestTopmostPopup, 0, 0, 0, 0, Native.KeepGeometryAndFocus))
            return;

        ReadWindowsInFront();
        if (windowsInFront.Contains(lowestTopmostPopup)
            && (Native.GetWindowLong(Handle, Native.ExtendedStyle) & Native.TopmostStyle) != 0)
        {
            popupAdjustedOrder = true;
            return;
        }

        // Some protected surfaces do not accept normal Z-order placement.
        RestoreOverlayOrder();
        ReadWindowsInFront();
    }

    void BeginFrame(Rectangle settingsBounds, List<Ledge> ledges = null)
    {
        canvas.ResetClip();
        canvas.Clear(Color.Transparent);
        ExcludeScreenBounds(settingsBounds);
        if (ledges == null) return;
        foreach (var ledge in ledges)
        {
            if (!ledge.Transient || windowsInFront.Contains(ledge.Handle)) continue;
            ExcludeScreenBounds(ledge.ClipBounds, ledge.RoundedPopup);
        }
    }

    void ExcludeScreenBounds(Rectangle bounds, bool rounded = false)
    {
        if (!bounds.IntersectsWith(ScreenBounds)) return;
        bounds.Offset(-ScreenBounds.Left, -ScreenBounds.Top);
        int diameter = rounded ? Math.Min(12, Math.Min(bounds.Width, bounds.Height)) : 0;
        if (diameter < 2)
        {
            canvas.ExcludeClip(bounds);
            return;
        }

        using (var shape = new GraphicsPath())
        {
            shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            shape.CloseFigure();
            canvas.SetClip(shape, CombineMode.Exclude);
        }
    }

    public void VerifyWindowAttachment()
    {
        var window = new IntPtr(3);
        var edge = new Rectangle(14, 35, 90, 1);
        TrackBank(window, edge, null, true, 0);
        var bank = banks.Find(item => item.Handle == window);
        bank.Occupied = true;
        for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 12;
        BeginFrame(Rectangle.Empty);
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(52, 36).A == 0 || bitmap.GetPixel(52, 39).A != 0)
            throw new InvalidOperationException("The snowbank does not meet the title bar.");

        edge.Offset(5, 19);
        TrackBank(window, edge, null, true, .02f);
        BeginFrame(Rectangle.Empty);
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(52, 36).A != 0 || bitmap.GetPixel(57, 55).A == 0 || bank.Heights[6] != 12)
            throw new InvalidOperationException("The snowbank lost contact or changed shape during a steady drag.");
        banks.Clear();
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

    public void VerifyTaskbarClip()
    {
        var taskbar = new IntPtr(1);
        var ledges = new List<Ledge>
        {
            new Ledge(taskbar, new Rectangle(ScreenBounds.Left, ScreenBounds.Top + 60, 80, 20), false, false),
            new Ledge(new IntPtr(2), new Rectangle(ScreenBounds.Left + 20, ScreenBounds.Top + 20, 28, 50), true, true)
        };
        SyncBanks(ledges, CollectWindowBounds(ledges), true, 0);
        var bank = banks.Find(item => item.Handle == taskbar);
        bank.Occupied = true;
        for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 12;
        BeginFrame(Rectangle.Empty);
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(30, 54).A != 0 || bitmap.GetPixel(12, 54).A == 0 || bitmap.GetPixel(60, 54).A == 0)
            throw new InvalidOperationException("A window did not obscure only the overlapping taskbar snow.");

        ledges.RemoveAt(1);
        SyncBanks(ledges, CollectWindowBounds(ledges), true, .1f);
        BeginFrame(Rectangle.Empty);
        DrawBank(bank);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(30, 54).A == 0)
            throw new InvalidOperationException("Taskbar snow did not reappear after the window moved away.");
        banks.Clear();
    }

    public void VerifyTaskbarShadow()
    {
        var bank = new Snowbank(new Rectangle(0, 72, 100, 1)) { Occupied = true, Taskbar = true };
        for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 16;
        var windows = new List<Rectangle> { new Rectangle(16, 16, 30, 42) };
        BeginFrame(Rectangle.Empty);
        DrawBank(bank, windows);
        canvas.Flush(FlushIntention.Sync);
        Color near = bitmap.GetPixel(32, 64);
        Color corner = bitmap.GetPixel(58, 64);
        Color distant = bitmap.GetPixel(84, 64);
        if (near.R >= corner.R - 4 || corner.R >= distant.R - 4 || bitmap.GetPixel(32, 50).A != 0)
            throw new InvalidOperationException("The taskbar shadow did not fade within the snowbank.");

        windows[0] = new Rectangle(60, 16, 30, 42);
        BeginFrame(Rectangle.Empty);
        DrawBank(bank, windows);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(78, 64).R >= bitmap.GetPixel(26, 64).R - 10)
            throw new InvalidOperationException("The taskbar shadow did not follow the moving window.");

        bank.Taskbar = false;
        BeginFrame(Rectangle.Empty);
        DrawBank(bank, windows);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(78, 64).R != distant.R)
            throw new InvalidOperationException("The shadow darkened snow outside the taskbar.");
    }

    public void VerifyPopupClip()
    {
        var ledges = new List<Ledge>
        {
            new Ledge(new IntPtr(1), new Rectangle(ScreenBounds.Left, ScreenBounds.Top + 70, 80, 20), false, false),
            new Ledge(new IntPtr(2), new Rectangle(ScreenBounds.Left + 22, ScreenBounds.Top + 34, 24, 44), false, true, true, true)
        };
        var windows = CollectWindowBounds(ledges);
        SyncBanks(ledges, windows, true, 0);
        var taskbar = banks.Find(bank => bank.Taskbar);
        if (windows.Count != 0 || taskbar == null || banks.Exists(bank => bank.Handle == ledges[1].Handle))
            throw new InvalidOperationException("A tray popup created a snowbank or cast a window shadow.");
        taskbar.Occupied = true;
        for (int i = 0; i < taskbar.Heights.Length; i++) taskbar.Heights[i] = 12;

        BeginFrame(Rectangle.Empty, ledges);
        DrawBank(taskbar, windows);
        canvas.Flush(FlushIntention.Sync);
        if (!canvas.IsVisible(22, 34) || canvas.IsVisible(34, 34)
            || bitmap.GetPixel(32, 64).A != 0 || bitmap.GetPixel(18, 64).A == 0)
            throw new InvalidOperationException("A tray popup did not hide only the snow behind it.");

        ledges.RemoveAt(1);
        BeginFrame(Rectangle.Empty, ledges);
        DrawBank(taskbar, windows);
        canvas.Flush(FlushIntention.Sync);
        if (bitmap.GetPixel(32, 64).A == 0)
            throw new InvalidOperationException("Snow did not reappear when the tray popup closed.");
        banks.Clear();
    }

    public void VerifyTrayTooltipClip()
    {
        var tray = new Rectangle(ScreenBounds.Left, ScreenBounds.Top + 144, 300, 40);
        var cursor = new Point(tray.Left + 70, tray.Top + 22);
        var labels = new[]
        {
            new Rectangle(tray.Left + 18, tray.Top - 43, 104, 39),
            new Rectangle(tray.Left + 18, tray.Top - 65, 145, 61),
            new Rectangle(tray.Left + 18, tray.Top - 65, 210, 61),
            new Rectangle(tray.Left + 18, tray.Top - 113, 128, 109)
        };
        var bank = new Snowbank(new Rectangle(0, 144, 240, 1)) { Occupied = true };
        for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 20;
        foreach (var bounds in labels)
        {
            var body = SnowApp.TrayTooltipBody(bounds, tray, 96, false, cursor);
            if (body.Bottom != bounds.Bottom - 8)
                throw new InvalidOperationException("The tray label lost its expected clip bounds.");

            var ledges = new List<Ledge>
            {
                new Ledge(new IntPtr(2), bounds, false, true, true, true, false, body)
            };
            BeginFrame(Rectangle.Empty, ledges);
            DrawBank(bank);
            canvas.Flush(FlushIntention.Sync);
            if (bitmap.GetPixel(54, 129).A != 0 || bitmap.GetPixel(54, 137).A == 0)
                throw new InvalidOperationException("The tray label should hide the drift and keep the snow below visible.");
        }

        var tall = labels[labels.Length - 1];
        var elsewhere = new Point(tray.Right - 1, cursor.Y);
        if (SnowApp.TrayTooltipBody(tall, tray, 96, false, elsewhere) != tall
            || SnowApp.TrayTooltipBody(tall, tray, 96, false, null) != tall
            || SnowApp.TrayTooltipBody(tall, tray, 96, true, null).Bottom != tall.Bottom - 8)
            throw new InvalidOperationException("The taller label was not distinguished from other popups.");

        var scaled = new Rectangle(-1200, 44, 219, 90);
        var scaledTray = new Rectangle(-1500, 140, 1000, 60);
        if (SnowApp.TrayTooltipBody(scaled, scaledTray, 144, false, null).Bottom != scaled.Bottom - 12)
            throw new InvalidOperationException("Tooltip trimming did not follow display scaling.");
        var flyout = new Rectangle(tray.Left + 18, tray.Top - 224, 186, 220);
        if (SnowApp.TrayTooltipBody(flyout, tray, 96, false, cursor) != flyout)
            throw new InvalidOperationException("A larger flyout was treated as a tray label.");
    }

    public void VerifyNativePopupOrder()
    {
        var popup = new NativeWindow();
        popup.CreateHandle(new CreateParams
        {
            X = -32000, Y = -32000, Width = 80, Height = 32,
            Style = unchecked((int)0x80000000),
            ExStyle = Native.TopmostStyle | 0x08000080
        });
        try
        {
            Native.ShowWindow(popup.Handle, 4);
            Native.SetWindowPos(popup.Handle, Native.TopmostWindow, 0, 0, 0, 0, Native.KeepGeometryAndFocus);
            var bounds = new Rectangle(ScreenBounds.Left, ScreenBounds.Top, 80, 32);
            var ledges = new List<Ledge> { new Ledge(popup.Handle, bounds, false, true, true, true) };
            UpdatePopupOrder(ledges);
            BeginFrame(Rectangle.Empty, ledges);
            if (!canvas.IsVisible(40, 16) || popupAdjustedOrder)
                throw new InvalidOperationException("A popup already in front was unnecessarily cut out.");

            RestoreOverlayOrder();
            UpdatePopupOrder(ledges);
            BeginFrame(Rectangle.Empty, ledges);
            if (canvas.IsVisible(40, 16))
                throw new InvalidOperationException("An obscured popup did not fall back to clipping.");

            ledges[0] = new Ledge(popup.Handle, bounds, false, true, true, true, true);
            UpdatePopupOrder(ledges);
            BeginFrame(Rectangle.Empty, ledges);
            if (!popupAdjustedOrder || !canvas.IsVisible(40, 16))
                throw new InvalidOperationException("The overlay did not move behind the topmost popup.");

            ledges.Clear();
            UpdatePopupOrder(ledges);
            if (popupAdjustedOrder || windowsInFront.Count != 0)
                throw new InvalidOperationException("The overlay did not restore its position after the popup closed.");
        }
        finally { popup.DestroyHandle(); }
    }

    public void Frame(float dt, float time, SnowSettings settings, List<Ledge> ledges, bool falling,
        Rectangle settingsBounds, IntPtr movingWindow)
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
        var windowBounds = CollectWindowBounds(ledges);
        if (!sweeping || !testSweep) SyncBanks(ledges, windowBounds, settings.Settle, time);
        UpdatePopupOrder(ledges);
        BeginFrame(settingsBounds, ledges);
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
        Snowbank dragBank = null;
        if (failedDragTarget != movingWindow) failedDragTarget = IntPtr.Zero;
        if (!sweeping && movingWindow != IntPtr.Zero && movingWindow != failedDragTarget && settingsBounds.IsEmpty
            && !ledges.Exists(ledge => ledge.Transient && ledge.Bounds.IntersectsWith(ScreenBounds)))
            dragBank = banks.Find(bank => bank.Handle == movingWindow && bank.Occupied && bank.Covered.Count == 0);
        if (dragBank == null) HideDragStrip();
        foreach (var bank in banks)
            if (bank != dragBank) DrawBank(bank, windowBounds);
        if (activeTrack != null) DrawPlow(activeTrack);
        for (int i = loose.Count - 1; i >= 0; i--)
        {
            var clump = loose[i];
            if (!AdvanceClump(clump, dt, breeze, !sweeping && settings.Settle))
            {
                loose.RemoveAt(i);
                continue;
            }
            if (clump.Delay > 0) continue;
            canvas.FillEllipse(ice, clump.X, clump.Y, clump.Radius + 1, clump.Radius);
            canvas.FillEllipse(snow, clump.X, clump.Y - .6f, clump.Radius, clump.Radius * .77f);
        }
        DrawAnimals(dt, falling && !sweeping && settings.Animals, windowBounds);
        PublishOverlay();
        if (dragBank == null) return;
        if (!PublishDragStrip(dragBank))
        {
            failedDragTarget = dragBank.Handle;
            HideDragStrip();
        }
    }

    void PublishOverlay()
    {
        canvas.Flush(FlushIntention.Sync);
        var dst = position;
        var extent = size;
        var src = new Native.Point(0, 0);
        var blend = new Native.Blend { Alpha = 255, Format = 1 };
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref extent, dc, ref src, 0, ref blend, 2))
            throw new System.ComponentModel.Win32Exception();
    }

    bool PublishDragStrip(Snowbank bank)
    {
        var edge = bank.Edge;
        var area = Rectangle.Intersect(new Rectangle(0, 0, size.Width, size.Height),
            new Rectangle(edge.Left, edge.Top - 33, edge.Width, DragLayer.StripHeight));
        if (area.Width <= 0 || area.Height <= 0) return false;
        if (dragStrip == null) dragStrip = new DragLayer(size, position, Handle);

        // The full frame is already on screen. Reuse its DIB as scratch space
        // and send only the narrow snow texture to the drag layer's UI thread.
        var state = canvas.Save();
        try
        {
            var scratch = new Rectangle(0, 0, area.Width, area.Height);
            canvas.SetClip(scratch, CombineMode.Replace);
            canvas.CompositingMode = CompositingMode.SourceCopy;
            using (var transparent = new SolidBrush(Color.Transparent)) canvas.FillRectangle(transparent, scratch);
            canvas.CompositingMode = CompositingMode.SourceOver;
            canvas.TranslateTransform(-area.X, -area.Y);
            DrawBank(bank);
            canvas.Flush(FlushIntention.Sync);
            var frame = new Rectangle(ScreenBounds.Left + edge.Left, ScreenBounds.Top + edge.Top, edge.Width, 1);
            return dragStrip.Publish(bank.Handle, frame, area, readDragInsets(bank.Handle), bitmapBits);
        }
        finally { canvas.Restore(state); }
    }

    void HideDragStrip()
    {
        if (dragStrip != null) dragStrip.Hide();
    }

    public void VerifyMinimizedSnow()
    {
        var target = new NativeWindow();
        target.CreateHandle(new CreateParams
        {
            X = -31700, Y = -31700, Width = 140, Height = 100,
            Style = unchecked((int)0x80c20000),
            ExStyle = 0x08000080
        });
        try
        {
            Native.ShowWindow(target.Handle, 4);
            var edge = new Rectangle(18, 65, 140, 1);
            TrackBank(target.Handle, edge, null, true, 0);
            var bank = banks.Find(item => item.Handle == target.Handle);
            bank.Occupied = true;
            for (int i = 0; i < bank.Heights.Length; i++) bank.Heights[i] = 12;
            Native.ShowWindow(target.Handle, 7);
            if (!Native.IsIconic(target.Handle)) throw new InvalidOperationException("The test window did not minimize.");
            SyncBanks(new List<Ledge>(), new List<Rectangle>(), true, .05f);
            if (banks.Contains(bank) || Array.Exists(bank.Heights, height => height != 0)
                || loose.Count < 2 || !loose.Exists(clump => clump.Delay > 0))
                throw new InvalidOperationException("Minimizing the window did not release its accumulated snow.");

            int released = loose.Count;
            DropMinimizedSnow(target.Handle, .06f);
            if (loose.Count != released)
                throw new InvalidOperationException("Snow was released twice for one minimization.");
            var delayed = loose.Find(clump => clump.Delay > 0);
            float startY = delayed.Y;
            if (!AdvanceClump(delayed, delayed.Delay * .5f, 0, false) || delayed.Y != startY)
                throw new InvalidOperationException("A delayed fragment moved before its release.");
            if (!AdvanceClump(delayed, .25f, 0, false) || delayed.Y <= startY)
                throw new InvalidOperationException("The released snow did not fall from the window.");
        }
        finally
        {
            target.DestroyHandle();
            banks.Clear();
            loose.Clear();
        }
    }

    public void VerifyDragStrip()
    {
        var target = new NativeWindow();
        target.CreateHandle(new CreateParams
        {
            X = -31700, Y = -31700, Width = 90, Height = 120,
            Style = unchecked((int)0x80000000),
            ExStyle = 0x08000080
        });
        using (var layer = new DragLayer(size, position, Handle))
        try
        {
            Native.ShowWindow(target.Handle, 4);
            Native.Rect original;
            if (!Native.GetWindowRect(target.Handle, out original))
                throw new InvalidOperationException("The test window was not created.");
            var frame = original.ToRectangle();
            var pixels = new Rectangle(14, 12, 90, 38);
            BeginFrame(Rectangle.Empty);
            canvas.FillRectangle(snow, 0, 18, pixels.Width, 13);
            canvas.Flush(FlushIntention.Sync);
            if (!layer.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
                || !layer.WaitForPosition(position.X + 14, position.Y + 12, 90))
                throw new InvalidOperationException("The independent drag layer could not present a frame.");

            Native.SetWindowPos(target.Handle, IntPtr.Zero, frame.X + 11, frame.Y + 23,
                0, 0, Native.MoveWithoutActivation);
            // This thread is held here deliberately. The drag layer must follow without a snowfall tick.
            if (!layer.WaitForPosition(position.X + 25, position.Y + 35, 90))
                throw new InvalidOperationException("The drag layer waited for the snowfall thread.");
            pixels.Width = 83;
            if (!layer.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
                || !layer.WaitForPosition(position.X + 25, position.Y + 35, 83))
                throw new InvalidOperationException("A repaint moved the independent drag layer backward.");

            Native.SetWindowPos(target.Handle, IntPtr.Zero, frame.X - 30, frame.Y + 9,
                0, 0, Native.MoveWithoutActivation);
            if (!layer.WaitForPosition(position.X, position.Y + 21, 67))
                throw new InvalidOperationException("The moving drag layer was not clipped to the display.");
        }
        finally { target.DestroyHandle(); }
    }

    public void Dispose()
    {
        if (dragStrip != null) dragStrip.Dispose();
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
        if (shadowBrush != null) shadowBrush.Dispose();
        if (shadowBitmap != null) shadowBitmap.Dispose();
        foreach (var sprite in sprites) if (sprite != null) sprite.Dispose();
        foreach (var shadow in shadows) if (shadow != null) shadow.Dispose();
        if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
        if (dib != IntPtr.Zero) Native.DeleteObject(dib);
        if (dc != IntPtr.Zero) Native.DeleteDC(dc);
    }
}

sealed class DragLayer : NativeWindow, IDisposable
{
    const int FrameMessage = 0x8001;
    const int CloseMessage = 0x0010;
    internal const int StripHeight = 38;
    readonly object gate = new object();
    readonly System.Threading.ManualResetEventSlim ready = new System.Threading.ManualResetEventSlim();
    readonly System.Threading.Thread thread;
    readonly Native.Size display;
    readonly Native.Point origin;
    readonly IntPtr overlay;
    readonly int[] transfer;
    readonly Native.WindowEvent moved;
    volatile bool failed, stopping;
    IntPtr dc, dib, previous, bits, moveHook;
    Timer poll;
    Tile pending, current;
    bool queued, shown;
    Rectangle lastPixels, lastSource;

    struct Tile
    {
        public IntPtr Target;
        public Rectangle Frame, Area;
        public Padding? Insets;
    }

    public DragLayer(Native.Size display, Native.Point origin, IntPtr overlay)
    {
        this.display = display;
        this.origin = origin;
        this.overlay = overlay;
        transfer = new int[display.Width * StripHeight];
        moved = OnWindowMoved;
        thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "Desktop Snow window tracking" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        ready.Wait(1000);
    }

    void Run()
    {
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "Desktop Snow Drag Layer",
                X = origin.X, Y = origin.Y,
                Style = unchecked((int)0x80000000),
                ExStyle = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | Native.TopmostStyle
            });
            dc = Native.CreateCompatibleDC(IntPtr.Zero);
            var info = new Native.BitmapInfo
            {
                HeaderSize = 40, Width = display.Width, Height = -StripHeight, Planes = 1, Bits = 32
            };
            dib = Native.CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();
            previous = Native.SelectObject(dc, dib);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception();
            poll = new Timer { Interval = 15 };
            poll.Tick += (sender, args) => Present(false);
            ready.Set();
            if (!stopping) Application.Run();
        }
        catch (System.ComponentModel.Win32Exception) { failed = true; }
        finally
        {
            if (moveHook != IntPtr.Zero) Native.UnhookWinEvent(moveHook);
            if (poll != null) poll.Dispose();
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            if (Handle != IntPtr.Zero) DestroyHandle();
            ready.Set();
        }
    }

    public bool Publish(IntPtr target, Rectangle frame, Rectangle area, Padding? insets, IntPtr source)
    {
        if (!ready.IsSet || failed || stopping || Handle == IntPtr.Zero) return false;
        lock (gate)
        {
            Marshal.Copy(source, transfer, 0, display.Width * area.Height);
            pending = new Tile { Target = target, Frame = frame, Area = area, Insets = insets };
            return PostFrame();
        }
    }

    public void Hide()
    {
        if (!ready.IsSet || failed || stopping || Handle == IntPtr.Zero) return;
        lock (gate)
        {
            if (pending.Target == IntPtr.Zero) return;
            pending = new Tile();
            PostFrame();
        }
    }

    bool PostFrame()
    {
        if (queued) return true;
        queued = Native.PostMessage(Handle, FrameMessage, IntPtr.Zero, IntPtr.Zero);
        return queued;
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case FrameMessage:
                ReceiveFrame();
                return;
            case CloseMessage:
                Application.ExitThread();
                return;
            case 0x84:
                message.Result = new IntPtr(-1);
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    void ReceiveFrame()
    {
        IntPtr oldTarget = current.Target;
        lock (gate)
        {
            current = pending;
            queued = false;
            if (current.Target != IntPtr.Zero)
                Marshal.Copy(transfer, 0, bits, display.Width * current.Area.Height);
        }

        if (current.Target != oldTarget)
        {
            if (moveHook != IntPtr.Zero) Native.UnhookWinEvent(moveHook);
            moveHook = IntPtr.Zero;
            if (current.Target != IntPtr.Zero)
            {
                uint process;
                uint sourceThread = Native.GetWindowThreadProcessId(current.Target, out process);
                if (sourceThread != 0)
                    moveHook = Native.SetWinEventHook(Native.WindowLocationChanged, Native.WindowLocationChanged,
                        IntPtr.Zero, moved, process, sourceThread, 1); // Out-of-context, skip this thread.
            }
            poll.Enabled = current.Target != IntPtr.Zero;
        }
        Present(true);
    }

    void OnWindowMoved(IntPtr hook, uint eventId, IntPtr handle, int objectId, int childId, uint sourceThread, uint time)
    {
        if (hook == moveHook && handle == current.Target && objectId == 0 && childId == 0) Present(false);
    }

    public static Rectangle InsetFrame(Rectangle raw, Padding insets)
    {
        var frame = Rectangle.FromLTRB(raw.Left + insets.Left, raw.Top + insets.Top,
            raw.Right - insets.Right, raw.Bottom - insets.Bottom);
        return frame.Width > 0 && frame.Height > 0 ? frame : Rectangle.Empty;
    }

    void Present(bool repaint)
    {
        if (failed || stopping) return;
        Native.Rect raw;
        if (current.Target == IntPtr.Zero || !Native.IsWindowVisible(current.Target)
            || !Native.GetWindowRect(current.Target, out raw))
        {
            SetHidden();
            return;
        }

        var frame = raw.ToRectangle();
        if (current.Insets.HasValue)
            frame = InsetFrame(frame, current.Insets.Value);
        else
        {
            Native.Rect visible;
            if (Native.DwmGetWindowAttribute(current.Target, 9, out visible, 16) == 0)
                frame = visible.ToRectangle();
        }
        if (frame.IsEmpty)
        {
            SetHidden();
            return;
        }
        var destination = current.Area;
        destination.Offset(frame.X - current.Frame.X, frame.Y - current.Frame.Y);
        // The source image describes the previous width during a resize. Crop
        // only the part no longer over the live window and refresh next frame.
        var monitor = new Rectangle(0, 0, display.Width, display.Height);
        if (frame.Width < current.Frame.Width)
        {
            int clippedLeft = Math.Max(0, origin.X - current.Frame.Left);
            int liveRight = destination.Left - clippedLeft + frame.Width;
            monitor.Width = Math.Max(0, Math.Min(monitor.Width, liveRight));
        }
        var pixels = Rectangle.Intersect(destination, monitor);
        if (pixels.Width <= 0 || pixels.Height <= 0)
        {
            SetHidden();
            return;
        }
        var source = new Rectangle(pixels.X - destination.X, pixels.Y - destination.Y, pixels.Width, pixels.Height);
        if (!repaint && shown && source == lastSource)
        {
            if (pixels != lastPixels && Native.SetWindowPos(Handle, IntPtr.Zero,
                origin.X + pixels.X, origin.Y + pixels.Y, 0, 0, Native.MoveWithoutActivation))
                lastPixels = pixels;
            return;
        }

        var dst = new Native.Point(origin.X + pixels.X, origin.Y + pixels.Y);
        var extent = new Native.Size(pixels.Width, pixels.Height);
        var src = new Native.Point(source.X, source.Y);
        var blend = new Native.Blend { Alpha = 255, Format = 1 };
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref extent, dc, ref src, 0, ref blend, 2))
        {
            SetHidden();
            poll.Stop();
            failed = true;
            return;
        }
        if (!shown)
        {
            Native.ShowWindow(Handle, 4);
            Native.SetWindowPos(Handle, overlay, 0, 0, 0, 0, Native.KeepGeometryAndFocus);
            shown = true;
        }
        lastPixels = pixels;
        lastSource = source;
    }

    void SetHidden()
    {
        if (!shown) return;
        Native.ShowWindow(Handle, 0);
        shown = false;
    }

    internal bool WaitForPosition(int x, int y, int width)
    {
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 700 && !failed)
        {
            Native.Rect rect;
            if (Handle != IntPtr.Zero && Native.IsWindowVisible(Handle) && Native.GetWindowRect(Handle, out rect)
                && rect.Left == x && rect.Top == y && rect.Right - rect.Left == width) return true;
            System.Threading.Thread.Sleep(5);
        }
        return false;
    }

    public void Dispose()
    {
        stopping = true;
        if (Handle != IntPtr.Zero) Native.PostMessage(Handle, CloseMessage, IntPtr.Zero, IntPtr.Zero);
        if (thread.Join(750)) ready.Dispose();
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    public const uint WindowMinimizeStart = 0x0016;
    public const uint WindowLocationChanged = 0x800b;
    public const uint MoveWithoutActivation = 0x0215;
    public delegate void WindowEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint first, uint last,
        IntPtr module, WindowEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const int ExtendedStyle = -20;
    public const int TopmostStyle = 0x00000008;
    public const uint KeepGeometryAndFocus = 0x0213;
    public static readonly IntPtr TopmostWindow = new IntPtr(-1);
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
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretBounds;
    }

    static readonly uint GuiThreadInfoSize = (uint)Marshal.SizeOf(typeof(GuiThreadInfo));

    public static IntPtr GetMovingWindow()
    {
        const uint InMoveSize = 2;
        var state = new GuiThreadInfo { Size = GuiThreadInfoSize };
        return GetGUIThreadInfo(0, ref state) && (state.Flags & InMoveSize) != 0
            ? state.MoveSize : IntPtr.Zero;
    }

    public static uint WindowDpi(IntPtr window)
    {
        try
        {
            uint dpi = GetDpiForWindow(window);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException) { return 96; }
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo state);
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

    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr window, int index);
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

















