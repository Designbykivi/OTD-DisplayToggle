using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Display;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace DisplayToggle
{
    /// <summary>
    /// "Display Toggle": remaps the absolute output area to another monitor
    /// when a pen button or express key is pressed.
    /// </summary>
    [PluginName(PLUGIN_NAME)]
    public class DisplayToggleBinding : IStateBinding
    {
        private const string PLUGIN_NAME = "Display Toggle";

        public const string NEXT = "Next display";
        public const string PREVIOUS = "Previous display";
        public const string SPECIFIC = "Specific display";
        public const string FULL_DESKTOP = "Full desktop";
        public const string ORIGINAL = "Original area";

        public static string[] Actions { get; } = { NEXT, PREVIOUS, SPECIFIC, FULL_DESKTOP, ORIGINAL };

        // Shared between every binding instance so that e.g. "Next" on the pen and
        // "Previous" on an express key operate on the same toggle position.
        // Keyed by output mode instance, so applying settings in OTD starts fresh.
        private static readonly ConditionalWeakTable<AbsoluteOutputMode, ToggleState> States = new();

        private static bool warnedNotAbsolute;

        [Resolved]
        public IDriver? Driver { set; get; }

        [Resolved]
        public IVirtualScreen? VirtualScreen { set; get; }

        [Property("Action"), PropertyValidated(nameof(Actions)), DefaultPropertyValue(NEXT)]
        [ToolTip("Next/Previous: cycle through monitors (left to right).\n" +
                 "Specific display: jump to the monitor number below.\n" +
                 "Full desktop: map to all monitors.\n" +
                 "Original area: restore the output area configured in OTD.")]
        public string Action { set; get; } = NEXT;

        [Property("Display number"), DefaultPropertyValue(2)]
        [ToolTip("Used by \"Specific display\". Monitors are numbered 1, 2, 3... from left to right " +
                 "(top to bottom when stacked). The order is printed to the OTD log when the binding loads.")]
        public int DisplayNumber { set; get; } = 2;

        [BooleanProperty("Include full desktop in cycle", ""), DefaultPropertyValue(false)]
        [ToolTip("When cycling with Next/Previous, include a step that maps the pen to all monitors.")]
        public bool IncludeFullDesktop { set; get; }

        [BooleanProperty("Force proportions", ""), DefaultPropertyValue(false)]
        [ToolTip("Shrink the tablet area (within the area you configured) so it matches the aspect ratio of the " +
                 "target monitor. Prevents stretching when monitors have different shapes.")]
        public bool ForceProportions { set; get; }

        [BooleanProperty("Hold to switch", ""), DefaultPropertyValue(false)]
        [ToolTip("Only stay on the new monitor while the button is held; return to the previous one on release.")]
        public bool HoldToSwitch { set; get; }

        [BooleanProperty("Move cursor immediately", ""), DefaultPropertyValue(true)]
        [ToolTip("Jump the cursor to the centre of the new monitor right away, even if the pen is out of range.")]
        public bool MoveCursor { set; get; } = true;

        [OnDependencyLoad]
        public void OnLoad()
        {
            if (Driver == null || VirtualScreen == null)
            {
                Log.Write(PLUGIN_NAME, "Driver or virtual screen unavailable; display toggling will not work.", LogLevel.Error);
                return;
            }

            var order = GetDisplays().Select((d, i) => $"{i + 1}: {d.Width}x{d.Height} @ {d.Position}");
            Log.Debug(PLUGIN_NAME, "Monitor order: " + string.Join(", ", order));
        }

        // Bindings run on the tablet's report thread: an exception escaping here kills the
        // reader and OTD loses the tablet until "Detect tablet". Never let one through.
        public void Press(TabletReference tablet, IDeviceReport report) => Guard(() => PressCore(tablet));

        public void Release(TabletReference tablet, IDeviceReport report) => Guard(() => ReleaseCore(tablet));

        private static void Guard(System.Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Write(PLUGIN_NAME, $"Display toggle failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                Log.Debug(PLUGIN_NAME, ex.ToString());
            }
        }

        private void PressCore(TabletReference tablet)
        {
            if (FindOutputMode(tablet) is not AbsoluteOutputMode mode)
                return;

            var state = States.GetValue(mode, m => new ToggleState(m));
            lock (state)
            {
                var targets = GetTargets(state);
                if (targets.Count == 0)
                    return;

                if (HoldToSwitch)
                    state.HeldFrom = state.Current;

                int next = Action switch
                {
                    NEXT => CycleIndex(state.Current, +1, targets),
                    PREVIOUS => CycleIndex(state.Current, -1, targets),
                    SPECIFIC => Math.Clamp(DisplayNumber - 1, 0, GetDisplays().Count - 1),
                    FULL_DESKTOP => targets.FindIndex(t => t.IsFullDesktop),
                    _ => ToggleState.ORIGINAL_INDEX
                };

                if (Action == FULL_DESKTOP && next < 0)
                {
                    // Full desktop isn't in the cycle; apply it directly.
                    Apply(mode, state, ToggleState.FULL_DESKTOP_INDEX, CreateFullDesktopArea());
                    return;
                }

                Apply(mode, state, next, next >= 0 ? targets[next].Area : null);
            }
        }

        private void ReleaseCore(TabletReference tablet)
        {
            if (!HoldToSwitch || FindOutputMode(tablet) is not AbsoluteOutputMode mode)
                return;

            if (!States.TryGetValue(mode, out var state))
                return;

            lock (state)
            {
                if (state.HeldFrom is not int previous)
                    return;

                state.HeldFrom = null;
                var targets = GetTargets(state);
                Area? area = previous switch
                {
                    >= 0 when previous < targets.Count => targets[previous].Area,
                    ToggleState.FULL_DESKTOP_INDEX => CreateFullDesktopArea(),
                    _ => null
                };
                Apply(mode, state, area == null ? ToggleState.ORIGINAL_INDEX : previous, area);
            }
        }

        /// <param name="output">The output area to apply, or null to restore the originally configured areas.</param>
        private void Apply(AbsoluteOutputMode mode, ToggleState state, int index, Area? output)
        {
            state.Current = index;

            if (output == null)
            {
                mode.Input = Clone(state.OriginalInput);
                mode.Output = Clone(state.OriginalOutput);
            }
            else
            {
                mode.Input = ForceProportions
                    ? FitAspect(state.OriginalInput, output.Width / output.Height)
                    : Clone(state.OriginalInput);
                mode.Output = output;
            }

            if (MoveCursor && mode.Pointer is IAbsolutePointer pointer)
            {
                pointer.SetPosition(mode.Output.Position);
                if (pointer is ISynchronousPointer sync)
                    sync.Flush();
            }

            Log.Debug(PLUGIN_NAME, $"Output area -> {mode.Output.Width}x{mode.Output.Height} centred at {mode.Output.Position}");
        }

        private static int CycleIndex(int current, int step, List<Target> targets)
        {
            // From the original area (or an off-cycle full desktop), start at the first/last target.
            if (current < 0 || current >= targets.Count)
                return step > 0 ? 0 : targets.Count - 1;
            return (current + step + targets.Count) % targets.Count;
        }

        private List<Target> GetTargets(ToggleState state)
        {
            var targets = GetDisplays().Select(d => new Target(CreateDisplayArea(d), false)).ToList();
            if (IncludeFullDesktop && targets.Count > 1)
                targets.Add(new Target(CreateFullDesktopArea(), true));

            // On first use, figure out where in the cycle the configured area already sits,
            // so that "Next" from monitor 1 goes to monitor 2 rather than staying put.
            if (!state.Initialized)
            {
                state.Initialized = true;
                state.Current = targets.FindIndex(t => Matches(t.Area, state.OriginalOutput));
            }

            return targets;
        }

        /// <summary>
        /// Physical monitors ordered left to right, then top to bottom.
        /// </summary>
        private List<IDisplay> GetDisplays()
        {
            if (VirtualScreen == null)
                return new List<IDisplay>();

            return VirtualScreen.Displays
                .Where(d => d is not IVirtualScreen)
                .OrderBy(d => d.Position.X)
                .ThenBy(d => d.Position.Y)
                .ToList();
        }

        // Mirrors OTD's own "Set to display" menu in the output area editor, so the
        // resulting areas are identical to what the user would pick by hand.
        private Area CreateDisplayArea(IDisplay display)
        {
            var displays = VirtualScreen!.Displays.Where(d => d is not IVirtualScreen).ToList();
            float xOffset = displays.Min(d => d.Position.X);
            float yOffset = displays.Min(d => d.Position.Y);

            var center = new Vector2(
                display.Position.X - xOffset + VirtualScreen.Position.X + display.Width / 2,
                display.Position.Y - yOffset + VirtualScreen.Position.Y + display.Height / 2);
            return new Area(display.Width, display.Height, center, 0);
        }

        private Area CreateFullDesktopArea()
        {
            var screen = VirtualScreen!;
            return new Area(screen.Width, screen.Height, new Vector2(screen.Width / 2, screen.Height / 2), 0);
        }

        /// <summary>
        /// Largest area with the given aspect ratio that fits inside <paramref name="input"/>, sharing its centre and rotation.
        /// </summary>
        private static Area FitAspect(Area input, float aspect)
        {
            float width = input.Width;
            float height = input.Width / aspect;
            if (height > input.Height)
            {
                height = input.Height;
                width = input.Height * aspect;
            }

            return new Area(width, height, input.Position, input.Rotation);
        }

        private static bool Matches(Area a, Area b)
        {
            const float tolerance = 1f;
            return Math.Abs(a.Width - b.Width) < tolerance
                && Math.Abs(a.Height - b.Height) < tolerance
                && Vector2.Distance(a.Position, b.Position) < tolerance;
        }

        private static Area Clone(Area area) => new Area(area.Width, area.Height, area.Position, area.Rotation);

        private AbsoluteOutputMode? FindOutputMode(TabletReference tablet)
        {
            if (Driver == null)
                return null;

            // The plugin API doesn't expose output modes, but the concrete driver has
            // InputDevices -> InputDeviceTree.OutputMode. Reach them via reflection so the
            // plugin only depends on OpenTabletDriver.Plugin.
            if (Driver.GetType().GetProperty("InputDevices")?.GetValue(Driver) is not IEnumerable trees)
                return null;

            IOutputMode? fallback = null;
            foreach (var tree in trees)
            {
                if (tree?.GetType().GetProperty("OutputMode")?.GetValue(tree) is not IOutputMode mode)
                    continue;

                if (ReferenceEquals(mode.Tablet, tablet))
                    return Absolute(mode);
                if (mode.Tablet?.Properties?.Name == tablet?.Properties?.Name)
                    fallback ??= mode;
            }

            return fallback != null ? Absolute(fallback) : null;
        }

        private static AbsoluteOutputMode? Absolute(IOutputMode mode)
        {
            if (mode is AbsoluteOutputMode absolute)
                return absolute;

            if (!warnedNotAbsolute)
            {
                warnedNotAbsolute = true;
                Log.Write(PLUGIN_NAME, "Display Toggle needs an absolute output mode (e.g. \"Absolute Mode\"); relative mode has no monitor mapping.", LogLevel.Warning);
            }
            return null;
        }

        public override string ToString() => Action == SPECIFIC
            ? $"{PLUGIN_NAME}: Display {DisplayNumber}"
            : $"{PLUGIN_NAME}: {Action}";

        private record Target(Area Area, bool IsFullDesktop);

        private class ToggleState
        {
            public const int ORIGINAL_INDEX = -1;
            public const int FULL_DESKTOP_INDEX = -2;

            public ToggleState(AbsoluteOutputMode mode)
            {
                OriginalInput = Clone(mode.Input);
                OriginalOutput = Clone(mode.Output);
            }

            public Area OriginalInput { get; }
            public Area OriginalOutput { get; }
            public bool Initialized { set; get; }
            public int Current { set; get; } = ORIGINAL_INDEX;
            public int? HeldFrom { set; get; }
        }
    }
}
