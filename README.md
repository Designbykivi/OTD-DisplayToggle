# Display Toggle for OpenTabletDriver

A **Display Toggle** binding for OpenTabletDriver. Assign it to any pen button,
express key, tip or eraser. Each press moves the tablet's mapping, and the cursor with it, to the next monitor.

Requires OpenTabletDriver **0.6.5 or newer** (tested against the 0.6.5.1 and 0.6.8 plugin APIs) and **Absolute Mode**.

<img width="504" height="450" alt="image" src="https://github.com/user-attachments/assets/4c1f1a88-5522-4379-a2f8-c0b5007f25ed" />


## Install

**From the Plugin Manager:** in OpenTabletDriver, open **Plugins → Plugin Manager**, select
**Display Toggle** and click **Install**.

**Manually:** download `DisplayToggle.zip` from the
[latest release](https://github.com/Designbykivi/OTD-DisplayToggle/releases/latest) and drag it
into the Plugin Manager. You can also copy `DisplayToggle.dll` into its own folder under the plugin directory:

- macOS: `~/Library/Application Support/OpenTabletDriver/Plugins/Display Toggle/`
- Windows: `%localappdata%\OpenTabletDriver\Plugins\Display Toggle\`
- Linux: `~/.config/OpenTabletDriver/Plugins/Display Toggle/`

Then restart OpenTabletDriver.

## Build

Requires the .NET 8 SDK or newer.

```bash
dotnet build -c Release
```

The plugin is written to `bin/Release/net8.0/DisplayToggle.dll`.

## Use

Go to **Pen Settings** (or **Auxiliary Settings** for express keys). Click a button, choose
**Display Toggle**, pick an action, then click **Apply**.

| Action | What it does |
|---|---|
| Next display | Moves to the next monitor, left to right, wrapping back to monitor 1. If "Include full desktop in cycle" is on, full desktop comes after the last monitor. |
| Previous display | Moves through the same cycle in reverse. |
| Specific display | Jumps to the monitor given by **Display number**. |
| Full desktop | Maps the tablet to all monitors. |
| Original area | Restores the output area you configured in OTD. |

| Option | Default | |
|---|---|---|
| Display number | 2 | Used by *Specific display*. Monitors are counted 1, 2, 3… from left to right. |
| Include full desktop in cycle | off | Adds an "all displays" step to Next/Previous. |
| Force proportions | off | Fits the tablet area to the target monitor's aspect ratio, inside the area you configured, so nothing gets stretched. |
| Hold to switch | off | Switches only while the button is held, then returns to the previous monitor. |
| Move cursor immediately | on | Moves the cursor to the centre of the new monitor even when the pen is out of range. This helps with express keys. |

Every Display Toggle binding on a tablet shares one position in the cycle. For example,
*Next* on the pen and *Previous* on an express key move through the same list.

## Notes

- The toggle changes the live output area only. OTD's UI still shows your configured area.
  Clicking **Apply** or **Save** resets the mapping to that configured area.
- Relative Mode is not supported because it has no monitor mapping. A warning is written to the OTD log.
- The plugin API does not expose the active output mode, so the plugin finds it through
  reflection on the driver (`Driver.InputDevices[].OutputMode`). This has been checked against
  0.6.x. A future major OTD version (0.7) will probably need an update.

## License

[MIT](LICENSE)
