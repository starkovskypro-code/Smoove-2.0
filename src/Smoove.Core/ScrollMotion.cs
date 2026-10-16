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
public sealed class ScrollMotion : IScrollMotion
{
    private double _time;
    private double _motionTime;
    private double _lastInput;
    private double _lastMotionInput;
    private double _emitted;
    private int _direction;
    private MotionProfile _profile = MotionProfile.Responsive;
    private const double BrakeSeconds = 0.12;
    private bool _braking;
    private double _brakeStart, _brakePosition, _brakeVelocity;
    private double _tempo = 1;

    public double Position { get; private set; }
    // Velocity is per animation second; physical velocity is this value times tempo.
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool Active { get; private set; }
    public double Time => _time;

    public void Configure(MotionProfile profile, double tempo = 1)
    {
        profile.Validate();
        if (!double.IsFinite(tempo) || tempo is < 0.5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(tempo));
        _profile = profile;
        _tempo = tempo;
    }

    public void Add(double delta, double seconds)
    {
        if (!double.IsFinite(delta) || Math.Abs(delta) > int.MaxValue || !double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == 0) return;
        Advance(seconds);
        int direction = Math.Sign(delta);
        if (Active && !_braking && direction != _direction && Math.Sign(Velocity) == _direction && seconds - _lastInput >= 0.08)
        {
            // The first opposite notch during the tail is a brake, not reverse travel.
            _braking = true;
            _brakeStart = _motionTime;
            _brakePosition = Position;
            _brakeVelocity = Velocity;
            Target = Position + Velocity * BrakeSeconds / 2;
            _lastInput = seconds;
            _lastMotionInput = _motionTime;
            _direction = direction;
            return;
        }
        if (_braking)
        {
            _braking = false;
            Target = Position + Velocity * (_profile.RiseSeconds + _profile.CoastSeconds);
        }
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
            _motionTime = seconds;
            // A small initial impulse avoids the quantization dead zone without a position jump.
            Velocity=delta*0.2/Math.Min(_profile.RiseSeconds+_profile.CoastSeconds,0.2);
        }
        Target += delta;
        _lastInput = seconds;
        _lastMotionInput = _motionTime;
        _direction = direction;
        Active = true;
    }

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < _time)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        double dt = seconds - _time;
        _time = seconds;
        double motionDt = dt * _tempo;
        _motionTime += motionDt;
        if (!Active || dt == 0) return;
        if (_braking)
        {
            double u = Math.Clamp((_motionTime - _brakeStart) / BrakeSeconds, 0, 1);
            // Smoothstep velocity reaches zero with zero slope; integrate it exactly.
            Position = _brakePosition + _brakeVelocity * BrakeSeconds * (u - u*u*u + 0.5*u*u*u*u);
            Velocity = _brakeVelocity * (1 - 3*u*u + 2*u*u*u);
            if (u == 1) { Position = Target; Velocity = 0; Active = false; _braking = false; }
            return;
        }

        // Two real negative poles: an overdamped target follower, integrated exactly.
        // x'' + (1/a + 1/b)x' + (x-target)/(a*b) = 0.
        // Changing target/profile leaves both x and velocity continuous.
        double a = _profile.RiseSeconds;
        double b = _profile.CoastSeconds;
        dt = motionDt;
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
        if (_motionTime - _lastMotionInput > 0.05 && Math.Abs(Target - Position) < 0.01 &&
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
        _motionTime = seconds;
        Active = false;
        _direction = 0;
        _braking = false;
    }
}
