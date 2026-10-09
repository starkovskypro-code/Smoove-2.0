namespace Smoove.Core;

public sealed class TempoAcceleration
{
    private double _time, _rate;
    private int _direction;
    public void Reset(double time) { _time = time; _rate = 0; _direction = 0; }

    public double Scale(double delta, double time, double strength)
    {
        if (!double.IsFinite(delta) || !double.IsFinite(time) || time < _time ||
            !double.IsFinite(strength) || strength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(strength));
        int direction = Math.Sign(delta);
        if (direction != _direction) _rate = 0;
        _rate *= Math.Exp(-(time - _time) / 0.2);
        // Distance per time, so high-resolution wheels don't accelerate from packet count.
        _rate += Math.Abs(delta) / 120 / 0.2;
        _time = time;
        _direction = direction;
        double level = Math.Clamp((_rate - 6) / 8, 0, 1);
        double gain = 1 + strength * level * level * (3 - 2 * level);
        return delta * gain;
    }
}
