using System.Diagnostics;
using System.Runtime.InteropServices;
using Smoove.Core;
using Smoove.Probe;

namespace Smoove.Integration;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length is 2 or 3 && args[0]=="--reaper-analyze")
        {
            bool verify=args.Length==3 && args[2]=="--verify";
            int reversals=ReaperCheck.Analyze(Path.GetFullPath(args[1]),verify);
            if(verify && reversals!=0)throw new InvalidOperationException("REAPER rebound remains in the recording.");
            return;
        }
        if(args.Length is 2 or 3 && args[0]=="--reaper-check")
        {
            try { ReaperCheck.Run(Path.GetFullPath(args[1]),args.Length==3 ? args[2] : "smooth"); }
            catch(Exception ex){File.WriteAllText(args[1]+".error.txt","FAIL: "+ex);Environment.ExitCode=1;}
            return;
        }
        if(args.Length==2 && args[0]=="--reaper-windows")
        {
            ReaperCheck.Describe(Path.GetFullPath(args[1]));
            return;
        }
        if(args.Length==2 && args[0]=="--reaper-midi-scroll-patterns")
        {
            ReaperCheck.DescribeMidiScrollPatterns(Path.GetFullPath(args[1]));
            return;
        }
        if(args.Length==2 && args[0]=="--tree-check")
        {
            try { TreeScrollChecks.Run(Path.GetFullPath(args[1])); }
            catch(Exception ex){File.WriteAllText(args[1],"FAIL: "+ex);Environment.ExitCode=1;}
            return;
        }
        bool uiFixture=args.Length==1 && args[0]=="--fixture-ui";
        bool endingCheck=args.Length==2 && args[0]=="--ending-check";
        string output=uiFixture ? "" : Path.GetFullPath(endingCheck ? args[1] : args.Single());
        nint oldForeground=Native.GetForegroundWindow();
        using var form=new Form { Text="Smoove integration fixture", ClientSize=new(800,480), StartPosition=FormStartPosition.CenterScreen };
        var editor=new Receiver { Dock=DockStyle.Left, Width=400, AccessibleName="Editor" };
        var terminal=new Receiver { Dock=DockStyle.Fill, AccessibleName="Terminal" };
        form.Controls.Add(terminal);form.Controls.Add(editor);
        if(uiFixture){Application.Run(form);return;}
        form.Shown+=async (_,_)=>
        {
            Native.GetCursorPos(out var oldCursor);
            using var engine=new WheelEngine(true);
            try
            {
                engine.Configure(true, MotionProfile.Responsive, 1, outputHz:0);
                string executable=Process.GetCurrentProcess().MainModule!.FileName;
                Require(ScrollRouting.IsIde(Path.GetFileNameWithoutExtension(executable)), "Run the fixture through its Code.exe alias to exercise IDE routing");
                async Task Aim(Control control, bool allowed)
                {
                    uint thread=Native.GetCurrentThreadId();
                    uint foregroundThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
                    bool attached=thread!=foregroundThread && Native.AttachThreadInput(thread,foregroundThread,true);
                    try { Native.ShowWindow(form.Handle,9);Native.SetWindowPos(form.Handle,-1,0,0,0,0,0x03);Native.SetForegroundWindow(form.Handle);control.Focus(); }
                    finally { if(attached)Native.AttachThreadInput(thread,foregroundThread,false); }
                    var point=control.PointToScreen(new Point(control.Width/2,control.Height/2));
                    Native.SetCursorPos(point.X,point.Y);
                    for(int i=0;i<50;i++)
                    {
                        await Task.Delay(20);
                        string route=engine.RefreshRoute(true);
                        if(route.StartsWith(allowed ? "Разрешён" : "Исходный"))
                        {
                            Native.GetCursorPos(out var actual);
                            Require(Native.GetForegroundWindow()==form.Handle && actual.X==point.X && actual.Y==point.Y,
                                $"Fixture could not acquire cursor/focus: foreground={Native.GetForegroundWindow():X}/{form.Handle:X}, cursor={actual.X},{actual.Y}/{point.X},{point.Y}, visible={form.Visible}, route={route}");
                            return;
                        }
                    }
                    throw new InvalidOperationException("Route not ready: "+engine.RefreshRoute(true));
                }
                void Wheel(int delta, bool horizontal=false)
                {
                    Require(Native.GetForegroundWindow()==form.Handle,"Foreground changed before injection");
                    Require(Native.SendInput(1,[Native.WheelInput(delta,0,horizontal)],Marshal.SizeOf<Native.Input>())==1,"SendInput failed");
                }
                async Task Settle()
                {
                    await Task.Delay(100);
                    for(int i=0;i<250 && engine.IsBusy;i++)await Task.Delay(20);
                    Require(!engine.IsBusy && engine.Failure is null,"Motion failed/stalled: "+engine.Statistics);
                }
                if (endingCheck)
                {
                    await Aim(editor,true);
                    foreach (bool horizontal in new[]{false,true})
                    foreach (int count in new[]{1,30})
                    {
                        engine.Cancel();
                        engine.Configure(true,MotionProfile.Responsive,1,outputHz:0,engine:MotionEngineKind.SmoothieExperimental,experimental:new(1.5,200,1,1));
                        await Aim(editor,true);await Task.Delay(100);
                        editor.Messages.Clear();editor.Paints.Clear();
                        for(int i=0;i<count;i++){Wheel(-120,horizontal);await Task.Delay(35);}
                        await Settle();await Task.Delay(100);
                        string trace=Path.Combine(Path.GetDirectoryName(output)!,$"window-{count}-{(horizontal ? "horizontal" : "vertical")}");
                        File.WriteAllLines(trace+"-messages.csv",new[]{"seconds,delta"}.Concat(editor.Messages.Select(p=>FormattableString.Invariant($"{p.Time:R},{p.Delta}"))));
                        File.WriteAllLines(trace+"-paints.csv",new[]{"seconds,position"}.Concat(editor.Paints.Select(p=>FormattableString.Invariant($"{p.Time:R},{p.Position:R}"))));
                        Require(editor.Messages.Count>10 && editor.Paints.Count>10,"Ending scenario did not animate in real window");
                        Require(editor.Messages.TakeLast(4).All(p=>Math.Abs(p.Delta)==1),$"Final wheel messages contain a jump: {trace}");
                        Require(editor.Paints.Zip(editor.Paints.Skip(1)).TakeLast(4).All(p=>Math.Abs(p.Second.Position-p.First.Position)<=1),$"Final rendered movement exceeds one pixel: {trace}");
                        Require(count!=1 || editor.Messages.Sum(p=>p.Delta)==-180,"Single notch lost distance in Windows delivery");
                    }
                    File.WriteAllText(output,"PASS: screenshot profile, actual window: single/30-notch bursts, vertical/horizontal, last four messages are one unit, last four paint changes <=1 pixel\n"+engine.Statistics);
                    return;
                }
                engine.SetApplicationProfiles([],true);
                await Aim(editor,false);
                long accepted=engine.Accepted, before=editor.Vertical;
                Wheel(-120);await Settle();
                Require(engine.Accepted==accepted && editor.Vertical-before==-120,"Selected-only did not pass unlisted application through");
                var app=new ApplicationProfile(executable);
                engine.SetApplicationProfiles([app],true);await Aim(editor,true);
                before=editor.Vertical;accepted=engine.Accepted;
                Wheel(-120);await Settle();
                Require(engine.Accepted-accepted==1 && editor.Vertical-before==-120,"Global app profile lost native distance");
                foreach(var kind in new[]{MotionEngineKind.Smoove,MotionEngineKind.SmoothieExperimental})
                {
                    var tuning=new ScrollSettings(Distance:2,Acceleration:0,Smooth:100,Engine:kind,Experimental:new(2,80,0,0));
                    app=app with {Mode=ApplicationScrollMode.Custom,Settings=tuning};
                    engine.SetApplicationProfiles([app],true);await Aim(editor,true);
                    before=editor.Vertical;Wheel(-120);await Settle();
                    Require(Math.Abs(editor.Vertical-before+240)<=1,$"Custom engine profile distance: {kind}; actual={editor.Vertical-before}");
                    long h=editor.Horizontal,v=editor.Vertical;
                    for(int i=0;i<3;i++){Wheel(120,true);await Task.Delay(35);}
                    await Settle();
                    Require(Math.Abs(editor.Horizontal-h-720)<=1 && editor.Vertical==v,$"Native horizontal right/mixed axis: {kind}; h={editor.Horizontal-h}, v={editor.Vertical-v}");
                    h=editor.Horizontal;Wheel(-120,true);await Settle();
                    Require(Math.Abs(editor.Horizontal-h+240)<=1,$"Native horizontal left: {kind}");
                }
                app=app with {Settings=new ScrollSettings(Distance:1,Acceleration:0,Smooth:100,AnimationTempo:0.5)};
                engine.SetApplicationProfiles([app],true);await Aim(editor,true);
                before=editor.Vertical;long cancelled=engine.Cancellations;
                Wheel(-120);await Task.Delay(60);
                app=app with {Settings=app.Settings! with {AnimationTempo=4}};
                engine.SetApplicationProfiles([app],true);
                // Global engine edits must not change an application's own active engine.
                engine.Configure(true,MotionProfile.Gliding,4,engine:MotionEngineKind.SmoothieExperimental,outputHz:0);
                await Settle();
                Require(editor.Vertical-before==-120 && engine.Cancellations==cancelled,"Live custom tempo/global edit discarded the app's trajectory");
                await Aim(terminal,false);accepted=engine.Accepted;before=terminal.Vertical;
                Wheel(-120);await Settle();
                Require(engine.Accepted==accepted && terminal.Vertical-before==-120,"Terminal pane was transformed instead of bypassed");
                await Aim(editor,true);accepted=engine.Accepted;
                Wheel(-120);await Settle();Require(engine.Accepted-accepted==1,"Editor stayed bypassed after leaving terminal");
                app=app with {Mode=ApplicationScrollMode.Excluded};engine.SetApplicationProfiles([app],false);
                await Aim(editor,false);accepted=engine.Accepted;before=editor.Vertical;Wheel(-120);await Settle();
                Require(engine.Accepted==accepted && editor.Vertical-before==-120,"Excluded profile did not preserve physical input");
                Native.GetCursorPos(out var currentCursor);
                Require(engine.EffectiveOutputHz==ScrollRouting.OutputFrequency(0,Native.MonitorFrequency(currentCursor)),"Automatic pacing differs from monitor configuration");
                File.WriteAllText(output,"PASS: actual wheel hook/receiver: selected-only, global/custom/excluded profiles, both engines, native horizontal right/left, isolated axes, live custom tempo, global/profile independence, terminal bypass and editor resume\n"+engine.Statistics);
            }
            catch(Exception ex){Environment.ExitCode=1;File.WriteAllText(output,"FAIL: "+ex);}
            finally {engine.Configure(false,MotionProfile.Responsive,1);Native.SetCursorPos(oldCursor.X,oldCursor.Y);if(oldForeground!=0)Native.SetForegroundWindow(oldForeground);form.Close();}
        };
        Application.Run(form);
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class Receiver:Control
    {
        internal long Vertical, Horizontal;
        internal readonly List<(double Time,int Delta)> Messages=[];
        internal readonly List<(double Time,double Position)> Paints=[];
        internal Receiver(){BackColor=SystemColors.Window;TabStop=true;}
        protected override void OnPaint(PaintEventArgs e)
        {
            double position=(Vertical+Horizontal)*0.5;
            if (Paints.Count==0 || Paints[^1].Position!=position)Paints.Add((Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,position));
            for(int y=-40;y<Height+40;y+=40)e.Graphics.DrawLine(SystemPens.ControlDark,0,y+(float)(position%40),Width,y+(float)(position%40));
            e.Graphics.DrawString(AccessibleName+"\n"+$"V={Vertical}, H={Horizontal}",SystemFonts.DefaultFont,SystemBrushes.WindowText,20,20);
        }
        protected override void WndProc(ref Message message)
        {
            if(message.Msg is Native.Wheel or Native.HWheel)
            {
                int delta=unchecked((short)((long)message.WParam>>16));
                Messages.Add((Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,delta));
                if(message.Msg==Native.HWheel)Horizontal+=delta;else Vertical+=delta;
                Invalidate();message.Result=0;return;
            }
            base.WndProc(ref message);
        }
    }
}
