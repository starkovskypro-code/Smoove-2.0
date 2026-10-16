using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Smoove.Probe;
using Smoove.Core;

namespace Smoove.Integration;

internal static class ReaperCheck
{
    internal static void Describe(string output)
    {
        var rows=new List<string>();
        Native.EnumWindows((window,_)=>
        {
            Native.GetWindowThreadProcessId(window,out uint pid);
            using var process=Process.GetProcessById((int)pid);
            if(!process.ProcessName.Equals("Reaper",StringComparison.OrdinalIgnoreCase))return true;
            rows.Add($"{window:X}; top; {Native.ClassName(window)}; {Native.WindowTitle(window)}");
            Native.EnumChildWindows(window,(child,_)=>
            {
                Native.GetWindowRect(child,out var bounds);
                rows.Add($"{child:X}; child; {Native.GetDlgCtrlID(child)}; {Native.ClassName(child)}; {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
                return true;
            },0);
            return true;
        },0);
        File.WriteAllLines(output,rows);
    }

    internal static void DescribeMidiScrollPatterns(string output)
    {
        var rows=new List<string>();
        Native.EnumWindows((window,_)=>
        {
            if(!Native.WindowTitle(window).StartsWith("MIDI take:",StringComparison.Ordinal))return true;
            try
            {
                var root=AutomationElement.FromHandle(window);
                rows.Add($"Window: {Native.WindowTitle(window)}; class={Native.ClassName(window)}; scroll={root.GetCurrentPropertyValue(AutomationElement.IsScrollPatternAvailableProperty)}");
                var elements=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty,true));
                for(int i=0;i<elements.Count;i++)
                {
                    var item=elements[i];
                    object raw;
                    if(!item.TryGetCurrentPattern(ScrollPattern.Pattern,out raw))continue;
                    var state=((ScrollPattern)raw).Current;
                    var bounds=item.Current.BoundingRectangle;
                    rows.Add($"name={item.Current.Name}; class={item.Current.ClassName}; id={item.Current.AutomationId}; hwnd={item.Current.NativeWindowHandle:X}; rect={bounds}; vertical={state.VerticallyScrollable}/{state.VerticalScrollPercent}/{state.VerticalViewSize}; horizontal={state.HorizontallyScrollable}/{state.HorizontalScrollPercent}/{state.HorizontalViewSize}");
                }
            }
            catch(Exception error){rows.Add(error.GetType().Name+": "+error.Message);}
            return true;
        },0);
        File.WriteAllLines(output,rows);
    }

    internal static void Run(string output,string mode="smooth")
    {
        var scenario=mode.Split(':');
        bool midi=scenario.Last()=="midi";
        bool nativeHorizontal=scenario.Last()=="native";
        bool horizontal=scenario.Contains("shift") || nativeHorizontal;
        bool isolated=scenario[0] is "smoove" or "smoothie";
        if(!isolated && scenario[0] is not ("smooth" or "raw"))throw new ArgumentException("Expected smooth, raw, smoove or smoothie.");
        int count=isolated && scenario.Length>2 ? int.Parse(scenario[2],CultureInfo.InvariantCulture) : 1;
        int requested=isolated && scenario.Length>1 ? int.Parse(scenario[1],CultureInfo.InvariantCulture) : -120;
        int zoom=scenario.Length>4 ? int.Parse(scenario[3],CultureInfo.InvariantCulture) : 0;
        if(zoom is <-20 or >20)throw new ArgumentOutOfRangeException(nameof(mode));
        if(count is <1 or >3 || requested is not (-120 or 120))throw new ArgumentException("Expected one to three notches of +/-120.");
        if(isolated && Process.GetProcessesByName("Smoove.Settings").Length!=0)throw new InvalidOperationException("Close the settings app before isolated engine checks.");
        using var engine=isolated ? new WheelEngine(true) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var candidates=new List<nint>();
        Native.EnumWindows((handle,_)=>
        {
            string title=Native.WindowTitle(handle);
            if(Native.IsWindowVisible(handle) && (midi ? title.StartsWith("MIDI take:",StringComparison.Ordinal) : title.StartsWith("[unsaved project] - REAPER",StringComparison.Ordinal)))
            {
                Native.GetWindowThreadProcessId(handle,out uint owner);
                using var process=Process.GetProcessById(checked((int)owner));
                if(process.ProcessName.Equals("Reaper",StringComparison.OrdinalIgnoreCase))candidates.Add(handle);
            }
            return true;
        },0);
        if(candidates.Count!=1)throw new InvalidOperationException($"Expected one unsaved REAPER window, found {candidates.Count}.");
        nint window=candidates.Single(),view=0,tcp=0;
        Native.EnumChildWindows(midi ? window : window,(child,_)=>
        {
            if(Native.ClassName(child)==(midi ? "MIDIWindow" : "REAPERTrackListWindow"))view=child;
            if(Native.ClassName(child)==(midi ? "MIDIPianoWindow" : "REAPERTCPDisplay"))tcp=child;
            return true;
        },0);
        if(view==0 || tcp==0)throw new InvalidOperationException("Missing REAPER trackview/TCP.");
        Native.GetWindowThreadProcessId(window,out uint pid);
        Native.GetCursorPos(out var oldCursor);
        nint oldForeground=Native.GetForegroundWindow();
        var clock=Stopwatch.StartNew();
        var messages=new ConcurrentQueue<string>();
        Native.HookProc callback=(code,message,data)=>
        {
            if(code>=0 && (int)message is Native.Wheel or Native.HWheel)
            {
                var mouse=Marshal.PtrToStructure<Native.MouseHook>(data);
                messages.Enqueue(FormattableString.Invariant($"{clock.Elapsed.TotalMilliseconds:F3},{unchecked((short)(mouse.Data>>16))},{mouse.Extra:X},{Native.GetForegroundWindow():X},{Native.WindowFromPoint(mouse.Point):X}"));
            }
            return Native.CallNextHookEx(0,code,message,data);
        };
        nint hook=0;
        uint observerId=0;
        using var ready=new ManualResetEventSlim();
        var observer=new Thread(()=>
        {
            observerId=Native.GetCurrentThreadId();
            Native.PeekMessageW(out _,0,0,0,0);
            hook=Native.SetWindowsHookExW(14,callback,Native.GetModuleHandleW(null),0);
            ready.Set();
            if(hook==0)return;
            try{while(Native.GetMessageW(out _,0,0,0)>0){}}
            finally{Native.UnhookWindowsHookEx(hook);}
        }){IsBackground=true,Name="REAPER wheel observer"};
        observer.Start();
        if(!ready.Wait(TimeSpan.FromSeconds(3)) || hook==0)throw new InvalidOperationException("Wheel observer hook failed.");
        string frames=output+".frames";
        bool shiftHeld=false;
        int appliedZoom=0;
        Directory.CreateDirectory(frames);
        try
        {
            Native.ShowWindow(window,9);
            for(int attempt=0;attempt<10 && Native.GetForegroundWindow()!=window;attempt++)
            {
                uint thread=Native.GetCurrentThreadId();
                uint foregroundThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
                bool attached=thread!=foregroundThread && Native.AttachThreadInput(thread,foregroundThread,true);
                try{Native.SetForegroundWindow(window);}
                finally{if(attached)Native.AttachThreadInput(thread,foregroundThread,false);}
                Thread.Sleep(30);
            }
            if(Native.GetForegroundWindow()!=window)throw new InvalidOperationException($"REAPER did not acquire foreground: {Native.GetForegroundWindow():X}, {Native.ClassName(Native.GetForegroundWindow())}, {Native.WindowTitle(Native.GetForegroundWindow())}.");
            for(int z=0;z<Math.Abs(zoom);z++)
            {
                Native.PostMessageW(window,0x0111,(nuint)(horizontal ? (zoom>0 ? 1012 : 1011) : (zoom>0 ? 40111 : 40112)),0);
                appliedZoom+=Math.Sign(zoom);Thread.Sleep(40);
            }
            if(!Native.GetWindowRect(view,out var bounds) || !Native.GetWindowRect(tcp,out var panel))throw new InvalidOperationException("Missing track bounds.");
            var point=new Native.Point{X=bounds.Left+Math.Min(250,(bounds.Right-bounds.Left)/2),Y=bounds.Top+(bounds.Bottom-bounds.Top)/2};
            Native.SetCursorPos(point.X,point.Y);
            void Pump(int ms){long until=clock.ElapsedMilliseconds+ms;while(clock.ElapsedMilliseconds<until){Application.DoEvents();Thread.Sleep(1);}}
            if(engine is not null)
            {
                engine.AllowExternalTestWindow(Native.GetAncestor(window,2));
                engine.Configure(true,new(0.01,1),2,outputHz:0,animationTempo:6,
                    engine:scenario[0]=="smoove" ? MotionEngineKind.Smoove : MotionEngineKind.SmoothieExperimental,experimental:new(midi ? 1.8 : 2,200,0,0));
                engine.RefreshRoute(true);
            }
            Pump(400);
            if(engine is not null)
            {
                string route=engine.RefreshRoute(true);
                if(!route.StartsWith("Разрешён",StringComparison.Ordinal))throw new InvalidOperationException("MIDI route: "+route);
            }
            if(Native.GetAncestor(Native.WindowFromPoint(point),2)!=window)throw new InvalidOperationException("Cursor is not over unsaved REAPER.");
            var crop=new Rectangle(panel.Left+1,Math.Max(panel.Top,bounds.Top)+10,Math.Min(140,panel.Right-panel.Left-2),Math.Min(650,bounds.Bottom-Math.Max(panel.Top,bounds.Top)-20));
            if(midi)crop=new Rectangle(bounds.Left+200,bounds.Top+140,
                Math.Min(800,bounds.Right-bounds.Left-210),Math.Min(500,bounds.Bottom-bounds.Top-150));
            else if(horizontal)crop=new Rectangle(bounds.Left+15,bounds.Top-40,650,80);
            using var bitmap=new Bitmap(crop.Width,crop.Height);
            using var graphics=Graphics.FromImage(bitmap);
            var rows=new List<string>{"milliseconds,frame"};
            void Capture(int i)
            {
                graphics.CopyFromScreen(crop.Location,Point.Empty,crop.Size);
                string name=i.ToString("D4",CultureInfo.InvariantCulture)+".png";
                bitmap.Save(Path.Combine(frames,name));
                rows.Add(FormattableString.Invariant($"{clock.Elapsed.TotalMilliseconds:F3},{name}"));
            }
            Capture(0);
            if(horizontal && !nativeHorizontal)
            {
                if(Native.SendInput(1,[Native.ShiftInput(true)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("Shift press failed.");
                shiftHeld=true;Pump(30);
            }
            double injection=clock.Elapsed.TotalMilliseconds;
            int delta=scenario[0]=="raw" && scenario.Length>1 ? int.Parse(scenario[1],CultureInfo.InvariantCulture) : requested;
            if(Native.SendInput(1,[Native.WheelInput(delta,mode.StartsWith("raw",StringComparison.Ordinal) ? 0x52454150u : 0,nativeHorizontal)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("SendInput failed.");
            int added=1;
            for(int i=1;i<=(mode=="smooth" || isolated ? 100 : 15);i++)
            {
                Pump(12);
                if(shiftHeld && clock.Elapsed.TotalMilliseconds-injection>=100)
                {
                    Native.SendInput(1,[Native.ShiftInput(false)],Marshal.SizeOf<Native.Input>());shiftHeld=false;
                }
                if(isolated && added<count && clock.Elapsed.TotalMilliseconds-injection>=added*50)
                {
                    if(Native.SendInput(1,[Native.WheelInput(delta,0)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("Burst SendInput failed.");
                    added++;
                }
                if(Native.GetForegroundWindow()!=window)throw new InvalidOperationException("Foreground changed during capture.");
                Capture(i);
            }
            File.WriteAllLines(output,rows);
            File.WriteAllText(output+".target.txt",$"pid={pid}; hwnd={window:X}; title={Native.WindowTitle(window)}\nmode={mode}; horizontal={horizontal}; injectionMs={injection.ToString("R",CultureInfo.InvariantCulture)}; crop={crop}\n"+(engine is null ? "" : engine.Statistics));
            if(mode=="smooth" && !messages.Any(line=>line.Contains($",{Native.OwnMarker:X},",StringComparison.Ordinal)))
                throw new InvalidOperationException("No Smoove output observed; this run cannot verify smoothing.");
            bool lostDistance=engine is not null && (midi
                ? engine.OutputSum*requested<=0 || Math.Abs(engine.OutputSum)<count*12 || Math.Abs(engine.OutputSum)>count*72
                : engine.OutputSum!=requested*count*2);
            if(engine is not null && (engine.Failure is not null || engine.IsBusy || engine.Accepted!=count || lostDistance))
                throw new InvalidOperationException("Isolated engine lost input or did not settle: "+engine.Statistics+"; worker="+engine.LastWorkerFilter);
        }
        finally
        {
            if(shiftHeld)Native.SendInput(1,[Native.ShiftInput(false)],Marshal.SizeOf<Native.Input>());
            for(int z=0;z<Math.Abs(appliedZoom);z++)
                Native.PostMessageW(window,0x0111,(nuint)(horizontal ? (appliedZoom>0 ? 1011 : 1012) : (appliedZoom>0 ? 40112 : 40111)),0);
            Native.PostThreadMessageW(observerId,Native.Quit,0,0);
            observer.Join(TimeSpan.FromSeconds(3));GC.KeepAlive(callback);
            File.WriteAllLines(output+".wheel.csv",new[]{"milliseconds,delta,extra,foreground,hit"}.Concat(messages));
            Native.SetCursorPos(oldCursor.X,oldCursor.Y);
            if(oldForeground!=0)Native.SetForegroundWindow(oldForeground);
        }
        int reverseFrames=Analyze(output,isolated && !horizontal && !midi);
        if(isolated)
        {
            if(reverseFrames!=0)throw new InvalidOperationException("REAPER rebound remains in the captured trajectory.");
            File.WriteAllText(output+".pass.txt","PASS: unsaved REAPER, "+mode+(horizontal
                ? "; complete engine wheel distance; Shift released at 100ms; horizontal capture recorded (image matching has redraw/periodic-grid limits)."
                : midi ? "; reduced MIDI distance; complete engine distance; no exact-match reverse frames."
                : "; complete wheel distance; safe packet quantum; no exact-match reverse frames."));
        }
    }
    // shortcut: matches motion within 160 pixels/frame; increase capture rate for faster gestures.
    internal static int Analyze(string output,bool requireSmooth=false)
    {
        string frames=output+".frames";
        var rows=File.ReadAllLines(output);
        bool horizontal=File.ReadAllText(output+".target.txt").Contains("horizontal=True",StringComparison.Ordinal);
        var shifts=new List<string>{"milliseconds,contentShiftY,meanError"};
        double[] Signature(string path)
        {
            using var frame=new Bitmap(path);
            return Enumerable.Range(0,horizontal ? frame.Width : frame.Height).Select(y=>
            {
                double value=0;
                for(int x=horizontal ? 45 : 0;x<(horizontal ? frame.Height : frame.Width);x++){var color=horizontal ? frame.GetPixel(y,x) : frame.GetPixel(x,y);value+=color.R+color.G+color.B;}
                return value;
            }).ToArray();
        }
        var previous=Signature(Path.Combine(frames,"0000.png"));
        var baseline=previous;
        for(int i=1;i<rows.Length-1;i++)
        {
            var next=Signature(Path.Combine(frames,i.ToString("D4",CultureInfo.InvariantCulture)+".png"));
            var best=Enumerable.Range(-160,321).Select(dy=>(Shift:dy,Error:Enumerable.Range(160,previous.Length-320).Average(y=>Math.Abs(previous[y]-next[y+dy]))))
                .OrderBy(p=>p.Error).ThenBy(p=>Math.Abs(p.Shift)).First();
            shifts.Add(FormattableString.Invariant($"{rows[i+1].Split(',')[0]},{best.Shift},{best.Error:F3}"));
            previous=next;
        }
        File.WriteAllLines(output+".shifts.csv",shifts);
        int netRange=Math.Min(300,(baseline.Length-1)/2);
        var net=Enumerable.Range(-netRange,netRange*2+1).Select(dy=>(Shift:dy,Error:Enumerable.Range(netRange,baseline.Length-netRange*2).Average(y=>Math.Abs(baseline[y]-previous[y+dy]))))
            .OrderBy(p=>p.Error).ThenBy(p=>Math.Abs(p.Shift)).First();
        var wheel=File.ReadAllLines(output+".wheel.csv").Skip(1).Select(line=>line.Split(',')).ToArray();
        var own=wheel.Where(parts=>parts[2]==$"{Native.OwnMarker:X}").ToArray();
        int expectedDirection=Math.Sign(wheel.Sum(p=>int.Parse(p[1],CultureInfo.InvariantCulture)));
        var steps=shifts.Skip(1).Select(line=>line.Split(',')).ToArray();
        bool moving=false;
        int reversals=0;
        foreach(var step in steps)
        {
            int dy=int.Parse(step[1],CultureInfo.InvariantCulture);
            if(double.Parse(step[2],CultureInfo.InvariantCulture)>0.001)continue;
            if(dy*expectedDirection>0)moving=true;
            else if(dy*expectedDirection<0 && moving)reversals++;
        }
        File.WriteAllText(output+".analysis.txt",File.ReadAllText(output+".target.txt")+
            $"Smoove messages={own.Length}; sum={own.Sum(p=>int.Parse(p[1],CultureInfo.InvariantCulture))}; positive={own.Count(p=>int.Parse(p[1],CultureInfo.InvariantCulture)>0)}\n"+
            $"Exact-match reverse frames after scrolling={reversals}; one-unit wheel messages={own.Count(p=>Math.Abs(int.Parse(p[1],CultureInfo.InvariantCulture))==1)}\n"+
            FormattableString.Invariant($"NetContentShiftY={net.Shift}; meanError={net.Error:F3}\n"));
        if(requireSmooth)
        {
            var tail=steps.Where(p=>double.Parse(p[2],CultureInfo.InvariantCulture)==0 && p[1]!="0").TakeLast(2).ToArray();
            int matchedTravel=steps.Where(p=>double.Parse(p[2],CultureInfo.InvariantCulture)==0).Sum(p=>int.Parse(p[1],CultureInfo.InvariantCulture));
            if(tail.Length!=2 || matchedTravel*expectedDirection<=0)
                throw new InvalidOperationException("Trajectory could not be verified from stable track landmarks.");
            if(own.Length==0 || own.Any(p=>Math.Abs(int.Parse(p[1],CultureInfo.InvariantCulture))<4))
                throw new InvalidOperationException("REAPER output or rendered ending still contains a jump.");
        }
        return reversals;
    }
}
