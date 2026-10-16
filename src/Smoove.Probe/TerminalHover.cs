using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Smoove.Core;

namespace Smoove.Probe;

// Called only on the route thread. Never query a foreign UIA provider inside the wheel hook.
internal sealed class TerminalHover
{
    private long _checkedAt;
    private nint _root;
    private Native.Point _point;
    private bool _terminal;
    private Native.Rect? _bounds;

    internal (bool Terminal, Native.Rect? Bounds) Check(string processName, nint root, Native.Point point)
    {
        if (!ScrollRouting.IsIde(processName)) return (false, null);
        long now = Stopwatch.GetTimestamp();
        if (_root == root && _point.X == point.X && _point.Y == point.Y && now - _checkedAt < Stopwatch.Frequency / 10)
            return (_terminal, _bounds);
        _root = root; _point = point; _checkedAt = now;
        _terminal = true; _bounds = null;
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            if (element.Current.ControlType == ControlType.Window || element.Current.NativeWindowHandle == root)
                return (_terminal, _bounds);
            var leaf = element.Current.BoundingRectangle;
            Native.GetWindowThreadProcessId(root, out uint pid);
            bool terminal = false;
            for (int depth = 0; element is not null && depth < 12; depth++, element = TreeWalker.ControlViewWalker.GetParent(element))
            {
                var current = element.Current;
                if (current.ProcessId != pid) break;
                // Window titles such as "Terminal project" do not identify a terminal pane.
                if (current.ControlType != ControlType.Window && ScrollRouting.IsTerminalElement(current.Name, current.ClassName, current.AutomationId))
                { terminal = true; break; }
            }
            if (!leaf.IsEmpty && leaf.Width > 0 && leaf.Height > 0)
            {
                _bounds = new Native.Rect { Left = (int)leaf.Left, Top = (int)leaf.Top, Right = (int)Math.Ceiling(leaf.Right), Bottom = (int)Math.Ceiling(leaf.Bottom) };
                _terminal = terminal;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // Unknown IDE pane: preserve native input until the provider responds again.
        }
        return (_terminal, _bounds);
    }
}
