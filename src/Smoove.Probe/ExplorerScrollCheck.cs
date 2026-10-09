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
            if(Native.ClassName(root)!="CabinetWClass")return true;
            Native.EnumChildWindows(root,(child,_)=>
            {
                if(Native.ClassName(child)=="DirectUIHWND"&&Native.ClassName(Native.GetParent(child))=="SHELLDLL_DefView")viewHandle=child;
                return viewHandle==0;
            },0);
            return viewHandle==0;
        },0);
        if(Native.ClassName(viewHandle)!="DirectUIHWND" || Native.ClassName(Native.GetParent(viewHandle))!="SHELLDLL_DefView")
            throw new InvalidOperationException($"Поместите курсор в список файлов Проводника: point={oldCursor.X},{oldCursor.Y}; hit={viewHandle:X}/{Native.ClassName(viewHandle)}; parent={Native.ClassName(Native.GetParent(viewHandle))}");
        nint window=Native.GetAncestor(viewHandle,2);
        Native.ShowWindow(window,9);Native.SetWindowPos(window,-1,0,0,0,0,0x13);Native.SetForegroundWindow(window);
        await Task.Delay(300);
        var view=AutomationElement.FromHandle(viewHandle);
        var scroll=(ScrollPattern)view.GetCurrentPattern(ScrollPattern.Pattern);
        double original=scroll.Current.VerticalScrollPercent;
        var rows=view.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
        var bounds=view.Current.BoundingRectangle;
        var point=new Native.Point{X=(int)(bounds.Left+bounds.Width/2),Y=(int)(bounds.Top+bounds.Height/2)};
        Native.SetCursorPos(point.X,point.Y);await Task.Delay(150);
        nint foreground=Native.GetForegroundWindow();
        var anchor=rows.Cast<AutomationElement>().FirstOrDefault(e=>e.Current.BoundingRectangle.Top>bounds.Top+bounds.Height/2 && e.Current.BoundingRectangle.Bottom<bounds.Bottom-100)
            ?? throw new InvalidOperationException("Нет видимой строки для измерения.");
        var anchorName=anchor.Current.Name;
        double AnchorY()=>view.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.NameProperty,anchorName))?.Current.BoundingRectangle.Top
            ?? throw new InvalidOperationException("Строка проверки вышла из видимой области.");
        int direction=original<50?-1:1;
        async Task<(int Changes,double Distance,double MaxStep)> Series(bool enabled,string name)
        {
            scroll.SetScrollPercent(-1,original);
            engine.Configure(enabled,Smoove.Core.MotionProfile.Responsive,1);
            await Task.Delay(150);
            var samples=new List<(double Time,double Y)>();
            long start=Stopwatch.GetTimestamp();int sent=0;
            while(Stopwatch.GetElapsedTime(start).TotalSeconds<2.8)
            {
                if(!Native.GetCursorPos(out var cursor)||cursor.X!=point.X||cursor.Y!=point.Y||Native.GetForegroundWindow()!=foreground||Native.WindowFromPoint(cursor)!=viewHandle||Native.ModifiersOrButtons())
                    throw new InvalidOperationException("Контекст Проводника изменился; проверка остановлена.");
                double time=Stopwatch.GetElapsedTime(start).TotalSeconds;
                if(sent<12 && time>=sent*0.1)
                {
                    if(Native.SendInput(1,[Native.WheelInput(direction*30,0)],Marshal.SizeOf<Native.Input>())!=1)throw new InvalidOperationException("Input failed");
                    sent++;
                }
                samples.Add((time,AnchorY()));await Task.Delay(8);
            }
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
            return $"PASS: Explorer actual file view; raw changes={raw.Changes}, max step={raw.MaxStep:F1}px, distance={raw.Distance:F1}px; smooth changes={smooth.Changes}, max step={smooth.MaxStep:F1}px, distance={smooth.Distance:F1}px\n{engine.Statistics}";
        }
        finally {engine.Cancel();engine.AllowExternalTestWindow(0);scroll.SetScrollPercent(-1,original);Native.SetWindowPos(window,-2,0,0,0,0,0x13);Native.SetCursorPos(oldCursor.X,oldCursor.Y);}
    });
}
