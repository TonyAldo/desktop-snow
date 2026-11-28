// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed partial class SnowApp
{
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
        if (!CoversScreen(frame.ToRectangle(), screen)) throw new InvalidOperationException("A fullscreen app was missed.");
        frame.Bottom = 1032;
        if (CoversScreen(frame.ToRectangle(), screen)) throw new InvalidOperationException("A window above the taskbar was treated as fullscreen.");
        var rightScreen = new Rectangle(0, -180, 2560, 1440);
        if (!CoversScreen(rightScreen, rightScreen) || CoversScreen(rightScreen, screen))
            throw new InvalidOperationException("Fullscreen detection paused an adjacent monitor.");
        SnowAnimalScene.VerifyRoutes();
        SnowWindow.VerifyAdjacentAnimalFrames();
        SnowWindow.VerifyWindowDriftCrossesDisplays();
    }
}

sealed partial class SnowWindow
{
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

    internal static void VerifyWindowDriftCrossesDisplays()
    {
        var leftBounds = new Rectangle(-240, 0, 240, 280);
        var rightBounds = new Rectangle(0, 0, 260, 280);
        var registry = new Dictionary<IntPtr, WindowSnowDepth>();
        using (var left = new SnowWindow(new DisplayArea(leftBounds, leftBounds), true, handle => null, registry))
        using (var right = new SnowWindow(new DisplayArea(rightBounds, rightBounds), true, handle => null, registry))
        {
            var handle = new IntPtr(12);
            var ledges = new List<Ledge> { new Ledge(handle, new Rectangle(-190, 70, 125, 140), true, true) };
            var covers = new List<Rectangle>();
            left.SyncBanks(ledges, covers, true, 0);
            var initial = left.banks.Find(bank => bank.Handle == handle);
            initial.Heights[2] = 4;
            initial.Heights[19] = 13;
            initial.Occupied = true;
            var profile = initial.Heights;
            ledges[0] = new Ledge(handle, new Rectangle(-45, 70, 125, 140), true, true);
            left.SyncBanks(ledges, covers, true, .1f);
            right.SyncBanks(ledges, covers, true, .1f);
            var crossing = right.banks.Find(bank => bank.Handle == handle);
            if (crossing == null || !ReferenceEquals(initial.Depth, crossing.Depth) || crossing.Heights[19] != 13
                || left.BinAtBoundary(initial, leftBounds.Width) != right.BinAtBoundary(crossing, 0))
                throw new InvalidOperationException("The snow profile or bin ownership split at the display seam.");

            ledges[0] = new Ledge(handle, new Rectangle(22, 70, 125, 140), true, true);
            left.SyncBanks(ledges, covers, true, .2f);
            right.SyncBanks(ledges, covers, true, .2f);
            if (left.banks.Exists(bank => bank.Handle == handle) || !ReferenceEquals(crossing.Heights, profile)
                || crossing.Heights[2] != 4 || crossing.Heights[19] != 13)
                throw new InvalidOperationException("Moving the window fully onto a second display reset its drift.");

            ledges[0] = new Ledge(handle, new Rectangle(-190, 70, 125, 140), true, true);
            right.SyncBanks(ledges, covers, true, .6f);
            left.SyncBanks(ledges, covers, true, .6f);
            var returned = left.banks.Find(bank => bank.Handle == handle);
            if (returned == null || !ReferenceEquals(returned.Heights, profile) || !returned.Occupied)
                throw new InvalidOperationException("The returning window did not retain its snow.");
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
        {
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
                if (!System.Threading.SpinWait.SpinUntil(() => layer.Ready, 2000)
                    || !layer.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
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
                layer.RejectNextPost = true;
                if (layer.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
                    || !System.Threading.SpinWait.SpinUntil(() => layer.Finished, 2000))
                    throw new InvalidOperationException("A failed post left the drag layer running.");

                dragStrip = layer;
                if (DragStripAvailable() || dragStrip != null)
                    throw new InvalidOperationException("The drag layer did not fall back after failure.");
                if (!System.Threading.SpinWait.SpinUntil(DragStripAvailable, 4000)
                    || ReferenceEquals(layer, dragStrip))
                    throw new InvalidOperationException("The drag layer did not retry.");
                if (!dragStrip.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
                    || !dragStrip.WaitForPosition(position.X, position.Y + 21, 67))
                    throw new InvalidOperationException("The replacement drag layer could not present a frame.");
                dragStrip.RejectNextPresentation = true;
                if (!dragStrip.Publish(target.Handle, frame, pixels, Padding.Empty, bitmapBits)
                    || !System.Threading.SpinWait.SpinUntil(() => dragStrip.Finished, 2000)
                    || !dragStrip.Failed)
                    throw new InvalidOperationException("A failed presentation left the drag layer running.");

            }
            finally
            {
                if (dragStrip != null) dragStrip.Dispose();
                dragStrip = null;
                target.DestroyHandle();
            }
        }
    }

    internal static void VerifyAdjacentAnimalFrames()
    {
        var leftBounds = new Rectangle(-320, -36, 320, 300);
        var rightBounds = new Rectangle(0, -36, 400, 300);
        using (var left = new SnowWindow(new DisplayArea(leftBounds, leftBounds), true, handle => null))
        using (var right = new SnowWindow(new DisplayArea(rightBounds, rightBounds), true, handle => null))
        {
            var scene = new SnowAnimalScene();
            scene.TestAt((int)SnowAnimal.Penguin, 0, left.Display.Ground);
            var covers = new List<Rectangle>();
            left.BeginFrame(Rectangle.Empty);
            right.BeginFrame(Rectangle.Empty);
            left.DrawAnimals(scene, covers);
            right.DrawAnimals(scene, covers);
            left.canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            right.canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            int sampleY = left.Display.Ground - leftBounds.Top - 12;
            if (left.bitmap.GetPixel(leftBounds.Width - 6, sampleY).A == 0
                || right.bitmap.GetPixel(5, sampleY).A == 0)
                throw new InvalidOperationException("A display seam clipped one half of the same animal.");
            left.PublishOverlay();
            right.PublishOverlay();
        }
    }

    public void VerifyAnimalClip()
    {
        var scene = new SnowAnimalScene();
        int x = size.Width / 2;
        int ground = Display.Ground - ScreenBounds.Top;
        scene.TestAt((int)SnowAnimal.Penguin, ScreenBounds.Left + x, Display.Ground);
        var bounds = new List<Rectangle>();
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A == 0)
            throw new InvalidOperationException("The test animal was not visible.");

        bounds.Add(new Rectangle(x - 40, ground - 50, 80, 55));
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A != 0)
            throw new InvalidOperationException("The animal drew over an overlapping window.");

        bounds.Clear();
        BeginFrame(Rectangle.Empty);
        DrawAnimals(scene, bounds);
        canvas.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        if (bitmap.GetPixel(x, ground - 12).A == 0)
            throw new InvalidOperationException("The animal did not reappear after the window moved.");
    }
}

sealed partial class SnowAnimalScene
{
    internal void TestAt(int kind, float x, float y)
    {
        var point = new PointF(x, y);
        BeginVisit((SnowAnimal)kind, 1, point);
        route.Clear();
        AddLeg(ref point, x + 150, y);
        Active = true;
    }

    internal static void VerifyRoutes()
    {
        var left = new DisplayArea(new Rectangle(-400, -60, 400, 380), new Rectangle(-400, -60, 400, 360));
        var right = new DisplayArea(new Rectangle(0, 0, 500, 440), new Rectangle(0, 0, 500, 410));
        var scene = new SnowAnimalScene();
        var pair = new List<DisplayArea> { right, left };
        scene.Advance(0, pair);
        scene.StartVisit(right, SnowAnimal.Fox, 1);
        if (scene.X >= left.Bounds.Left)
            throw new InvalidOperationException("The route did not start on the outer display.");
        bool crossed = false;
        for (int i = 0; i < 400 && scene.Active; i++)
        {
            scene.Advance(.025f, pair);
            if (scene.X < 1 || scene.X > 18) continue;
            crossed = scene.Visitor == SnowAnimal.Fox && scene.Gait > 1 && scene.Y >= 295 && scene.Y < 330;
            if (crossed) break;
        }
        if (!crossed) throw new InvalidOperationException("The same animal did not cross staggered displays.");

        var upper = new DisplayArea(new Rectangle(0, -380, 480, 380), new Rectangle(0, -380, 480, 350));
        var stack = new List<DisplayArea> { right, upper };
        scene.Advance(0, stack);
        scene.StartVisit(right, SnowAnimal.Penguin, 1);
        crossed = false;
        for (int i = 0; i < 400 && scene.Active; i++)
        {
            scene.Advance(.025f, stack);
            if (scene.Y > right.Bounds.Top + 30 && scene.Falling && scene.Visitor == SnowAnimal.Penguin)
            {
                crossed = true;
                break;
            }
        }
        if (!crossed) throw new InvalidOperationException("The animal did not descend to the lower display.");
    }
}

sealed partial class DragLayer
{
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
}
