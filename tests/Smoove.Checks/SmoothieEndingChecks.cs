using Smoove.Core;

internal static class SmoothieEndingChecks
{
    internal static void Run(string output)
    {
        var failures = new List<string>();
        foreach (int hz in new[] { 60, 118, 144, 240, 500 })
        foreach (int notches in new[] { 1, 30 })
        foreach (int sign in new[] { -1, 1 })
        {
            string name = $"ending-{hz}hz-{notches}notches-{sign}";
            var motion = new SmoothieMotion();
            motion.Configure(new(1.5, 200, 1, 1));
            var rows = new List<string> { "seconds,remaining,velocity,step,delta" };
            double priorRemaining = 0, priorVelocity = 0, largestEndStep = 0, exhaustedVelocity = 0, excessEndStep = 0;
            int sent = 0, added = 0;
            var inputRate = new SmoothieInputRate();
            double expectedDistance = 0, lastInput = 0, lastRate = 0;
            bool tailBudgeted = false;
            for (int tick = 0; tick < hz * 15; tick++)
            {
                double time = tick / (double)hz;
                if (added < notches && time + 1e-9 >= added * 0.035)
                {
                    lastRate = inputRate.Update(sign * 120, time);
                    double impulse = sign * 180 * (1 + 3 * Math.Tanh(Math.Max(lastRate - 8, 0) / 30));
                    expectedDistance += Math.Clamp(motion.Remaining + impulse, -36000, 36000) - motion.Remaining;
                    motion.Add(sign * 120, time); added++; lastInput = time; tailBudgeted = false;
                }
                priorRemaining = motion.Remaining; priorVelocity = motion.Velocity;
                if (!tailBudgeted && time - lastInput >= 0.12)
                {
                    double tau = 200 * (1 + Math.Tanh(Math.Max(lastRate - 8, 0) / 25));
                    if (lastRate > 8 && Math.Abs(priorVelocity) * tau > Math.Abs(priorRemaining))
                        expectedDistance += Math.Sign(priorVelocity) * Math.Min(Math.Abs(priorVelocity) * tau, 36000) - priorRemaining;
                    tailBudgeted = true;
                }
                motion.Advance(time);
                double step = Math.Abs(priorRemaining - motion.Remaining);
                int delta = motion.TakeDelta(); sent += delta;
                if (added == notches && Math.Abs(priorRemaining) is > 0 and <= 6)
                {
                    largestEndStep = Math.Max(largestEndStep, step);
                    excessEndStep = Math.Max(excessEndStep, step - Math.Abs(priorVelocity) * 1000 / hz);
                }
                if (priorRemaining != 0 && motion.Remaining == 0) exhaustedVelocity = Math.Abs(priorVelocity);
                rows.Add(FormattableString.Invariant($"{time:R},{motion.Remaining:R},{motion.Velocity:R},{step:R},{delta}"));
                // Recorded pre-fix trajectory at the screenshot profile: preserve the burst and its large-scale tail.
                if (hz == 118 && notches == 30)
                {
                    double? reference = tick switch { 59 => 2501.6099743777477, 118 => 2605.6474671768137,
                        177 => 1213.2879099577196, 236 => 276.3877835276563, 354 => 14.34262981244993, _ => null };
                    if (reference is { } value && Math.Abs(motion.Remaining - sign * value) > 1e-8)
                        failures.Add($"{name}: existing burst trajectory changed at {time:F3}s");
                }
                if (added == notches && !motion.Active) break;
            }
            File.WriteAllLines(Path.Combine(output, name + ".csv"), rows);
            if (hz == 118 && sign == 1)
                Console.WriteLine(FormattableString.Invariant($"Ending {name}: output={sent}, max step below 6={largestEndStep:F4}, exhaustion velocity={exhaustedVelocity:F6}, active={motion.Active}"));
            if (motion.Active || excessEndStep > 0.001 || exhaustedVelocity > 0.001)
                failures.Add($"{name}: near-end step={largestEndStep:F4}, velocity at exhaustion={exhaustedVelocity:F6}, active={motion.Active}");
            if (notches == 1 && sent != sign * 180) failures.Add($"{name}: expected single-notch distance={sign * 180}, actual={sent}");
            if (sent != (int)Math.Round(expectedDistance, MidpointRounding.AwayFromZero))
                failures.Add($"{name}: expected distance including gain/inertia={expectedDistance:F4}, actual={sent}");
        }
        if (failures.Count > 0) throw new InvalidOperationException("Experimental ending regression: " + string.Join("; ", failures));
        Console.WriteLine("PASS: screenshot profile single/burst endings: no multi-unit tail flush, velocity tends to zero, exact single distance");
        var resumed = new SmoothieMotion(); resumed.Configure(new(1.5, 200, 1, 1));resumed.Add(120, 0);
        int endTick=1;
        for (;endTick<=500;endTick++){resumed.Advance(endTick * 0.01);resumed.TakeDelta();if(resumed.Remaining is > 0 and <= 6)break;}
        if (resumed.Remaining is <= 0 or > 6) throw new InvalidOperationException("Ending restart fixture missed final phase");
        resumed.Configure(new(1.5, 150, 1, 1));
        resumed.Add(-120, endTick * 0.01);
        int reverse=0;
        for(int tick=endTick+1;tick<=endTick+500;tick++){resumed.Advance(tick*0.01);reverse+=resumed.TakeDelta();}
        if (resumed.Active || reverse < 0) throw new InvalidOperationException("Reverse/configuration during ending emitted opposite-direction output");
        var fractional = new SmoothieMotion(); fractional.Configure(new(0.5, 200, 0, 0));
        int fractionalSum = 0;
        for (int gesture=0;gesture<20;gesture++)
        {
            fractional.Add(1,gesture*3);
            for(int tick=1;tick<=250;tick++){fractional.Advance(gesture*3+tick*0.01);fractionalSum+=fractional.TakeDelta();}
        }
        if(fractionalSum!=10)throw new InvalidOperationException($"Fractional distance lost between gestures: expected=10, actual={fractionalSum}");
    }
}
