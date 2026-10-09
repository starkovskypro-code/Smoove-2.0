using System.Globalization;
using Smoove.Core;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string output = Path.GetFullPath(args.FirstOrDefault() ?? "out/model-checks");
const nuint ownMarker = 0x534D5632;
Require(WheelSource.Transformable(false, 0, ownMarker), "Physical wheel was bypassed");
Require(WheelSource.Transformable(true, 0, ownMarker), "Real user's zero-extra injected wheel was bypassed");
Require(!WheelSource.Transformable(true, ownMarker, ownMarker) && !WheelSource.Transformable(false, ownMarker, ownMarker), "Own events can loop");
Require(!WheelSource.Transformable(true, 0x11223344, ownMarker), "Marked foreign transformation was processed twice");
Require(WheelSource.Transformable(false, 0x11223344, ownMarker), "Physical vendor metadata was misclassified as injection");
Directory.CreateDirectory(output);
var legacyExclusions=ApplicationExclusion.Migrate("editor.exe, EDITOR.exe, browser");
Require(legacyExclusions.Length==2 && legacyExclusions[0].Matches(@"C:\other\EDITOR.EXE"),"Legacy migration lost name matching");
var exactExclusion=new ApplicationExclusion(ApplicationExclusion.Normalize(@"C:\Apps\v1\..\v2\editor.exe"));
Require(exactExclusion.Matches(@"c:\apps\V2\EDITOR.exe") && !exactExclusion.Matches(@"C:\Apps\v1\editor.exe"),"Exact paths collide or fail normalization");
Require(!(exactExclusion with {Enabled=false}).Matches(@"C:\Apps\v2\editor.exe"),"Disabled exclusion still active");
Require(System.Text.Json.JsonSerializer.Deserialize<ApplicationExclusion[]>(System.Text.Json.JsonSerializer.Serialize(legacyExclusions))!.Length==2,"Legacy entries do not survive storage");
var results = new List<string>();
double AccelerationGain(double interval, double strength)
{
    var tempo = new TempoAcceleration();
    double sum = 0;
    for (int i = 0; i < 100; i++) sum += tempo.Scale(120, i * interval, strength);
    return sum / 12000;
}
double slowGain = AccelerationGain(0.4, 0.35), quickGain = AccelerationGain(0.05, 0.35);
Require(Math.Abs(slowGain - 1) < 1e-9 && quickGain > 1.25 && quickGain <= 1.35, "Tempo gain did not preserve slow precision/increase quick distance");
Require(AccelerationGain(0.001, 0.35) > 1.34 && AccelerationGain(0.001, 0) == 1, "Free-spin accelerated gain is bounded incorrectly or cannot disable");
var tempoReset = new TempoAcceleration();
for (int i = 0; i < 20; i++) tempoReset.Scale(120, i * 0.01, 0.35);
Require(tempoReset.Scale(-120, 0.21, 0.35) == -120, "Reversal retained high-tempo gain");
Require(tempoReset.Scale(-120, 2, 0.35) == -120, "New gesture retained stale acceleration");
results.Add($"tempo acceleration: slow gain={slowGain:F3}; quick gain={quickGain:F3}; free-spin gain={AccelerationGain(0.001, 0.35):F3}");
Require(AccelerationGain(0.1, 0.6) > 1.45 && AccelerationGain(0.4, 0.6) == 1,
    "Ordinary quicker notched rotation did not get a noticeable speed gain");
var normalSmoothing = new ScrollMotion();
var softerSmoothing = new ScrollMotion();
softerSmoothing.Configure(new(MotionProfile.Responsive.RiseSeconds * 1.5, MotionProfile.Responsive.CoastSeconds * 1.5));
normalSmoothing.Add(120, 0); softerSmoothing.Add(120, 0);
normalSmoothing.Advance(0.1); softerSmoothing.Advance(0.1);
Require(softerSmoothing.Position < normalSmoothing.Position && softerSmoothing.Velocity < normalSmoothing.Velocity,
    "Smoothing control did not soften the response");
normalSmoothing.Advance(5); softerSmoothing.Advance(5);
Require(normalSmoothing.TakeDelta() == 120 && softerSmoothing.TakeDelta() == 120, "Smoothing changed the distance");
foreach (var (name, profile) in new[] { ("responsive", MotionProfile.Responsive), ("gliding", MotionProfile.Gliding) })
{
    foreach (double interval in new[] { 0.02, 0.05, 0.1, 0.2, 0.4 })
    {
        var motion = new ScrollMotion();
        motion.Configure(profile);
        var speeds = new List<double>();
        int emitted = 0, input = 0;
        double previous = 0;
        double end = interval * 100 + 4;
        string file = Path.Combine(output, $"{name}-{interval * 1000:000}ms.csv");
        using var writer = new StreamWriter(file);
        writer.WriteLine("seconds,input_delta,position,velocity,output_delta,target");
        for (int step = 0; step <= (int)(end / 0.002); step++)
        {
            double time = step * 0.002;
            motion.Advance(time);
            bool notch = step % (int)Math.Round(interval / 0.002) == 0 && input < 100;
            if (notch)
            {
                double position = motion.Position, velocity = motion.Velocity;
                motion.Add(120, time);
                Require(motion.Position == position && motion.Velocity == velocity, "Input reset position or velocity");
                input++;
            }
            Require(motion.Position + 1e-8 >= previous && motion.Position <= motion.Target + 1e-8, "Forward gesture overshot or moved backwards");
            int delta = motion.TakeDelta();
            Require(delta >= 0, "Unexpected negative output");
            emitted += delta;
            if (time >= 1 && time < interval * 100 - interval) speeds.Add(motion.Velocity);
            writer.WriteLine(string.Join(",", new[] { time, notch ? 120 : 0, motion.Position, motion.Velocity, delta, motion.Target }
                .Select(value => value.ToString("G17", CultureInfo.InvariantCulture))));
            previous = motion.Position;
        }
        Require(emitted == 12000 && !motion.Active && motion.Velocity == 0, $"Distance/tail failed: {name}, {interval}, {emitted}");
        double ripple = (speeds.Max() - speeds.Min()) / speeds.Average();
        // Include ordinary 100ms notches: the earlier gate missed their visible speed pulses.
        if (interval <= 0.1) Require(ripple < 0.12, $"Excess speed ripple at {interval}: {ripple}");
        results.Add($"{name}, {interval * 1000:0}ms: sum={emitted}, velocity ripple={ripple:P1}");
    }
}

// Fractional distance residue survives gesture boundaries; small signed deltas aren't lost.
var residue = new ScrollMotion();
int residueSum = 0;
for (int i = 0; i < 100; i++)
{
    residue.Add(-0.1, i * 2);
    residue.Advance(i * 2 + 1.5);
    residueSum += residue.TakeDelta();
}
Require(residueSum == -10, "Fractional residue was lost");

// Equal poles and different time-step sizes must represent the same continuous trajectory.
foreach (var profile in new[] { MotionProfile.Gliding, new MotionProfile(0.1, 0.1) })
{
    var fine = new ScrollMotion(); var coarse = new ScrollMotion();
    fine.Configure(profile); coarse.Configure(profile);
    fine.Add(120, 0); coarse.Add(120, 0);
    for (int i = 1; i <= 100; i++) fine.Advance(i * 0.001);
    coarse.Advance(0.1);
    Require(Math.Abs(fine.Position - coarse.Position) < 1e-8 && Math.Abs(fine.Velocity - coarse.Velocity) < 1e-8, "Integration depends on tick size");
}

var reverse = new ScrollMotion();
reverse.Add(600, 0); reverse.Advance(0.06);
double beforeVelocity = reverse.Velocity, beforePosition = reverse.Position;
reverse.Add(-120, 0.06);
Require(reverse.Velocity == beforeVelocity && reverse.Position == beforePosition, "Reversal jumped state");
reverse.Advance(0.061);
Require(Math.Abs(reverse.Velocity - beforeVelocity) < Math.Abs(beforeVelocity) * 0.02, "Reversal caused a braking jerk");
for (int i = 1; i <= 8; i++) reverse.Add(-120, 0.06 + i * 0.05);
reverse.Advance(0.6);
Require(reverse.Velocity < 0, "Sustained opposite input did not reverse motion");
reverse.Configure(MotionProfile.Gliding);
Require(reverse.Velocity < 0, "Profile change reset velocity");
reverse.Cancel(0.6); reverse.Advance(1);
Require(!reverse.Active && reverse.TakeDelta() == 0 && reverse.Velocity == 0, "Cancellation emitted stale motion");

// Long free-spin series, including values that exceeded the old per-output limit.
foreach (double interval in new[] { 0.001, 0.005, 0.02, 0.05 })
{
    var spin = new ScrollMotion();
    long sum = 0;
    double minSpeed = double.MaxValue, maxSpeed = 0;
    int largestOutput = 0;
    for (int i = 0; i < 12000; i++)
    {
        double time = i * 0.001;
        if (i % (int)Math.Round(interval * 1000) == 0) spin.Add(120, time);
        spin.Advance(time);
        int part = spin.TakeDelta();
        sum += part;
        largestOutput = Math.Max(largestOutput, part);
        if (time > 2) { minSpeed = Math.Min(minSpeed, spin.Velocity); maxSpeed = Math.Max(maxSpeed, spin.Velocity); }
    }
    spin.Advance(17);
    sum += spin.TakeDelta();
    long expected = (long)Math.Round(12 / interval) * 120;
    Require(sum == expected && !spin.Active, "Free-spin lost distance or failed to settle");
    Require(minSpeed > 120 / interval * 0.98 && maxSpeed < 120 / interval * 1.02, "Free-spin stalls or speed is capped");
    results.Add($"free-spin {interval * 1000:F0}ms: sum={sum}; speed={minSpeed:F0}..{maxSpeed:F0} units/s; max 1ms delta={largestOutput}");
}

var large = new ScrollMotion();
large.Add(120000, 0);
large.Advance(0.2);
int largePart = large.TakeDelta();
Require(largePart > 32767, "Output still has a hidden speed cap");
large.Advance(5);
Require((long)largePart + large.TakeDelta() == 120000, "Large input lost distance");

// Faster ordinary rotation should increase speed proportionally, without an acceleration toggle.
double MeasureSpeed(double interval)
{
    var motion = new ScrollMotion();
    for (int i = 0; i < (int)(4 / interval); i++) motion.Add(120, i * interval);
    motion.Advance(4 - interval / 2);
    return motion.Velocity;
}
Require(MeasureSpeed(0.05) > MeasureSpeed(0.1) * 1.8, "Faster rotation did not produce faster scroll");

var alternating = new ScrollMotion();
for (int i = 0; i < 200; i++)
{
    double time = i * 0.03;
    alternating.Advance(time);
    double position = alternating.Position, velocity = alternating.Velocity;
    alternating.Add(i % 20 < 10 ? 120 : -120, time);
    Require(position == alternating.Position && velocity == alternating.Velocity, "Repeated reversal resets trajectory");
    alternating.Advance(time + 0.001);
    Require(Math.Abs(alternating.Velocity - velocity) < 120, "Repeated reversal produces a velocity spike");
}
results.Add("PASS: free-spin throughput, speed proportionality, large output, soft repeated reversals");

// Irregular hand-like timing, short gaps and new input during deceleration.
foreach (var profile in new[] { MotionProfile.Responsive, MotionProfile.Gliding })
{
    var motion = new ScrollMotion();
    motion.Configure(profile);
    double time = 0;
    foreach (double gap in new[] { 0.07, 0.04, 0.025, 0.06, 0.1, 0.18, 0.035, 0.08 })
    {
        time += gap;
        motion.Advance(time);
        double position = motion.Position, velocity = motion.Velocity;
        motion.Add(120, time);
        Require(motion.Position == position && motion.Velocity == velocity, "Irregular input reset motion");
    }
    motion.Advance(time + 4);
    Require(motion.TakeDelta() == 960 && !motion.Active, "Irregular sequence lost distance");
}

bool rejected = false;
try { new MotionProfile(double.NaN, 0.1).Validate(); } catch (ArgumentOutOfRangeException) { rejected = true; }
Require(rejected, "Invalid profile accepted");
rejected = false;
try { reverse.Add(double.PositiveInfinity, 1); } catch (ArgumentOutOfRangeException) { rejected = true; }
Require(rejected, "Invalid input accepted");

foreach (string result in results) Console.WriteLine(result);
Console.WriteLine("PASS: real source policy, own-loop guard, continuous series, distance, residue, time-step independence, reversal, cancellation, validation");
File.WriteAllLines(Path.Combine(output, "result.txt"), results.Append("PASS: model checks"));
