using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Smoove.Probe;

internal static class ExplorerScrollCheck
{
    internal static Task<string> Run(WheelEngine engine,string report)=>Task.Run(async()=>
    {
        Native.GetCursorPos(out var oldCursor);
        nint viewHandle=0;
        Native.EnumWindows((root,_)=>
        {
            if(Native.ClassName(root)!="CabinetWClass" || !Native.IsWindowVisible(root))return true;
            Native.EnumChildWindows(root,(child,_)=>
            {
                if(Native.IsWindowVisible(child)&&Native.ClassName(child)=="DirectUIHWND"&&Native.ClassName(Native.GetParent(child))=="SHELLDLL_DefView")viewHandle=child;
                return viewHandle==0;
            },0);
            return viewHandle==0;
        },0);
        if(Native.ClassName(viewHandle)!="DirectUIHWND" || Native.ClassName(Native.GetParent(viewHandle))!="SHELLDLL_DefView")
            throw new InvalidOperationException($"Поместите курсор в список файлов Проводника: point={oldCursor.X},{oldCursor.Y}; hit={viewHandle:X}/{Native.ClassName(viewHandle)}; parent={Native.ClassName(Native.GetParent(viewHandle))}");
        nint window=Native.GetAncestor(viewHandle,2);
        void ActivateWindow()
        {
            uint thread=Native.GetCurrentThreadId();
            uint foregroundThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
            bool attached=thread!=foregroundThread && Native.AttachThreadInput(thread,foregroundThread,true);
            try{Native.ShowWindow(window,9);Native.SetWindowPos(window,-1,0,0,0,0,0x03);Native.SetForegroundWindow(window);}
            finally{if(attached)Native.AttachThreadInput(thread,foregroundThread,false);}
        }
        ActivateWindow();
        await Task.Delay(300);
        var view=AutomationElement.FromHandle(viewHandle);
        var scroll=(ScrollPattern)view.GetCurrentPattern(ScrollPattern.Pattern);
        bool horizontalView=!scroll.Current.VerticallyScrollable && scroll.Current.HorizontallyScrollable;
        double original=horizontalView?scroll.Current.HorizontalScrollPercent:scroll.Current.VerticalScrollPercent;
        var rows=view.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
        var bounds=view.Current.BoundingRectangle;
        var point=new Native.Point{X=(int)(bounds.Left+bounds.Width/2),Y=(int)(bounds.Top+bounds.Height/2)};
        Native.SetCursorPos(point.X,point.Y);await Task.Delay(150);
        ActivateWindow();
        await Task.Delay(150);
        nint foreground=Native.GetForegroundWindow();
        if(foreground!=window)throw new InvalidOperationException($"Не удалось активировать Проводник: target={window:X}/{Native.ClassName(window)}/{Native.WindowTitle(window)}, foreground={foreground:X}/{Native.ClassName(foreground)}, view={viewHandle:X}, bounds={bounds}");
        var anchor=rows.Cast<AutomationElement>().FirstOrDefault(e=>e.Current.BoundingRectangle.Top>bounds.Top+bounds.Height/2 && e.Current.BoundingRectangle.Bottom<bounds.Bottom-100);
        if(anchor is null)
        {
            Native.SetWindowPos(window,-2,0,0,0,0,0x13);Native.SetCursorPos(oldCursor.X,oldCursor.Y);
            throw new InvalidOperationException($"Нет видимой строки: window={Native.WindowTitle(window)}, bounds={bounds}, rows={rows.Count}; "+string.Join("; ",rows.Cast<AutomationElement>().Take(4).Select(e=>$"{e.Current.Name}: {e.Current.BoundingRectangle}")));
        }
        var anchorName=anchor.Current.Name;
        double AnchorY()
        {
            var item=view.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.NameProperty,anchorName),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)))
                ?? throw new InvalidOperationException("Строка проверки вышла из видимой области.");
            return horizontalView?item.Current.BoundingRectangle.Left:item.Current.BoundingRectangle.Top;
        }
        int direction=original<50?-1:1;
        async Task<(int Changes,double Distance,double MaxStep)> Series(bool enabled,string name)
        {
            scroll.SetScrollPercent(horizontalView?original:-1,horizontalView?-1:original);
            engine.Configure(enabled,Smoove.Core.MotionProfile.Responsive,1);
            await Task.Delay(600);
            var samples=new List<(double Time,double Y)>();
            long start=Stopwatch.GetTimestamp();int sent=0;
            while(Stopwatch.GetElapsedTime(start).TotalSeconds<2.8 || sent<12)
            {
                if(!Native.GetCursorPos(out var cursor)||cursor.X!=point.X||cursor.Y!=point.Y||Native.GetForegroundWindow()!=foreground||PixelScrollTarget.TargetHandle(Native.WindowFromPoint(cursor))!=viewHandle||Native.ModifiersOrButtons())
                    throw new InvalidOperationException($"Контекст Проводника изменился: cursor={cursor.X},{cursor.Y}/{point.X},{point.Y}, foreground={Native.GetForegroundWindow():X}/{foreground:X}, hit={Native.WindowFromPoint(cursor):X}/{Native.ClassName(Native.WindowFromPoint(cursor))}, expected={viewHandle:X}, modifiers={Native.ModifiersOrButtons()}");
                double time=Stopwatch.GetElapsedTime(start).TotalSeconds;
                if(sent<12 && time>=sent*0.1)
                {
                    if(Native.SendInput(1,[Native.WheelInput(direction*30,0)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("Input failed");
                    sent++;
                }
                samples.Add((time,AnchorY()));await Task.Delay(8);
            }
            for(int i=0;i<100 && engine.IsBusy;i++)await Task.Delay(30);
            samples.Add((Stopwatch.GetElapsedTime(start).TotalSeconds,AnchorY()));
            File.WriteAllLines(report+"."+name+".csv",new[]{"seconds,row_top"}.Concat(samples.Select(s=>FormattableString.Invariant($"{s.Time:G17},{s.Y:G17}"))));
            var differences=samples.Zip(samples.Skip(1),(a,b)=>Math.Abs(b.Y-a.Y)).ToArray();
            return(differences.Count(d=>d>0.1),Math.Abs(samples[^1].Y-samples[0].Y),differences.Max());
        }
        try
        {
            engine.AllowExternalTestWindow(window);await Task.Delay(350);
            var raw=await Series(false,"raw");long accepted=engine.Accepted;
            var smooth=await Series(true,"smooth");
            if(engine.Accepted-accepted!=12 || smooth.Changes<=raw.Changes*3 || smooth.MaxStep>=raw.MaxStep || smooth.Distance<20)
                throw new InvalidOperationException($"Нет доказанной плавности: raw={raw}; smooth={smooth}; {engine.Statistics}");
            engine.Configure(false,Smoove.Core.MotionProfile.Responsive,1);
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic windows=shell.Windows();
            var modeResults=new List<string>();
            for(int wi=0;wi<(int)windows.Count;wi++)
            {
                dynamic folder=windows.Item(wi);
                if((long)folder.HWND!=(long)window)continue;
                dynamic document=folder.Document;
                int originalMode=(int)document.CurrentViewMode;
                try
                {
                    foreach(int mode in new[]{1,2,3,4,5,6,8})
                    {
                        document.CurrentViewMode=mode;
                        await Task.Delay(300);
                        var adapter=PixelScrollTarget.TryCreate(viewHandle);
                        var state=scroll.Current;
                        if(!state.VerticallyScrollable && !state.HorizontallyScrollable)
                        {modeResults.Add($"mode {mode}: fits viewport");continue;}
                        if(adapter is null)throw new InvalidOperationException($"No Explorer adapter in view mode {mode}");
                        bool horizontal=!state.VerticallyScrollable;
                        scroll.SetScrollPercent(horizontal?25:-1,horizontal?-1:25);
                        adapter.BeginGesture();
                        double previous=horizontal?scroll.Current.HorizontalScrollPercent:scroll.Current.VerticalScrollPercent;
                        double initialPercent=previous;
                        int changes=0;
                        for(int step=0;step<24;step++)
                        {
                            adapter.Move(-1);
                            var after=scroll.Current;
                            double position=horizontal?after.HorizontalScrollPercent:after.VerticalScrollPercent;
                            if(position>previous)changes++;
                            previous=position;
                        }
                        if(changes<10)throw new InvalidOperationException($"Quantized Explorer view mode {mode}: {changes}/24 intermediate positions");
                        adapter.Move(-96);
                        var final=scroll.Current;
                        double finalPercent=horizontal?final.HorizontalScrollPercent:final.VerticalScrollPercent;
                        var viewBounds=view.Current.BoundingRectangle;
                        double extent=(horizontal?viewBounds.Width:viewBounds.Height)*(100/(horizontal?final.HorizontalViewSize:final.VerticalViewSize)-1);
                        double distance=(finalPercent-initialPercent)/100*extent;
                        uint lines=Native.WheelScrollLines();
                        double expected=lines==uint.MaxValue?(horizontal?viewBounds.Width:viewBounds.Height):20*Math.Max(96,Native.GetDpiForWindow(viewHandle))/96.0*lines;
                        if(Math.Abs(distance-expected)>2)throw new InvalidOperationException($"Explorer view mode {mode} changes wheel speed: {distance:F1}px, expected {expected:F1}px");
                        modeResults.Add($"mode {mode}: {changes}/24 intermediate positions; one notch={distance:F1}px");
                    }
                }
                finally{document.CurrentViewMode=originalMode;await Task.Delay(300);}
                break;
            }
            if(modeResults.Count==0)throw new InvalidOperationException("Shell folder unavailable for view-mode checks");
            return $"PASS: Explorer actual file view; raw changes={raw.Changes}, max step={raw.MaxStep:F1}px, distance={raw.Distance:F1}px; smooth changes={smooth.Changes}, max step={smooth.MaxStep:F1}px, distance={smooth.Distance:F1}px\n"+string.Join("\n",modeResults)+$"\n{engine.Statistics}";
        }
        finally {engine.Cancel();engine.AllowExternalTestWindow(0);scroll.SetScrollPercent(horizontalView?original:-1,horizontalView?-1:original);Native.SetWindowPos(window,-2,0,0,0,0,0x13);Native.SetCursorPos(oldCursor.X,oldCursor.Y);}
    });
}
