namespace Smoove.Core;

// Small wheel packets can reverse in REAPER at low zoom. Round cumulative output, retaining residue.
public sealed class ReaperWheelOutput
{
    private long _pending;
    private bool _finishing;
    private double _finishStart;
    private int _finishDistance,_finishSent;
    public bool Active => _finishing && _finishSent!=_finishDistance;

    public void AddInput() => _finishing=false;

    public int Take(int delta,int quantum=4,double remaining=double.NaN,double seconds=0)
    {
        if(quantum is not (2 or 4 or 12 or 60 or 120))throw new ArgumentOutOfRangeException(nameof(quantum));
        _pending += delta;
        if(!_finishing && double.IsFinite(remaining) && Math.Abs(remaining)>0 && Math.Abs(_pending+remaining)<=quantum*4)
        {
            _finishing=true;_finishStart=seconds;_finishSent=0;
            _finishDistance=(int)Math.Round((_pending+remaining)/quantum,MidpointRounding.AwayFromZero)*quantum;
        }
        if(_finishing)
        {
            double u=Math.Clamp((seconds-_finishStart)/0.12,0,1);
            int target=(int)Math.Round(_finishDistance*(2*u-u*u)/quantum,MidpointRounding.AwayFromZero)*quantum;
            int part=target-_finishSent;_finishSent=target;_pending-=part;
            return part;
        }
        // Do not emit a correction against an unchanged trajectory at an exact rounding midpoint.
        if(delta==0 && Math.Abs(_pending)<quantum)return 0;
        long magnitude=(Math.Abs(_pending)+quantum/2)/quantum*quantum;
        long limit=int.MaxValue/quantum*quantum;
        int output=(int)(Math.Sign(_pending)*Math.Min(magnitude,limit));
        _pending -= output;
        return output;
    }

    public void Reset(){_pending=0;_finishing=false;}
}
