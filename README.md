# Desktop Snow

A click-through Windows overlay that brings animated snowfall to your desktop and apps. Flakes pile up in soft drifts, shake loose from windows, and land again on lower ledges.

## Desktop Snow in action

https://github.com/user-attachments/assets/8adaea70-498e-4642-b2d4-6f8d4b6bd71e

## Download and run

Download **DesktopSnow.exe** from [GitHub Releases](https://github.com/TonyAldo/desktop-snow/releases). If an older copy is running, choose **Exit Desktop Snow** in its tray menu first. Open the executable, then look for the snowflake icon in the Windows notification area, including the hidden icons menu.

Desktop Snow targets Windows 10 and 11 with .NET Framework 4.x. The executable is portable and unsigned. It runs without an installer or administrator access. You can also build it from the source below.

## Controls

| Tray option | Behavior |
| --- | --- |
| **Pause snowfall** | Hide the overlay until you resume. Double-click the tray icon to toggle pause. |
| **Weather** | Choose Gentle Flurries, Steady Snow, or Blizzard. Each changes flake size, density, wind, and accumulation. |
| **Let snow accumulate** | Toggle snowbanks on visible edges of non-maximized windows, taskbars, and the desktop. |
| **Snow animals** | Enable occasional visits from hopping rabbits, pouncing foxes, and sliding penguins. Off by default. |
| **Clear snow** | Send a plow along snowy ledges one at a time per display. It slows in deep snow and leaves a dusting. |
| **Settings** | Adjust wind and accumulation from 0% to 200% of the current preset and toggle fullscreen auto-pause. |
| **Exit Desktop Snow** | Close the app and remove the overlay. |

Press **Ctrl + Alt + Shift + S** to clear snow. If Windows cannot register the shortcut, use the tray menu.

Wiggle a snowy window by its title bar to dislodge clumps. The clumps can land on lower windows or the taskbar and add to their drifts. Minimizing a snowy window also sends its drift tumbling from the title bar. Fullscreen auto-pause hides snow on the display covered by the foreground app. Snow continues on the other displays.

Desktop Snow adapts when you connect, disconnect, or rearrange displays. Snowbanks travel with their windows across display boundaries. Animals can continue across adjacent displays, including offset and stacked layouts.

Preferences are saved in `%LOCALAPPDATA%\DesktopSnow\weather.txt`. To reset them, exit the app and rename that file before reopening it.

## Build and test

Open PowerShell in the source directory on Windows and run:

```powershell
.\build.cmd
```

The script uses the C# compiler included with .NET Framework 4.x and writes `dist\DesktopSnow.exe`. There are no NuGet dependencies. The app uses Windows Forms for the tray and settings, GDI+ for drawing, and Win32 layered windows for its click-through overlay.

```powershell
.\build.cmd --smoke-test
```

The off-screen smoke test exercises rendering, the three weather presets and animals, snow shedding during window shakes and minimization, redepositing, plow clearing, settings parsing, and fullscreen geometry.

## Source layout

| File | Purpose |
| --- | --- |
| `DesktopSnow.cs` | Tray controls, overlay, snow physics, and plow |
| `SnowSettings.cs` | Weather presets, settings dialog, preferences, and shortcut |
| `SnowAnimals.cs` | Visiting animals and tracks |
| `DesktopSnow.manifest` | Windows compatibility, DPI awareness, and execution level |
| `build.cmd` | Build and optional smoke test |

## Feedback

Report bugs or suggest features in [GitHub Issues](https://github.com/TonyAldo/desktop-snow/issues). For rendering issues, include your Windows version, display resolution and scaling, and steps to reproduce the problem.

## License

Desktop Snow is provided without warranty under the [GNU General Public License, version 3 or later](LICENSE). Corresponding source for release binaries is available from the release tag and its source archives.
