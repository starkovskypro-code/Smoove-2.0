namespace Smoove.Core;

public static class ScrollRouting
{
    public static int OutputFrequency(int configuredHz, int monitorHz) => configuredHz == 0
        ? (int)Math.Clamp((long)(monitorHz > 1 ? monitorHz : 60) * 2, 60, 500) : configuredHz;

    // Internal motion uses positive left/up; Windows native horizontal wheel uses positive right.
    public static int MotionDelta(int delta, bool nativeHorizontal) => nativeHorizontal ? -delta : delta;
    // REAPER's MIDI wheel action consumes one whole notch per event.
    public static double MotionScale(double multiplier, bool midiEditor) => midiEditor ? multiplier * 0.15 : multiplier;
    // REAPER's reversed horizontal action matches Shift+wheel rather than Windows' native sign.
    public static int OutputDelta(int delta,bool horizontalMessage,bool nativeHorizontal,bool reaperTracks) =>
        horizontalMessage && !(reaperTracks && !nativeHorizontal) ? -delta : delta;
    public static bool IsIde(string processName) => processName.ToLowerInvariant() is
        "code" or "code - insiders" or "cursor" or "windsurf" or "vscodium" or "codium" or "devenv" or "rider" or "rider64";
    public static bool IsTerminalElement(string name, string className, string automationId) =>
        className.Contains("xterm", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("terminal", StringComparison.OrdinalIgnoreCase) ||
        automationId.Contains("terminal", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Terminal", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Terminal ", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Терминал", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Терминал ", StringComparison.OrdinalIgnoreCase);
}
