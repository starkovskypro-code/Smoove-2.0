using Smoove.Core;

internal static class ReaperWheelChecks
{
    internal static void Run()
    {
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        Require(ScrollRouting.MotionScale(2,true)==0.3 && ScrollRouting.MotionScale(1.8,true)==0.27 && ScrollRouting.MotionScale(2,false)==2,
            "MIDI editor distance scale leaked to REAPER's other areas.");
        foreach(int sign in new[]{-1,1})
        {
            Require(ScrollRouting.OutputDelta(sign*2,false,false,true)==sign*2 &&
                ScrollRouting.OutputDelta(sign*2,true,false,true)==sign*2,"REAPER Shift release reversed horizontal travel.");
            Require(ScrollRouting.OutputDelta(sign*2,true,true,true)==-sign*2 &&
                ScrollRouting.OutputDelta(sign*2,true,false,false)==-sign*2,"Native horizontal or other application direction changed.");
        }
        foreach(int sign in new[]{-1,1})
        foreach(int quantum in new[]{2,4,12,60,120})
        {
            var output=new ReaperWheelOutput();
            int sum=0;
            for(int i=0;i<240;i++)
            {
                int delta=output.Take(sign,quantum);
                Require(delta==0 || delta==sign*quantum,"REAPER tail reversed or emitted a sub-quantum unit.");
                sum+=delta;
                Require(Math.Abs(sum-sign*(i+1))<quantum,"REAPER output accumulated a full packet of delay.");
            }
            Require(sum==sign*240,"REAPER output lost a full-notch distance.");
            int first=output.Take(sign,quantum),second=output.Take(-sign,quantum);
            Require(first+second==0,"Odd residue survived a cancelling reversal.");
            output.Take(sign,quantum);output.Reset();
            Require(output.Take(-sign,quantum)==new ReaperWheelOutput().Take(-sign,quantum),"Cancelled residue leaked into a new recipient.");
        }
        var large=new ReaperWheelOutput();
        Require((long)large.Take(int.MaxValue)+large.Take(1)==(long)int.MaxValue+1,"Large REAPER delta overflowed.");
        Require((long)large.Take(int.MinValue)+large.Take(0)==int.MinValue,"Negative REAPER delta overflowed.");
        var huge=new ReaperWheelOutput();
        int hugePart=huge.Take(int.MaxValue,12);
        Require(hugePart%12==0 && huge.Take(0,12)%12==0,"Large split emitted an unsafe REAPER packet.");
        foreach(int sign in new[]{-1,1})
        foreach(bool experimental in new[]{false,true})
        foreach(int hz in new[]{60,118,500})
        foreach(bool fast in new[]{false,true})
        {
            IScrollMotion motion=experimental ? new SmoothieMotion() : new ScrollMotion();
            if(motion is SmoothieMotion smoothie)smoothie.Configure(new(2,fast ? 20 : 200,0,0));
            else ((ScrollMotion)motion).Configure(fast ? new(0.005,0.005) : new(0.01,1),fast ? 8 : 6);
            var packets=new ReaperWheelOutput();
            motion.Add(sign*120*(experimental ? 1 : 2),0);
            int sum=0;var tailTimes=new List<double>();
            for(int tick=1;tick<hz*4;tick++)
            {
                double time=tick/(double)hz;
                motion.Advance(time);
                double remaining=motion is ScrollMotion s ? s.Target-s.Position : ((SmoothieMotion)motion).Remaining;
                int packet=packets.Take(motion.TakeDelta(),4,remaining,time);
                Require(packet*sign>=0 && packet%4==0,"Finite REAPER ending reversed or emitted an unsafe packet.");
                if(packet!=0){sum+=packet;if(Math.Abs(sum)>=224)tailTimes.Add(time);}
            }
            Require(sum==sign*240 && !packets.Active,"Finite REAPER ending lost distance or stayed active.");
            Require(tailTimes.Count>=1 && tailTimes.Zip(tailTimes.Skip(1)).All(p=>p.Second-p.First<=0.085),$"Finite REAPER ending contains a late isolated jump: experimental={experimental}, fast={fast}, hz={hz}, times={string.Join(",",tailTimes)}.");
        }
        Console.WriteLine("PASS: REAPER output: no +/-1 messages, conserved distance, reversal, cancellation and integer bounds");
    }
}
