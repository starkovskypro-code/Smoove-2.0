namespace Smoove.Core;

public sealed record MotionProfile(double RiseSeconds, double CoastSeconds)
{
    public static MotionProfile Responsive { get; } = new(0.070, 0.160);
    public static MotionProfile Gliding { get; } = new(0.110, 0.280);

    public void Validate()
    {
        if (!double.IsFinite(RiseSeconds) || !double.IsFinite(CoastSeconds) ||
            RiseSeconds is < 0.0025 or > 0.6 || CoastSeconds is < 0.0025 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MotionProfile));
    }
}

// One position/velocity state for the whole gesture, not an animation per notch.
public sealed class ScrollMotion
{
    private double _time;
    private double _lastInput;
    private double _emitted;
    private int _direction;
    private MotionProfile _profile = MotionProfile.Responsive;

    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool Active { get; private set; }
    public double Time => _time;

    public void Configure(MotionProfile profile)
    {
        profile.Validate();
        _profile = profile;
    }

    public void Add(double delta, double seconds)
    {
        if (!double.IsFinite(delta) || Math.Abs(delta) > int.MaxValue || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == 0) return;
        Advance(seconds);
        int direction = Math.Sign(delta);
        if (Active && direction != _direction)
        {
            // Discard backlog but keep the destination compatible with current velocity:
            // zero acceleration before the new impulse, using the SAME profile poles.
            // Snapping the destination to Position gave a large braking impulse.
            Target = Position + Velocity * (_profile.RiseSeconds + _profile.CoastSeconds);
        }
        else if (!Active)
        {
            _time = seconds;
            // A small initial impulse avoids the quantization dead zone without a position jump.
            Velocity=delta*0.2/Math.Min(_profile.RiseSeconds+_profile.CoastSeconds,0.2);
        }
        Target += delta;
        _lastInput = seconds;
        _direction = direction;
        Active = true;
    }

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < _time)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        double dt = seconds - _time;
        _time = seconds;
        if (!Active || dt == 0) return;

        // Two real negative poles: an overdamped target follower, integrated exactly.
        // x'' + (1/a + 1/b)x' + (x-target)/(a*b) = 0.
        // Changing target/profile leaves both x and velocity continuous.
        double a = _profile.RiseSeconds;
        double b = _profile.CoastSeconds;
        double error = Position - Target;
        if (Math.Abs(a - b) < 1e-9)
        {
            double omega = 1 / a;
            double term = Velocity + omega * error;
            double decay = Math.Exp(-omega * dt);
            Position = Target + (error + term * dt) * decay;
            Velocity = (Velocity - omega * term * dt) * decay;
        }
        else
        {
            double c1 = (Velocity + error / b) / (1 / b - 1 / a);
            double c2 = error - c1;
            double e1 = Math.Exp(-dt / a);
            double e2 = Math.Exp(-dt / b);
            Position = Target + c1 * e1 + c2 * e2;
            Velocity = -c1 / a * e1 - c2 / b * e2;
        }

        // Stop only when the last correction is sub-delta, not at an arbitrary deadline.
        if (seconds - _lastInput > 0.05 && Math.Abs(Target - Position) < 0.01 &&
            Math.Abs(Velocity) < 0.1)
        {
            Position = Target;
            Velocity = 0;
            Active = false;
        }
    }

    public int TakeDelta()
    {
        // Quantize accumulated position. Keep fractional residue across complete gestures.
        double whole = Math.Round(Position, MidpointRounding.AwayFromZero);
        int delta = (int)Math.Clamp(whole - _emitted, -int.MaxValue, int.MaxValue);
        _emitted += delta;
        return delta;
    }

    public void Cancel(double seconds)
    {
        Position = Target = Velocity = _emitted = 0;
        _time = seconds;
        Active = false;
        _direction = 0;
    }
}
