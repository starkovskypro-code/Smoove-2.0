namespace Smoove.Core;

public sealed record SmoothieProfile(double DistancePerNotch = 8, double SmoothnessMs = 80,
    double Acceleration = 1, double Inertia = 0.5)
{
    public void Validate()
    {
        if (!double.IsFinite(DistancePerNotch + SmoothnessMs + Acceleration + Inertia) ||
            DistancePerNotch is < 0.5 or > 12 || SmoothnessMs is < 20 or > 200 ||
            Acceleration is < 0 or > 2 || Inertia is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(SmoothieProfile));
    }
}

// The native model estimates wheel rate across both axes before updating a channel.
public sealed class SmoothieInputRate
{
    private double _time;
    private bool _hasInput;
    public double Value { get; private set; }

    public double Update(double delta, double seconds)
    {
        if (!double.IsFinite(delta) || !double.IsFinite(seconds) || (_hasInput && seconds < _time))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        double dtMs = _hasInput ? Math.Max(1, (seconds - _time) * 1000) : 301;
        if (dtMs > 300) Value = 0;
        double rate = Math.Min(100, Math.Abs(delta / 120) / (dtMs / 1000));
        Value += (rate - Value) * (1 - Math.Exp(-dtMs / 80));
        _time = seconds;
        _hasInput = true;
        return Value;
    }

    public void Clear() => Value = 0;
    public void Reset() { Value = 0; _time = 0; _hasInput = false; }
}

// Independent C# reconstruction of the observed native formulas; no competitor binary is loaded.
// shortcut: uses Smoove's scheduler and output routes, compare the original runtime separately for parity.
public sealed class SmoothieMotion : IScrollMotion
{
    private const double ReverseBrakeDelaySeconds = 0.08;
    private const double ReverseBrakeDurationMs = 120;
    private readonly SmoothieInputRate _rate;
    private SmoothieProfile _profile = new();
    private double _remaining, _residue, _velocity, _channelRate, _lastInput;
    private int _pending;
    private bool _tail;
    private bool _finishing;
    private bool _braking;
    private double _brakeStart, _brakeDistance, _brakeProgress, _brakeVelocity;
    private double _finishStart, _finishDuration, _finishDistance, _finishVelocity;
    public bool Active { get; private set; }
    public double Time { get; private set; }
    public double Remaining => _remaining;
    public double Velocity => _velocity;

    public SmoothieMotion(SmoothieInputRate? rate = null) => _rate = rate ?? new();

    public void Configure(SmoothieProfile profile)
    {
        profile.Validate();
        _profile = profile;
    }

    public void Add(double delta, double seconds)
    {
        if (!double.IsFinite(delta) || Math.Abs(delta) > int.MaxValue || !double.IsFinite(seconds) || seconds < Time)
            throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == 0) return;
        int direction = Math.Sign(delta);
        // After a pause, consume the first opposite notch as a smooth stop.
        if (Active && !_braking && Math.Sign(_remaining) != 0 && direction != Math.Sign(_remaining) &&
            Math.Sign(_velocity) == Math.Sign(_remaining) && Math.Abs(_velocity) > 0.01 &&
            seconds - _lastInput >= ReverseBrakeDelaySeconds)
        {
            _brakeDistance = Math.Sign(_velocity) * Math.Min(Math.Abs(_remaining), Math.Abs(_velocity) * ReverseBrakeDurationMs / 2);
            _remaining = _brakeDistance;
            _brakeVelocity = _velocity;
            _brakeStart = seconds;
            _brakeProgress = 0;
            _braking = true;
            _tail = false;
            _finishing = false;
            _lastInput = seconds;
            return;
        }
        if (_braking)
        {
            _braking = false;
            _remaining = _residue = _velocity = 0;
            _rate.Clear();
        }
        if (!Active)
        {
            _remaining = _velocity = 0;
            Time = seconds;
        }
        _rate.Update(delta, seconds);
        if (_remaining != 0 && Math.Sign(delta) != Math.Sign(_remaining))
        {
            _remaining = _residue = _velocity = 0;
            _rate.Clear();
        }
        double gain = 1 + 3 * _profile.Acceleration * Math.Tanh(Math.Max(_rate.Value - 8, 0) / 30);
        _remaining = Math.Clamp(_remaining + delta * _profile.DistancePerNotch * gain, -36000, 36000);
        _channelRate = _rate.Value;
        _lastInput = seconds;
        _tail = false;
        _finishing = false;
        Active = true;
    }

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < Time) throw new ArgumentOutOfRangeException(nameof(seconds));
        double dtMs = (seconds - Time) * 1000;
        Time = seconds;
        if (!Active || dtMs == 0) return;
        if (dtMs > 250) { Cancel(seconds); return; }
        if (_braking)
        {
            double u = Math.Clamp((seconds - _brakeStart) * 1000 / ReverseBrakeDurationMs, 0, 1);
            double progress = u - u * u * u + 0.5 * u * u * u * u;
            double brakeStep = _brakeDistance * (progress - _brakeProgress);
            _brakeProgress = progress;
            _remaining -= brakeStep;
            _velocity = _brakeVelocity * (1 - 3 * u * u + 2 * u * u * u);
            if (u == 1)
            {
                _remaining = _brakeDistance = _brakeProgress = 0;
                _velocity = 0;
                _braking = false;
                Active = false;
            }
            double brakeOutput = brakeStep + _residue;
            int brakeWhole = (int)Math.Round(brakeOutput, MidpointRounding.AwayFromZero);
            _residue = brakeOutput - brakeWhole;
            _pending = checked(_pending + brakeWhole);
            return;
        }
        double inertia = _profile.Inertia * Math.Tanh(Math.Max(_channelRate - 8, 0) / 25);
        double tau = _profile.SmoothnessMs * (1 + inertia);
        if (!_finishing && !_tail && seconds - _lastInput >= 0.12)
        {
            _tail = true;
            if (inertia > 0 && Math.Abs(_velocity) * tau > Math.Abs(_remaining))
                _remaining = Math.Sign(_velocity) * Math.Min(Math.Abs(_velocity) * tau, 36000);
        }
        double desired = _remaining / _profile.SmoothnessMs;
        double previousVelocity = _velocity;
        if (!_finishing && !_tail && (Math.Sign(desired) != Math.Sign(_velocity) || Math.Abs(desired) > Math.Abs(_velocity)))
            _velocity = desired;
        else if (!_finishing)
            _velocity *= Math.Exp(-dtMs / tau);
        // Integrate the tail exactly: sampled v*dt can leave a permanent remainder with zero velocity.
        double step = _tail ? previousVelocity * tau * (1 - Math.Exp(-dtMs / tau)) : _velocity * dtMs;
        if (!_finishing && (Math.Abs(_remaining) <= 6 || Math.Abs(step) >= Math.Abs(_remaining)))
        {
            // Spend the final distance with continuous velocity instead of flushing it in one frame.
            _finishing = true;
            _finishStart = seconds - dtMs / 1000;
            _finishDistance = _remaining;
            _finishVelocity = previousVelocity;
            _finishDuration = Math.Min(3 * tau, previousVelocity == 0 ? 3 * tau : 3 * Math.Abs(_remaining / previousVelocity));
        }
        if (_finishing)
        {
            double u = _finishDuration == 0 ? 1 : Math.Clamp((seconds - _finishStart) * 1000 / _finishDuration, 0, 1);
            double vT = _finishVelocity * _finishDuration;
            double a = 3 * _finishDistance - 2 * vT, b = vT - 2 * _finishDistance;
            double remaining = u == 1 ? 0 : _finishDistance - (vT * u + a * u * u + b * u * u * u);
            step = _remaining - remaining;
            _velocity = u == 1 ? 0 : (_finishVelocity + (2 * a * u + 3 * b * u * u) / _finishDuration);
        }
        _remaining -= step;
        double output = step + _residue;
        // Nearest-unit quantization distributes the last unit before rest, not as a late flush.
        int whole = (int)(_finishing ? Math.Round(output, MidpointRounding.AwayFromZero) : Math.Truncate(output));
        _residue = output - whole;
        int limited = Math.Clamp(whole, -1200, 1200);
        _remaining += whole - limited;
        _pending = checked(_pending + limited);
        if (_remaining == 0 && _velocity == 0)
        {
            Active = false;
            _finishing = false;
        }
    }

    public int TakeDelta() { int delta = _pending; _pending = 0; return delta; }

    public void Cancel(double seconds)
    {
        _remaining = _residue = _velocity = _channelRate = 0;
        _pending = 0;
        _tail = _finishing = Active = false;
        _braking = false;
        _brakeStart = _brakeDistance = _brakeProgress = _brakeVelocity = 0;
        Time = seconds;
    }
}
