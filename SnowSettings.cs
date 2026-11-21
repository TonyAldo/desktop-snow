// Desktop Snow
// Copyright (C) 2026 TonyAldo
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

sealed class SnowWeather
{
    public static readonly SnowWeather[] Presets =
    {
        new SnowWeather("Gentle Flurries", 90, .75f, .42f, .55f),
        new SnowWeather("Steady Snow", 235, 1f, 1f, 1f),
        new SnowWeather("Blizzard", 610, 1.20f, 2.35f, 1.55f)
    };

    public readonly string Name;
    public readonly int Density;
    public readonly float Size, Wind, Deposit;

    SnowWeather(string name, int density, float size, float wind, float deposit)
    {
        Name = name;
        Density = density;
        Size = size;
        Wind = wind;
        Deposit = deposit;
    }
}

sealed class SnowSettings
{
    public int Preset = 1, Wind = 100, Accumulation = 100;
    public bool Settle = true, AutoPause = true, Animals;
    static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopSnow", "weather.txt");

    public SnowWeather Weather { get { return SnowWeather.Presets[Preset]; } }

    public static SnowSettings Load()
    {
        try { return Read(File.ReadAllLines(PathName)); }
        catch (IOException) { return new SnowSettings(); }
        catch (UnauthorizedAccessException) { return new SnowSettings(); }
    }

    internal static SnowSettings Read(string[] values)
    {
        var result = new SnowSettings();
        if (values.Length < 5) return result;
        int value;
        bool flag;
        if (int.TryParse(values[0], out value) && value >= 0 && value < SnowWeather.Presets.Length) result.Preset = value;
        if (int.TryParse(values[1], out value) && value >= 0 && value <= 200) result.Wind = value;
        if (int.TryParse(values[2], out value) && value >= 0 && value <= 200) result.Accumulation = value;
        if (bool.TryParse(values[3], out flag)) result.Settle = flag;
        if (bool.TryParse(values[4], out flag)) result.AutoPause = flag;
        if (values.Length >= 6 && bool.TryParse(values[5], out flag)) result.Animals = flag;
        return result;
    }

    internal string[] Write()
    {
        return new[] { Preset.ToString(), Wind.ToString(), Accumulation.ToString(), Settle.ToString(), AutoPause.ToString(), Animals.ToString() };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName));
        string temp = PathName + ".tmp";
        File.WriteAllLines(temp, Write());
        if (File.Exists(PathName)) File.Replace(temp, PathName, null);
        else File.Move(temp, PathName);
    }
}

sealed class SnowSettingsDialog : Form
{
    readonly TrackBar wind, accumulation;
    readonly CheckBox fullscreen;
    readonly SnowSettings settings;

    public SnowSettingsDialog(SnowSettings settings, bool shortcutAvailable)
    {
        this.settings = settings;
        Text = "Desktop Snow Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;

        var layout = new FlowLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16)
        };
        wind = AddSlider(layout, "Wind", settings.Wind);
        accumulation = AddSlider(layout, "Accumulation rate", settings.Accumulation);
        fullscreen = new CheckBox { Text = "Pause during fullscreen apps", Checked = settings.AutoPause, AutoSize = true };
        layout.Controls.Add(fullscreen);
        layout.Controls.Add(new Label {
            Text = shortcutAvailable ? "Clear snow shortcut   Ctrl + Alt + Shift + S"
                : "Clear snow shortcut unavailable. Use the tray menu.",
            AutoSize = true, Margin = new Padding(3, 9, 3, 12)
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static TrackBar AddSlider(FlowLayoutPanel layout, string name, int value)
    {
        var label = new Label { AutoSize = true };
        var slider = new TrackBar { AccessibleName = name, Maximum = 200, TickFrequency = 25,
            SmallChange = 5, LargeChange = 25, Value = value, Width = 290 };
        EventHandler update = (sender, args) => label.Text = name + "   " + slider.Value + "%";
        update(null, EventArgs.Empty);
        slider.ValueChanged += update;
        layout.Controls.Add(label);
        layout.Controls.Add(slider);
        return slider;
    }

    public void Apply()
    {
        settings.Wind = wind.Value;
        settings.Accumulation = accumulation.Value;
        settings.AutoPause = fullscreen.Checked;
    }
}

sealed class ClearShortcut : NativeWindow, IDisposable
{
    readonly Action clear;
    public readonly bool Registered;

    public ClearShortcut(Action clear)
    {
        this.clear = clear;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        const uint modifiers = 0x4000 | 1 | 2 | 4; // No repeat, Alt, Control, Shift.
        Registered = Native.RegisterHotKey(Handle, 1, modifiers, (uint)Keys.S);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312 && message.WParam.ToInt32() == 1) clear();
        else base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}
