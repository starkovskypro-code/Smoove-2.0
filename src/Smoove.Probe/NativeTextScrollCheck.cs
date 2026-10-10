using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Smoove.Core;

namespace Smoove.Probe;

internal static class NativeTextScrollCheck
{
    internal static Task<string> Run(WheelEngine engine, string report) => Task.Run(async () =>
    {
        using var process = Process.GetProcessesByName("Notepad").FirstOrDefault(p => p.MainWindowHandle != 0)
            ?? throw new InvalidOperationException("Откройте длинный документ в Блокноте.");
        nint window = process.MainWindowHandle;
        Native.GetCursorPos(out var oldCursor);
        nint oldForeground = Native.GetForegroundWindow();
        var samples = new List<string> { "seconds,percent" };
        ScrollPattern? scroll = null;
        double original = 0;
        try
        {
            uint thread = Native.GetCurrentThreadId();
            uint foregroundThread = Native.GetWindowThreadProcessId(oldForeground, out _);
            bool attached = thread != foregroundThread && Native.AttachThreadInput(thread, foregroundThread, true);
            try { Native.ShowWindow(window, 9); Native.SetWindowPos(window, -1, 0, 0, 0, 0, 0x03); Native.SetForegroundWindow(window); }
            finally { if (attached) Native.AttachThreadInput(thread, foregroundThread, false); }
            await Task.Delay(300);
            var root = AutomationElement.FromHandle(window);
            var children = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            AutomationElement? editor = null;
            foreach (AutomationElement element in children)
            {
                if (element.Current.ClassName.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) editor = element;
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var raw) && ((ScrollPattern)raw).Current.VerticallyScrollable)
                    scroll = (ScrollPattern)raw;
            }
            if (editor is null || scroll is null) throw new InvalidOperationException("RichEdit/ScrollPattern не найдены.");
            var thumbs=children.Cast<AutomationElement>().Where(e=>e.Current.ControlType==ControlType.Thumb).ToArray();
            var bars=children.Cast<AutomationElement>().Where(e=>e.Current.ControlType==ControlType.ScrollBar && e.TryGetCurrentPattern(RangeValuePattern.Pattern,out _))
                .Select(e=>(RangeValuePattern)e.GetCurrentPattern(RangeValuePattern.Pattern)).ToArray();
            File.WriteAllLines(report+".bars.txt",children.Cast<AutomationElement>().Where(e=>e.Current.ControlType==ControlType.Thumb || e.Current.ControlType==ControlType.ScrollBar)
                .Select(e=>{string range=e.TryGetCurrentPattern(RangeValuePattern.Pattern,out var rp)?$"range={((RangeValuePattern)rp).Current.Value}/{((RangeValuePattern)rp).Current.Maximum}; readonly={((RangeValuePattern)rp).Current.IsReadOnly}":"no range";return $"{e.Current.ControlType.ProgrammaticName}; {e.Current.ClassName}; {e.Current.AutomationId}; {e.Current.BoundingRectangle}; {range}";}));
            original = scroll.Current.VerticalScrollPercent;
            var bounds = editor.Current.BoundingRectangle;
            Native.SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            nint hit = Native.WindowFromPoint(new Native.Point { X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) });
            var target=PixelScrollTarget.TryCreate(hit);
            if (target is null)
                throw new InvalidOperationException($"Общий pixel adapter не найден: hit={Native.ClassName(hit)}, editor={editor.Current.ClassName}.");
            engine.AllowExternalTestWindow(window);
            engine.Configure(false, MotionProfile.Responsive, 1);
            await Task.Delay(700);
            double extent = bounds.Height * (100 / scroll.Current.VerticalViewSize - 1);
            double expected = 20 * Math.Max(96, Native.GetDpiForWindow((nint)editor.Current.NativeWindowHandle)) / 96.0 * Native.WheelScrollLines();
            void NotifyScroll()
            {
                nint handle=(nint)editor.Current.NativeWindowHandle;
                Native.PostMessageW(Native.GetParent(handle),0x0111,unchecked((nuint)((0x0602u<<16)|(uint)(Native.GetDlgCtrlID(handle)&0xFFFF))),handle);
            }
            async Task<(double Distance,int Changes,double MaxStep)> RawDistance(int count,int delta)
            {
                scroll.SetScrollPercent(-1,40);await Task.Delay(300);
                double before=scroll.Current.VerticalScrollPercent;
                double previous=before,maxStep=0;
                int changes=0;
                for(int i=0;i<count;i++)
                {
                    if(Native.GetForegroundWindow()!=window || Native.ModifiersOrButtons() || !Native.GetCursorPos(out var cursor) ||
                        PixelScrollTarget.TargetHandle(Native.WindowFromPoint(cursor))!=(nint)editor.Current.NativeWindowHandle)
                        throw new InvalidOperationException("Raw context changed.");
                    if(Native.SendInput(1,[Native.WheelInput(delta,0)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("Raw input failed.");
                    await Task.Delay(4);
                    double current=scroll.Current.VerticalScrollPercent;
                    if(current!=previous)changes++;
                    maxStep=Math.Max(maxStep,Math.Abs(current-previous)/100*extent);previous=current;
                }
                await Task.Delay(500);
                double final=scroll.Current.VerticalScrollPercent;
                if(final!=previous)changes++;
                maxStep=Math.Max(maxStep,Math.Abs(final-previous)/100*extent);
                return ((final-before)/100*extent,changes,maxStep);
            }
            var rawNotch=await RawDistance(1,-120);
            var rawFragments=await RawDistance(120,-1);
            engine.Configure(true,MotionProfile.Responsive,1);
            var distances = new List<double>();
            int minimumTextChanges=int.MaxValue;
            double largestTextStep=0;
            for (int notch = 0; notch < 5; notch++)
            {
                scroll.SetScrollPercent(-1, 40);
                NotifyScroll();
                await Task.Delay(300);
                long accepted = engine.Accepted, output = engine.OutputSum, cancellations = engine.Cancellations;
                double before = scroll.Current.VerticalScrollPercent;
                var thumbBefore=thumbs.Select(e=>e.Current.BoundingRectangle).ToArray();
                var barBefore=bars.Select(b=>b.Current.Value).ToArray();
                var text=(TextPattern)editor.GetCurrentPattern(TextPattern.Pattern);
                var anchor=text.GetVisibleRanges()[0].Clone();
                anchor.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,2000);
                anchor.MoveEndpointByRange(TextPatternRangeEndpoint.End,anchor,TextPatternRangeEndpoint.Start);
                anchor.ExpandToEnclosingUnit(TextUnit.Character);
                var rectangles=anchor.GetBoundingRectangles();
                if(rectangles.Length==0)throw new InvalidOperationException($"Text geometry unavailable: visible ranges={text.GetVisibleRanges().Length}, rects={rectangles.Length}");
                double initialY=rectangles[0].Top;
                long start = Stopwatch.GetTimestamp();
                if (Native.SendInput(1, [Native.WheelInput(-120, 0)], Marshal.SizeOf<Native.Input>()) != 1)
                    throw new InvalidOperationException("Input failed.");
                int changes = 0;
                int textChanges=0;
                double previousY=initialY;
                double previous = before, maxStep = 0;
                do
                {
                    if (!Native.GetCursorPos(out var cursor) || Native.GetForegroundWindow() != window || Native.ModifiersOrButtons() ||
                        PixelScrollTarget.TargetHandle(Native.WindowFromPoint(cursor))!=(nint)editor.Current.NativeWindowHandle)
                        throw new InvalidOperationException($"Контекст проверки изменился: foreground={Native.GetForegroundWindow():X}/{window:X}, cursor={cursor.X},{cursor.Y}, hit={Native.ClassName(Native.WindowFromPoint(cursor))}, keys={Native.ModifiersOrButtons()}");
                    double percent = scroll.Current.VerticalScrollPercent;
                    if (percent != previous) changes++;
                    maxStep = Math.Max(maxStep, Math.Abs(percent - previous) / 100 * extent);
                    previous = percent;
                    var currentRectangles=anchor.GetBoundingRectangles();
                    if(currentRectangles.Length==0)throw new InvalidOperationException("Text anchor left viewport.");
                    double y=currentRectangles[0].Top;
                    if(Math.Abs(y-previousY)>0.1)textChanges++;
                    largestTextStep=Math.Max(largestTextStep,Math.Abs(y-previousY));
                    previousY=y;
                    samples.Add(FormattableString.Invariant($"{Stopwatch.GetElapsedTime(start).TotalSeconds:G17},{percent:G17}"));
                    await Task.Delay(10);
                } while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1.8 || engine.IsBusy);
                double distance = (previous - before) / 100 * extent;
                var finalRectangles=anchor.GetBoundingRectangles();
                if(finalRectangles.Length==0 || Math.Abs(initialY-finalRectangles[0].Top-expected)>3)
                    throw new InvalidOperationException($"Provider position did not move actual text: before={initialY}, after={(finalRectangles.Length==0?double.NaN:finalRectangles[0].Top)}, expected={expected}");
                distances.Add(distance);
                if(bars.Where((b,i)=>Math.Abs(b.Current.Value-barBefore[i])<0.1).Any())
                    throw new InvalidOperationException("Visible scrollbar did not move: "+string.Join("; ",bars.Select((b,i)=>$"{barBefore[i]}->{b.Current.Value}")));
                if(thumbs.Length==0 || !thumbs.Where((e,i)=>Math.Abs(e.Current.BoundingRectangle.Top-thumbBefore[i].Top)>0.1).Any())
                    throw new InvalidOperationException($"Text moved but scrollbar thumb did not: thumbs={thumbs.Length}; "+string.Join("; ",thumbs.Select(e=>$"{e.Current.BoundingRectangle}")));
                minimumTextChanges=Math.Min(minimumTextChanges,textChanges);
                if (engine.Accepted - accepted != 1 || engine.OutputSum - output != -120 || engine.Cancellations != cancellations ||
                    Math.Abs(distance - expected) > 2 || changes < 10 || textChanges<10 || maxStep >= expected / 2 || largestTextStep>=expected/2)
                    throw new InvalidOperationException($"Одиночный щелчок {notch}: {distance:F2}/{expected:F2}px, changes={changes}, maxStep={maxStep:F2}px; {engine.Statistics}");
            }
            File.WriteAllLines(report + ".csv", samples);
            return $"PASS: shared RichEdit pixel path; 5 single notches: {string.Join(", ", distances.Select(d => $"{d:F2}px"))}; expected={expected:F2}px; no lost/duplicate input or cancellation; both scrollbar values and thumb synchronized\n"+
                $"Actual text: at least {minimumTextChanges} intermediate positions per notch; max step={largestTextStep:F2}px. Raw control: 1x120={rawNotch}; 120x1={rawFragments}.\n{engine.Statistics}";
        }
        finally
        {
            engine.Cancel(); engine.AllowExternalTestWindow(0);
            try
            {
                scroll?.SetScrollPercent(-1, original);
                nint editHandle=0;
                Native.EnumChildWindows(window,(child,_)=>{if(Native.ClassName(child).StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase))editHandle=child;return editHandle==0;},0);
                if(editHandle!=0)Native.PostMessageW(Native.GetParent(editHandle),0x0111,unchecked((nuint)((0x0602u<<16)|(uint)(Native.GetDlgCtrlID(editHandle)&0xFFFF))),editHandle);
            }
            finally { Native.SetWindowPos(window, -2, 0, 0, 0, 0, 0x13); Native.SetCursorPos(oldCursor.X, oldCursor.Y); Native.SetForegroundWindow(oldForeground); }
        }
    });
}
