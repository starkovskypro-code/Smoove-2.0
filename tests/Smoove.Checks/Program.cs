using System.Globalization;
using Smoove.Core;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string output = Path.GetFullPath(args.FirstOrDefault() ?? "out/model-checks");
Directory.CreateDirectory(output);
var results = new List<string>();
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
reverse.Advance(0.11);
Require(reverse.Velocity < 0, "Reversal did not turn promptly");
reverse.Configure(MotionProfile.Gliding);
Require(reverse.Velocity < 0, "Profile change reset velocity");
reverse.Cancel(0.11); reverse.Advance(1);
Require(!reverse.Active && reverse.TakeDelta() == 0 && reverse.Velocity == 0, "Cancellation emitted stale motion");

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
Console.WriteLine("PASS: continuous series, distance, residue, time-step independence, reversal, cancellation, validation");
File.WriteAllLines(Path.Combine(output, "result.txt"), results.Append("PASS: model checks"));
