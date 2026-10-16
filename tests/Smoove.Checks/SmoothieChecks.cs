using Smoove.Core;

internal static class SmoothieChecks
{
    internal static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Experimental model: " + message);
        }

        var motion = new SmoothieMotion();
        motion.Configure(new(8, 80, 0, 0));
        motion.Add(120, 0);
        motion.Advance(0.004);
        Require(motion.TakeDelta() == 48 && motion.Remaining == 912 && motion.Velocity == 12,
            "first frame differs from native remaining/smoothness formula");
        motion.Advance(0.008);
        double expectedVelocity = 12 * Math.Exp(-4.0 / 80);
        Require(motion.TakeDelta() == 45 && Math.Abs(motion.Velocity - expectedVelocity) < 1e-10,
            "second frame differs from exponential decay");
        motion.Add(-120, 0.008);
        Require(motion.Remaining == -960 && motion.Velocity == 0, "reverse did not discard old backlog/velocity");
        motion.Advance(0.012);
        Require(motion.TakeDelta() == -48, "reverse preserved old residue");
        motion.Cancel(0.012);
        Require(!motion.Active && motion.TakeDelta() == 0 && motion.Remaining == 0, "cancel retained output");
        motion.Add(120, 2);
        motion.Advance(2.004);
        Require(motion.TakeDelta() == 48 && motion.Active, "first notch after idle was discarded as a scheduler stall");

        foreach (int sign in new[] { -1, 1 })
        {
            var brake = new SmoothieMotion();
            brake.Configure(new(1, 80, 0, 0));
            brake.Add(sign * 1200, 0);
            brake.Advance(0.004);
            brake.TakeDelta();
            brake.Add(-sign * 120, 0.2);
            int afterReverse = 0;
            for (int tick = 1; tick <= 200; tick++)
            {
                brake.Advance(0.2 + tick * 0.001);
                int delta = brake.TakeDelta();
                afterReverse += delta;
                Require(sign * delta >= 0, "reverse notch emitted opposite-direction output during braking");
            }
            Require(!brake.Active && sign * afterReverse >= 0, "reverse notch did not stop the experimental tail");

            var resume = new SmoothieMotion();
            resume.Configure(new(1, 80, 0, 0));
            resume.Add(sign * 1200, 0);
            resume.Advance(0.004);
            resume.TakeDelta();
            resume.Add(-sign * 120, 0.2);
            resume.Advance(0.25);
            resume.TakeDelta();
            resume.Add(-sign * 120, 0.25);
            int resumedOutput = 0;
            for (int tick = 1; tick <= 300; tick++)
            {
                resume.Advance(0.25 + tick * 0.001);
                resumedOutput += resume.TakeDelta();
            }
            Require(sign * resumedOutput < 0, "next reverse notch did not resume opposite scrolling");
        }

        foreach (int sign in new[] { -1, 1 })
        foreach (int hz in new[] { 60, 120, 240, 500 })
        {
            var series = new SmoothieMotion();
            series.Configure(new(1, 80, 0, 0));
            int sum = 0;
            for (int tick = 0; tick <= 5 * hz; tick++)
            {
                double time = tick / (double)hz;
                if (tick < hz / 2 && tick % Math.Max(1, hz / 20) == 0) series.Add(sign * 120, time);
                series.Advance(time);
                sum += series.TakeDelta();
            }
            int notches = Enumerable.Range(0, hz / 2).Count(i => i % Math.Max(1, hz / 20) == 0);
            Require(!series.Active && Math.Abs(sum - sign * 120 * notches) <= 1, $"series distance at {hz} Hz: {sum}");
        }

        static int Burst(double inertia)
        {
            var burst = new SmoothieMotion();
            burst.Configure(new(1, 80, 0, inertia));
            int sum = 0;
            for (int tick = 0; tick <= 2000; tick++)
            {
                double time = tick * 0.004;
                if (tick < 50 && tick % 2 == 0) burst.Add(120, time);
                burst.Advance(time);
                sum += burst.TakeDelta();
            }
            if (burst.Active) throw new InvalidOperationException($"Experimental tail never settled: inertia={inertia}, remaining={burst.Remaining}, velocity={burst.Velocity}, output={sum}");
            return sum;
        }
        int noTail = Burst(0), extendedTail = Burst(0.5);
        Require(extendedTail > noTail, $"conditional tail extension: expected more than {noTail}, got {extendedTail}");
        Require(Burst(2) > 0, "maximum inertia did not settle with output");

        var rate = new SmoothieInputRate();
        var vertical = new SmoothieMotion(rate);
        var horizontal = new SmoothieMotion(rate);
        vertical.Add(120, 0);
        horizontal.Add(120, 0.005);
        Require(horizontal.Remaining > 960 && vertical.Remaining == 960, "axes did not share rate or leaked backlog");
        double remaining = horizontal.Remaining, velocity = horizontal.Velocity;
        horizontal.Configure(new(2, 200, 2, 2));
        Require(horizontal.Remaining == remaining && horizontal.Velocity == velocity, "tuning reset trajectory");
        horizontal.Advance(0.3);
        Require(!horizontal.Active && horizontal.TakeDelta() == 0, "long scheduler stall emitted backlog");

        foreach (var invalid in new[] { new SmoothieProfile(double.NaN), new(0.4), new(13), new(1, 19), new(1, 201), new(1, 80, -1), new(1, 80, 1, 3) })
        {
            bool rejected = false;
            try { invalid.Validate(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "invalid profile accepted");
        }
        var restored = System.Text.Json.JsonSerializer.Deserialize<SmoothieProfile>(
            System.Text.Json.JsonSerializer.Serialize(new SmoothieProfile(2.5, 135, 1.3, 1.7)));
        Require(restored == new SmoothieProfile(2.5, 135, 1.3, 1.7), "profile did not survive JSON round trip");
        Console.WriteLine("PASS: experimental native-formula frames, reverse braking, inertia, axes, distances, cancellation, validation, JSON");
    }
}
