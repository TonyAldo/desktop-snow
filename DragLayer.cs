// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed partial class DragLayer : NativeWindow, IDisposable
{
    const int FrameMessage = 0x8001;
    const int CloseMessage = 0x0010;
    internal const int StripHeight = 38;
    readonly object gate = new object();

    readonly Native.Size display;
    readonly Native.Point origin;
    readonly IntPtr overlay;
    readonly int[] transfer;
    readonly Native.WindowEvent moved;
    volatile bool ready, failed, stopping;
    public bool Failed { get { return failed; } }
    public bool Ready { get { return ready && !failed && !stopping; } }
#if SMOKE_TEST
    internal bool RejectNextPost;
    internal volatile bool RejectNextPresentation;
    internal volatile bool Finished;
#endif
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
        var thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "Desktop Snow window tracking" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
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
            poll = new Timer { Interval = 250 };
            poll.Tick += (sender, args) =>
            {
                if (failed || stopping) Application.ExitThread();
                else if (current.Target != IntPtr.Zero) Present(false);
            };
            poll.Start();
            ready = true;
            if (!stopping) Application.Run();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            FlagFailure(error.Message);
        }
        finally
        {
            if (moveHook != IntPtr.Zero) Native.UnhookWinEvent(moveHook);
            if (poll != null) poll.Dispose();
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            if (Handle != IntPtr.Zero) DestroyHandle();
            ready = false;
#if SMOKE_TEST
            Finished = true;
#endif
        }
    }

    public bool Publish(IntPtr target, Rectangle frame, Rectangle area, Padding? insets, IntPtr source)
    {
        if (!Ready || Handle == IntPtr.Zero) return false;
        lock (gate)
        {
            Marshal.Copy(source, transfer, 0, display.Width * area.Height);
            pending = new Tile { Target = target, Frame = frame, Area = area, Insets = insets };
            return PostFrame();
        }
    }

    public void Hide()
    {
        if (!Ready || Handle == IntPtr.Zero) return;
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
#if SMOKE_TEST
        if (RejectNextPost)
        {
            RejectNextPost = false;
            FlagFailure("Injected frame delivery failure.");
            return false;
        }
#endif
        queued = Native.PostMessage(Handle, FrameMessage, IntPtr.Zero, IntPtr.Zero);
        if (!queued) FlagFailure("Could not post a drag frame. Win32 error " + Marshal.GetLastWin32Error());
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
            poll.Interval = current.Target == IntPtr.Zero ? 250 : 15;
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
            if (pixels == lastPixels) return;
            if (Native.SetWindowPos(Handle, IntPtr.Zero,
                origin.X + pixels.X, origin.Y + pixels.Y, 0, 0, Native.MoveWithoutActivation))
            {
                lastPixels = pixels;
                return;
            }
        }

        var dst = new Native.Point(origin.X + pixels.X, origin.Y + pixels.Y);
        var extent = new Native.Size(pixels.Width, pixels.Height);
        var src = new Native.Point(source.X, source.Y);
        var blend = new Native.Blend { Alpha = 255, Format = 1 };
#if SMOKE_TEST
        bool injectedFailure = RejectNextPresentation;
        RejectNextPresentation = false;
        if (injectedFailure || !Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref extent, dc, ref src, 0, ref blend, 2))
#else
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref extent, dc, ref src, 0, ref blend, 2))
#endif
        {
#if SMOKE_TEST
            if (injectedFailure) FlagFailure("Injected presentation failure.");
            else
#endif
                FlagFailure("Could not update the drag layer. Win32 error " + Marshal.GetLastWin32Error());
            SetHidden();
            Application.ExitThread();
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

    void FlagFailure(string reason)
    {
        failed = true;
        SnowLog.DragLayerFailure(reason);
    }

    void SetHidden()
    {
        if (!shown) return;
        Native.ShowWindow(Handle, 0);
        shown = false;
    }

    public void Dispose()
    {
        stopping = true;
        if (Handle != IntPtr.Zero) Native.PostMessage(Handle, CloseMessage, IntPtr.Zero, IntPtr.Zero);
    }
}

static class SnowLog
{
    static readonly object gate = new object();
    public static void DragLayerFailure(string reason)
    {
#if SMOKE_TEST
        if (reason == "Injected frame delivery failure." || reason == "Injected presentation failure.") return;
#endif
        System.Threading.ThreadPool.QueueUserWorkItem(ignored =>
        {
            try
            {
                lock (gate)
                {
                    string folder = System.IO.Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "DesktopSnow");
                    string path = System.IO.Path.Combine(folder, "error.log");
                    System.IO.Directory.CreateDirectory(folder);
                    var file = new System.IO.FileInfo(path);
                    if (file.Exists && file.Length >= 256 * 1024) file.Delete();
                    System.IO.File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + reason + Environment.NewLine);
                }
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }
}
