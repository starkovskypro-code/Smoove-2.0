namespace Smoove.Core;

public enum MotionEngineKind { Smoove, SmoothieExperimental }

public interface IScrollMotion
{
    bool Active { get; }
    double Time { get; }
    void Add(double delta, double seconds);
    void Advance(double seconds);
    int TakeDelta();
    void Cancel(double seconds);
}
