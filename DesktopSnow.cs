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
#if SMOKE_TEST
        bool smoke = Array.IndexOf(args, "--smoke-test") >= 0;
#else
        bool smoke = false;
#endif
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

sealed partial class SnowApp : ApplicationContext
{
    readonly List<SnowWindow> windows = new List<SnowWindow>();
    readonly List<DisplayArea> animalDisplays = new List<DisplayArea>();
    readonly SnowAnimalScene animals = new SnowAnimalScene();
    readonly Dictionary<IntPtr, WindowSnowDepth> windowSnow = new Dictionary<IntPtr, WindowSnowDepth>();
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
    readonly uint ownProcess = Native.GetCurrentProcessId();
    readonly SnowSettings settings;
    readonly ClearShortcut shortcut;
    SnowSettingsDialog settingsDialog;
    bool clearQueued;
#if SMOKE_TEST
    int frames;
#endif
    bool paused;
    IntPtr movingWindow, attachmentWindow, frameInsetOwner;
    Padding frameInsets;
    uint frameInsetDpi;
    readonly ToolStripMenuItem clear = new ToolStripMenuItem("Clear snow");

    public SnowApp(bool smoke)
    {
        this.smoke = smoke;
        settings = smoke ? new SnowSettings() : SnowSettings.Load();
        if (!smoke) shortcut = new ClearShortcut(StartSweep);
#if SMOKE_TEST
        else VerifyConfiguration();
#endif
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
        UpdateVisibility();
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

    static Rectangle GetForegroundFrame()
    {
        IntPtr active = Native.GetForegroundWindow();
        if (active == IntPtr.Zero || active == Native.GetShellWindow() || Native.IsIconic(active)) return Rectangle.Empty;
        var name = new System.Text.StringBuilder(64);
        Native.GetClassName(active, name, name.Capacity);
        switch (name.ToString())
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
                return Rectangle.Empty;
        }

        Native.Rect rect;
        if (Native.DwmGetWindowAttribute(active, 9, out rect, 16) != 0
            && !Native.GetWindowRect(active, out rect)) return Rectangle.Empty;
        return rect.ToRectangle();
    }

    static bool CoversScreen(Rectangle frame, Rectangle screen)
    {
        return !frame.IsEmpty && frame.Left <= screen.Left + 2 && frame.Top <= screen.Top + 2
            && frame.Right >= screen.Right - 2 && frame.Bottom >= screen.Bottom - 2;
    }

    void Toggle()
    {
        paused = !paused;
        pause.Text = paused ? "Resume snowfall" : "Pause snowfall";
        UpdateVisibility();
    }

    bool AnyVisibleSweep()
    {
        return windows.Exists(window => !window.FullscreenPaused && !window.DisplayLost && window.IsSweeping);
    }

    void StartSweep()
    {
        if (settingsDialog != null || AnyVisibleSweep()) return;
        if (!windows.Exists(window => !window.FullscreenPaused && !window.DisplayLost))
        {
            clearQueued = true;
            return;
        }
        foreach (var window in windows)
            if (!window.IsSweeping) window.BeginSweep(smoke);
        UpdateVisibility();
    }

    bool UpdateVisibility()
    {
        bool visible = false;
        foreach (var window in windows)
        {
            bool show = !window.DisplayLost && !window.FullscreenPaused && (!paused || window.IsSweeping);
            window.SetVisible(show);
            visible |= show;
        }
        clear.Enabled = !AnyVisibleSweep();
        if (!visible && timer.Interval != 250) timer.Interval = 250;
        return visible;
    }

    void SyncScreens()
    {
        var displays = Native.ReadDisplays();
        if (displays.Count == 0)
        {
            // A display driver can report no monitors briefly during a topology change.
            if (windows.Count != 0) return;
            foreach (var screen in Screen.AllScreens)
                displays.Add(new DisplayArea(screen.Bounds, screen.WorkingArea));
        }

        for (int i = windows.Count - 1; i >= 0; i--)
        {
            var window = windows[i];
            Rectangle bounds = window.ScreenBounds;
            if (!window.DisplayLost && displays.Exists(display => display.Bounds == bounds)) continue;
            window.Dispose();
            windows.RemoveAt(i);
        }

        foreach (var display in displays)
        {
            var window = windows.Find(candidate => candidate.ScreenBounds == display.Bounds);
            if (window != null)
            {
                window.Display = display;
                continue;
            }
            try { windows.Add(new SnowWindow(display, smoke, ReadDragInsets, windowSnow)); }
            catch (System.ComponentModel.Win32Exception error)
            {
                if (smoke) throw;
                Debug.WriteLine(error); // Retry this monitor without interrupting the remaining displays.
            }
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
        var fullscreen = !smoke && settings.AutoPause ? GetForegroundFrame() : Rectangle.Empty;
        foreach (var window in windows) window.FullscreenPaused = CoversScreen(fullscreen, window.ScreenBounds);
        bool visible = UpdateVisibility();
        if (clearQueued && windows.Exists(window => !window.FullscreenPaused && !window.DisplayLost))
        {
            clearQueued = false;
            StartSweep();
            visible = UpdateVisibility();
        }
        if (!visible) return;
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
#if SMOKE_TEST
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
            }
        }
        if (smoke && frames == 3)
        {
            foreach (var window in windows) window.SeedTestPile();
            StartSweep();
        }
#endif
        ReleaseMinimizingWindows(ledges, (float)now);
        if (windowSnow.Count != 0)
        {
            // Keep drifts for live windows even if their top edge is temporarily off a display.
            var live = new HashSet<IntPtr>();
            if (settings.Settle)
                foreach (var ledge in ledges)
                    if (ledge.Moves && ledge.Collects) live.Add(ledge.Handle);
            var stale = new List<IntPtr>();
            foreach (var handle in windowSnow.Keys)
                if (!live.Contains(handle)) stale.Add(handle);
            foreach (var handle in stale) windowSnow.Remove(handle);
        }
        Rectangle settingsBounds = GetSettingsBounds();
        animalDisplays.Clear();
        if (settings.Animals && !paused)
            foreach (var window in windows)
                if (window.SceneVisible && !window.IsSweeping) animalDisplays.Add(window.Display);
        animals.Advance(dt, animalDisplays);
#if SMOKE_TEST
        if (smoke && frames < SnowWeather.Presets.Length && animalDisplays.Count != 0)
        {
            var display = animalDisplays[0];
            animals.TestAt(frames, display.Bounds.Left + display.Bounds.Width / 2, display.Ground);
        }

#endif
        foreach (var window in windows)
        {
            if (!window.SceneVisible) continue;
            try
            {
                window.Frame(dt, (float)now, settings, ledges, !paused, settingsBounds, movingWindow, animals);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                if (smoke) throw;
                Debug.WriteLine(error);
                window.DisplayLost = true;
                continue;
            }
            if (window.IsSweeping && window.SweepFinished)
            {
#if SMOKE_TEST
                if (smoke && window.HasDeepSnow) throw new InvalidOperationException("The sweep left snow behind.");
#endif
                window.EndSweep();
            }
        }
        UpdateVisibility();
#if SMOKE_TEST
        if (smoke && ++frames >= 24)
        {
            if (AnyVisibleSweep()) throw new InvalidOperationException("The sweep did not finish.");
            ExitThread();
        }
#endif
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
            var monitor = windows.Find(window => window.ScreenBounds.Contains(
                bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            if (monitor != null && monitor.Display.WorkArea.Bottom < monitor.ScreenBounds.Bottom)
            {
                uint dpi = Native.WindowDpi(handle);
                var tray = Rectangle.FromLTRB(monitor.ScreenBounds.Left, monitor.Display.WorkArea.Bottom,
                    monitor.ScreenBounds.Right, monitor.ScreenBounds.Bottom);
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

internal sealed class DisplayArea
{
    public readonly Rectangle Bounds, WorkArea;
    public DisplayArea(Rectangle bounds, Rectangle workArea)
    {
        Bounds = bounds;
        WorkArea = Rectangle.Intersect(bounds, workArea);
        if (WorkArea.Width <= 0 || WorkArea.Height <= 0) WorkArea = bounds;
    }

    public int Ground { get { return Math.Min(Bounds.Bottom - 1, WorkArea.Bottom); } }
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

sealed class WindowSnowDepth
{
    public float[] Heights;
    public bool Occupied;

    public WindowSnowDepth(int count) { Heights = new float[count]; }
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
        public readonly WindowSnowDepth Depth;
        public float[] Heights { get { return Depth.Heights; } }
        public bool Occupied { get { return Depth.Occupied; } set { Depth.Occupied = value; } }
        public readonly bool Shared;
        public IntPtr Handle;
        public bool Seen;
        public int Direction;
        public int Motion;
        public Point MotionOrigin;
        public float LastMove, LastShake = -10;
        public bool Taskbar, WindowLedge;
        public readonly List<Rectangle> Covered = new List<Rectangle>();
        public readonly List<SnowBreak> Breaks = new List<SnowBreak>();

        public Snowbank(Rectangle edge, WindowSnowDepth sharedDepth = null)
        {
            Edge = edge;
            MotionOrigin = edge.Location;
            Shared = sharedDepth != null;
            Depth = sharedDepth ?? new WindowSnowDepth((edge.Width + BinWidth - 1) / BinWidth);
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

    public DisplayArea Display { get; set; }
    public Rectangle ScreenBounds { get { return Display.Bounds; } }
    public bool FullscreenPaused { get; set; }
    public bool DisplayLost { get; set; }
    public bool SceneVisible { get; private set; }
    public bool IsSweeping { get { return sweeping; } }
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
    readonly Dictionary<IntPtr, WindowSnowDepth> windowSnow;
    DragLayer dragStrip;
    IntPtr bitmapBits;
    long? dragRetryAt;

    public SnowWindow(DisplayArea display, bool smoke, Func<IntPtr, Padding?> readDragInsets,
        Dictionary<IntPtr, WindowSnowDepth> windowSnow = null)
    {
        this.readDragInsets = readDragInsets;
        this.windowSnow = windowSnow;
        Display = display;
        Rectangle bounds = display.Bounds;
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

    public void SetVisible(bool visible)
    {
        if (visible == SceneVisible) return;
        SceneVisible = visible;
        if (visible) Show();
        else Hide();
    }

    void Show()
    {
        Native.SetWindowPos(Handle, Native.TopmostWindow, 0, 0, 0, 0, Native.KeepGeometryAndFocus);
        popupAdjustedOrder = false;
        Native.ShowWindow(Handle, 4);
    }

    void Hide()
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

    public void Frame(float dt, float time, SnowSettings settings, List<Ledge> ledges, bool falling,
        Rectangle settingsBounds, IntPtr movingWindow, SnowAnimalScene animals)
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
        Snowbank dragBank = null;
        if (!sweeping && movingWindow != IntPtr.Zero && settingsBounds.IsEmpty
            && !ledges.Exists(ledge => ledge.Transient && ledge.Bounds.IntersectsWith(ScreenBounds)))
            dragBank = banks.Find(bank => bank.Handle == movingWindow && bank.Occupied && bank.Covered.Count == 0);
        if (dragBank != null && (!DragStripAvailable() || !PublishDragStrip(dragBank))) dragBank = null;
        if (dragBank == null) HideDragStrip();
        BeginFrame(settingsBounds, ledges);
        float wind = weather.Wind * settings.Wind / 100f;
        float deposit = weather.Deposit * settings.Accumulation / 100f;
        float breeze = (7 + 13 * (float)Math.Sin(time * .34f)) * wind;
        if (falling)
        {
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
        }
        var activeTrack = sweeping ? AdvancePlow(dt) : null;
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
        if (falling && !sweeping && settings.Animals) DrawAnimals(animals, windowBounds);
        PublishOverlay();
    }

    bool DragStripAvailable()
    {
        if (dragStrip != null && dragStrip.Failed)
        {
            dragStrip.Dispose();
            dragStrip = null;
            dragRetryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }
        if (dragStrip == null)
        {
            if (dragRetryAt.HasValue && Stopwatch.GetTimestamp() < dragRetryAt.Value) return false;
            dragRetryAt = null;
            dragStrip = new DragLayer(size, position, Handle);
        }
        return dragStrip.Ready;
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

        // Use the DIB as scratch space before drawing the main overlay. If the
        // worker rejects the frame, the main overlay can still draw this bank.
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
