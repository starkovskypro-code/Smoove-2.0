using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Smoove.Probe;

internal static class TelegramScrollCheck
{
    internal static Task<string> Run(WheelEngine engine, string report) => Task.Run(async () =>
    {
        using var process = Process.GetProcessesByName("Telegram").FirstOrDefault(p => p.MainWindowHandle != 0)
            ?? throw new InvalidOperationException("Откройте переписку в Telegram до запуска проверки");
        nint window = process.MainWindowHandle;
        Native.ShowWindow(window, 9); // Restore minimized current chat before measuring its geometry.
        await Task.Delay(300);
        var root = AutomationElement.FromHandle(window);
        var history = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "class HistoryWidget"))
            ?? throw new InvalidOperationException("Текущая переписка не найдена; другие страницы не открывались");
        var viewport = history.Current.BoundingRectangle;
        var rows = history.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        if (rows.Count == 0) throw new InvalidOperationException("Telegram не предоставил геометрию сообщений");
        int low = 0, high = rows.Count - 1;
        double desiredY = viewport.Top + viewport.Height * 0.45;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (rows[middle].Current.BoundingRectangle.Top < desiredY) low = middle + 1;
            else high = middle;
        }
        var anchor = rows[low];
        double initialY = anchor.Current.BoundingRectangle.Top;
        if (initialY < viewport.Top || initialY > viewport.Bottom - 150)
            throw new InvalidOperationException("Нет пригодного видимого элемента для измерения; переписка не менялась");
        Native.GetCursorPos(out var oldCursor);
        var point = new Native.Point { X = (int)(viewport.Left + viewport.Width * 0.45), Y = (int)(viewport.Top + viewport.Height * 0.5) };
        try
        {
            Native.SetWindowPos(window, -1, 0, 0, 0, 0, 0x13);
            Native.SetForegroundWindow(window);
            Native.SetCursorPos(point.X, point.Y);
            engine.AllowExternalTestWindow(window);
            await Task.Delay(1000);
            if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != window)
                throw new InvalidOperationException($"Курсор не в текущей переписке Telegram: point={point.X},{point.Y}; viewport={viewport}; hitRoot={Native.GetAncestor(Native.WindowFromPoint(point), 2):X}; expected={window:X}");
            nint foreground = Native.GetForegroundWindow();
            // First measure the same current-chat anchor with the exact same source paused.
            engine.Configure(false, Smoove.Core.MotionProfile.Responsive, 1);
            var rawSamples = new List<(double Time, double Y)>();
            long rawStart = Stopwatch.GetTimestamp();
            int rawSent = 0;
            while (Stopwatch.GetElapsedTime(rawStart).TotalSeconds < 2)
            {
                double seconds = Stopwatch.GetElapsedTime(rawStart).TotalSeconds;
                if (!Native.GetCursorPos(out var cursor) || cursor.X != point.X || cursor.Y != point.Y ||
                    Native.GetForegroundWindow() != foreground || Native.ModifiersOrButtons())
                    throw new InvalidOperationException("Отрицательный контроль чата остановлен: контекст изменился");
                if (rawSent < 12 && seconds >= rawSent * 0.1)
                {
                    if (Native.SendInput(1, [Native.WheelInput(30, 0)], Marshal.SizeOf<Native.Input>()) != 1)
                        throw new InvalidOperationException("Raw wheel в чат не вставлен");
                    rawSent++;
                }
                rawSamples.Add((seconds, anchor.Current.BoundingRectangle.Top));
                await Task.Delay(8);
            }
            int rawChanges = rawSamples.Zip(rawSamples.Skip(1), (a, b) => Math.Abs(b.Y - a.Y) > 0.1 ? 1 : 0).Sum();
            double rawDisplacement = rawSamples[^1].Y - rawSamples[0].Y;
            File.WriteAllLines(report + ".raw-geometry.csv", new[] { "seconds,message_top" }.Concat(rawSamples.Select(p =>
                p.Time.ToString("G17", CultureInfo.InvariantCulture) + "," + p.Y.ToString("G17", CultureInfo.InvariantCulture))));
            engine.Configure(true, Smoove.Core.MotionProfile.Responsive, 1);
            long accepted = engine.Accepted, inputSum = engine.InputSum, outputSum = engine.OutputSum;
            var samples = new List<(double Time, double Y)>();
            long start = Stopwatch.GetTimestamp();
            int sent = 0;
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < 3)
            {
                double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                if (!Native.GetCursorPos(out var cursor) || cursor.X != point.X || cursor.Y != point.Y ||
                    Native.GetForegroundWindow() != foreground || Native.ModifiersOrButtons())
                    throw new InvalidOperationException($"Проверка чата остановлена: контекст изменился; cursor={cursor.X},{cursor.Y}/{point.X},{point.Y}; foreground={Native.GetForegroundWindow():X}/{foreground:X}; modifiers={Native.ModifiersOrButtons()}");
                if (sent < 12 && seconds >= sent * 0.1)
                {
                    if (Native.SendInput(1, [Native.WheelInput(30, 0)], Marshal.SizeOf<Native.Input>()) != 1)
                        throw new InvalidOperationException("Обычный wheel в Telegram не вставлен");
                    sent++;
                }
                samples.Add((seconds, anchor.Current.BoundingRectangle.Top));
                await Task.Delay(8);
            }
            File.WriteAllLines(report + ".geometry.csv", new[] { "seconds,message_top" }.Concat(samples.Select(p =>
                p.Time.ToString("G17", CultureInfo.InvariantCulture) + "," + p.Y.ToString("G17", CultureInfo.InvariantCulture))));
            int changes = samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b.Y - a.Y) > 0.1 ? 1 : 0).Sum();
            double displacement = samples[^1].Y - samples[0].Y;
            if (engine.Accepted - accepted != 12 || engine.InputSum - inputSum != 360 ||
                engine.OutputSum - outputSum != 360 || displacement < 20 || changes <= 20)
                throw new InvalidOperationException($"Чат не подтвердил непрерывное движение: changes={changes}, displacement={displacement:F1}px; {engine.Statistics}");
            return $"PASS: Telegram current conversation; normal zero-extra source; no messages sent; " +
                $"raw changes={rawChanges}, raw displacement={rawDisplacement:F1}px; " +
                $"smoothed changes={changes}, displacement={displacement:F1}px; samples={samples.Count}\n{engine.Statistics}";
        }
        finally
        {
            engine.AllowExternalTestWindow(0);
            Native.SetWindowPos(window, -2, 0, 0, 0, 0, 0x13);
            Native.GetCursorPos(out var cursor);
            if (cursor.X == point.X && cursor.Y == point.Y) Native.SetCursorPos(oldCursor.X, oldCursor.Y);
        }
    });
}
