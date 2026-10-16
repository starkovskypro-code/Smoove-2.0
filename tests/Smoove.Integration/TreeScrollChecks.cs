using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Automation;
using Smoove.Core;
using Smoove.Probe;

namespace Smoove.Integration;

internal static class TreeScrollChecks
{
    internal static void Run(string report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        nint tree=0;
        Native.EnumWindows((root,_)=>
        {
            if(Native.ClassName(root)!="CabinetWClass" || !Native.IsWindowVisible(root))return true;
            Native.EnumChildWindows(root,(child,_)=>
            {
                if(Native.ClassName(child)=="SysTreeView32" && Native.IsWindowVisible(child))tree=child;
                return tree==0;
            },0);
            return tree==0;
        },0);
        if(tree==0)throw new InvalidOperationException("Open Explorer with a scrollable navigation tree.");
        var view=AutomationElement.FromHandle(tree);
        var scroll=(ScrollPattern)view.GetCurrentPattern(ScrollPattern.Pattern);
        double original=scroll.Current.VerticalScrollPercent;
        if(!scroll.Current.VerticallyScrollable)throw new InvalidOperationException("Expand folders in the navigation tree first.");
        var results=new List<string>();
        Native.GetCursorPos(out var oldCursor);
        nint foreground=Native.GetForegroundWindow();
        using var engine=new WheelEngine(true);
        try
        {
            var bounds=view.Current.BoundingRectangle;
            nint point=unchecked((nint)(((uint)(ushort)(bounds.Top+bounds.Height/2)<<16)|(ushort)(bounds.Left+bounds.Width/2)));
            double Baseline()
            {
                scroll.SetScrollPercent(-1,40);Thread.Sleep(150);
                return scroll.Current.VerticalScrollPercent;
            }
            double start=Baseline();
            Native.PostMessageW(tree,Native.Wheel,unchecked((nuint)((uint)(ushort)-120<<16)),point);
            Thread.Sleep(200);
            double raw=scroll.Current.VerticalScrollPercent-start;
            start=Baseline();
            var wheelPositions=new HashSet<double>();
            for(int i=0;i<120;i++)
            {
                Native.PostMessageW(tree,Native.Wheel,unchecked((nuint)((uint)(ushort)-1<<16)),point);
                Thread.Sleep(3);
                wheelPositions.Add(scroll.Current.VerticalScrollPercent);
            }
            Thread.Sleep(150);
            double small=scroll.Current.VerticalScrollPercent-start;
            start=Baseline();
            var positions=new HashSet<double>();
            for(int i=1;i<=120;i++)
            {
                scroll.SetScrollPercent(-1,start+i*0.01);
                positions.Add(scroll.Current.VerticalScrollPercent);
            }
            results.Add($"class={view.Current.ClassName}; viewSize={scroll.Current.VerticalViewSize:F3}%; raw notch={raw:F4}%; 120 small wheel messages={small:F4}%/{wheelPositions.Count} positions; 120 small provider positions={positions.Count}");
            start=Baseline();
            var adapter=PixelScrollTarget.TryCreate(tree) ?? throw new InvalidOperationException("Missing Explorer tree adapter.");
            var info=new Native.ScrollInfo{Size=(uint)Marshal.SizeOf<Native.ScrollInfo>(),Mask=7};
            Require(Native.GetScrollInfo(tree,1,ref info),"Missing tree scroll range.");
            double rowPercent=100.0/(info.Max-info.Min-Math.Max(0,(long)info.Page-1));
            positions.Clear();
            for(int i=0;i<120;i++){adapter.Move(-1);positions.Add(scroll.Current.VerticalScrollPercent);}
            Require(Math.Abs(scroll.Current.VerticalScrollPercent-start-raw)<rowPercent/2,"Adapter changed native notch distance.");
            Require(positions.Count>2,"Adapter still emits one notch-sized jump.");
            results.Add($"Adapter: {positions.Count} positions/notch; native row={rowPercent:F4}%");
            adapter.BeginGesture();adapter.Move(120);
            Require(Math.Abs(scroll.Current.VerticalScrollPercent-start)<rowPercent/2,"Reverse movement lost distance.");
            foreach(double edge in new[]{0.0,100.0})
            {
                scroll.SetScrollPercent(-1,edge);adapter.BeginGesture();adapter.Move(edge==0?120:-120);
                Require(Math.Abs(scroll.Current.VerticalScrollPercent-edge)<rowPercent/2,"Adapter overshot a tree boundary.");
            }

            nint root=Native.GetAncestor(tree,2);
            uint thread=Native.GetCurrentThreadId();
            uint foregroundThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
            bool attached=thread!=foregroundThread && Native.AttachThreadInput(thread,foregroundThread,true);
            try { Native.ShowWindow(root,9);Native.SetForegroundWindow(root); }
            finally {if(attached)Native.AttachThreadInput(thread,foregroundThread,false);}
            Native.SetCursorPos((int)(bounds.Left+bounds.Width/2),(int)(bounds.Top+bounds.Height/2));
            Require(Native.GetForegroundWindow()==root,"Explorer did not acquire foreground.");
            engine.AllowExternalTestWindow(root);
            foreach(var kind in new[]{MotionEngineKind.Smoove,MotionEngineKind.SmoothieExperimental})
            foreach(int direction in new[]{-1,1})
            foreach(int count in new[]{1,3})
            {
                engine.Cancel();engine.Configure(false,MotionProfile.Responsive,1);
                start=Baseline();
                engine.Configure(true,MotionProfile.Responsive,1,outputHz:120,engine:kind,experimental:new(1,80,0,0));
                engine.RefreshRoute(true);Thread.Sleep(200);
                long accepted=engine.Accepted;
                var samples=new List<(double Time,double Percent)>{(0,start)};
                long began=Stopwatch.GetTimestamp();int sent=0;
                while(Stopwatch.GetElapsedTime(began).TotalSeconds<2)
                {
                    double elapsed=Stopwatch.GetElapsedTime(began).TotalSeconds;
                    Require(Native.GetForegroundWindow()==root,"Foreground changed during the wheel check.");
                    if(sent<count && elapsed>=sent*0.04)
                    {
                        Require(Native.SendInput(1,[Native.WheelInput(direction*120,0)],Marshal.SizeOf<Native.Input>())==1,"Input injection failed.");
                        sent++;
                    }
                    samples.Add((elapsed,scroll.Current.VerticalScrollPercent));Thread.Sleep(8);
                }
                Require(!engine.IsBusy && engine.Failure is null,"Engine failed or did not stop: "+engine.Statistics);
                Require(engine.Accepted-accepted==count,"Wheel did not reach the tested engine: "+engine.Statistics);
                var steps=samples.Zip(samples.Skip(1),(a,b)=>Math.Abs(b.Percent-a.Percent)).ToArray();
                double distance=Math.Abs(samples[^1].Percent-start);
                string name=$"{kind}-{direction}-{count}";
                File.WriteAllLines(report+"."+name+".csv",new[]{"seconds,scroll_percent"}.Concat(samples.Select(p=>FormattableString.Invariant($"{p.Time:R},{p.Percent:R}"))));
                Require(Math.Abs(distance-Math.Abs(raw)*count)<rowPercent/2,$"Distance changed: {name}: {distance}/{Math.Abs(raw)*count}");
                Require(steps.Count(s=>s>rowPercent/2)>2,$"No intermediate row positions: {name}");
                Require(steps.Max()<Math.Abs(raw)*0.75,$"Notch-sized jump remains: {name}: {steps.Max()}/{raw}");
                double lastChange=samples.Zip(samples.Skip(1),(a,b)=>(b.Time,Changed:Math.Abs(b.Percent-a.Percent)>rowPercent/2)).Last(p=>p.Changed).Time;
                results.Add($"{name}: {steps.Count(s=>s>rowPercent/2)} row changes; distance={distance:F4}%; max sampled step={steps.Max()/rowPercent:F1} rows; last change at {lastChange:F3}s");
            }
            File.WriteAllLines(report,new[]{"PASS: actual Explorer navigation tree; both engines, single/burst, both directions, native distance, intermediate rows. Synthetic input; TreeView remains row-quantized."}.Concat(results));
        }
        finally
        {
            engine.Configure(false,MotionProfile.Responsive,1);engine.Cancel();
            scroll.SetScrollPercent(-1,original);
            Native.SetCursorPos(oldCursor.X,oldCursor.Y);
            if(foreground!=0)Native.SetForegroundWindow(foreground);
        }
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
