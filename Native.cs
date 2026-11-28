// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

static class Native
{
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public uint Size;
        public Rect Bounds, WorkArea;
        public uint Flags;
    }

    delegate bool DisplayVisitor(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, DisplayVisitor visitor, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static List<DisplayArea> ReadDisplays()
    {
        var displays = new List<DisplayArea>();
        bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                Rectangle area = info.Bounds.ToRectangle();
                if (area.Width > 0 && area.Height > 0 && !displays.Exists(display => display.Bounds == area))
                    displays.Add(new DisplayArea(area, info.WorkArea.ToRectangle()));
                return true;
            }, IntPtr.Zero);
        if (!ok) displays.Clear();
        return displays;
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
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
