using System.Text.Json;
using Smoove.Core;

internal static class ApplicationProfileChecks
{
    internal static void Run()
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var legacy = ApplicationProfile.Migrate(ApplicationExclusion.Migrate("Code.exe, Browser.exe"));
        Require(legacy.All(p => p.Mode == ApplicationScrollMode.Excluded) && legacy[0].Matches(@"C:\Tools\CODE.exe"), "Migration changed legacy exclusions");
        var tuning = new ScrollSettings(AnimationTempo: 8, Engine: MotionEngineKind.SmoothieExperimental, Experimental: new(2, 135, 1.3, 1.7));
        tuning.Validate();
        var custom = new ApplicationProfile(@"C:\Tools\Code.exe", ApplicationScrollMode.Custom, tuning).Validated();
        ApplicationProfile[] profiles = [..legacy, custom];
        Require(ApplicationProfile.Find(profiles, @"c:\tools\CODE.exe") == custom, "Legacy entry overrides exact app profile");
        Require(ApplicationProfile.Find(profiles, @"C:\Other\Code.exe") == legacy[0], "Legacy migration lost name matching");
        Require(ApplicationProfile.Allows(custom, true) && !ApplicationProfile.Allows(null, true) && ApplicationProfile.Allows(null, false), "Selected-only scope is incorrect");
        Require(!ApplicationProfile.Allows(legacy[0], false), "Excluded app still receives smoothing");
        Require(ApplicationProfile.Find([custom with { Enabled=false }], custom.Path) is null, "Disabled app participates in selection");
        var roundTrip = JsonSerializer.Deserialize<ApplicationProfile[]>(JsonSerializer.Serialize(profiles))!;
        Require(roundTrip.SequenceEqual(profiles) && roundTrip[^1].Settings!.Experimental == tuning.Experimental, "App/engine settings lost in JSON round trip");
        foreach (var invalid in new[] { custom with { Mode=(ApplicationScrollMode)99 }, custom with { Settings=null }, custom with { Path="Code.exe" }, custom with { Settings=tuning with { AnimationTempo=9 } } })
        {
            bool rejected=false;try { invalid.Validated(); } catch(ArgumentException) { rejected=true; }
            Require(rejected, "Invalid app profile accepted");
        }
        Require(ScrollRouting.OutputFrequency(0,60)==120 && ScrollRouting.OutputFrequency(0,144)==288 && ScrollRouting.OutputFrequency(0,360)==500 &&
            ScrollRouting.OutputFrequency(0,0)==120 && ScrollRouting.OutputFrequency(240,144)==240, "Monitor pacing calculation/bounds changed");
        Require(ScrollRouting.MotionDelta(120,true)==-120 && ScrollRouting.MotionDelta(-120,true)==120 && ScrollRouting.MotionDelta(120,false)==120, "Horizontal direction inverted");
        Require(ScrollRouting.IsIde("Code") && ScrollRouting.IsIde("Rider64") && !ScrollRouting.IsIde("chrome"), "IDE scope is incorrect");
        Require(ScrollRouting.IsTerminalElement("bash", "xterm-screen", "") && ScrollRouting.IsTerminalElement("Терминал", "", "") &&
            !ScrollRouting.IsTerminalElement("terminal.cs", "editor", "") && !ScrollRouting.IsTerminalElement("Editor", "", ""), "Terminal detection misclassifies editor");
        Console.WriteLine("PASS: app profiles, exact-path precedence, migration, scope, JSON, validation, auto frequency and horizontal direction");
    }
}
