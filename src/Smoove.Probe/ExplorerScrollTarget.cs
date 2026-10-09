using System.Windows.Automation;

namespace Smoove.Probe;

// Explorer's file view quantizes wheel input. Its UIA provider exposes pixel positions.
internal sealed class ExplorerScrollTarget
{
    private readonly ScrollPattern _scroll;
    private readonly AutomationElement _view;
    private double _pixelsPerWheelUnit;
    private double _scrollablePixels;
    private double _position;
    private bool _horizontal;
    internal nint Handle { get; }

    private ExplorerScrollTarget(AutomationElement view,ScrollPattern scroll)
    {
        _view=view;_scroll=scroll; Handle=view.Current.NativeWindowHandle;
    }

    internal static ExplorerScrollTarget? TryCreate(nint hit)
    {
        hit=FileViewHandle(hit);
        if(hit==0)return null;
        try
        {
            var view=AutomationElement.FromHandle(hit);
            if(!view.TryGetCurrentPattern(ScrollPattern.Pattern,out var raw))return null;
            var scroll=(ScrollPattern)raw;
            var target=new ExplorerScrollTarget(view,scroll);
            target.BeginGesture();
            return target;
        }
        catch(Exception ex)when(ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {return null;}
    }

    internal static nint FileViewHandle(nint hit)
    {
        for(nint window=hit;window!=0;window=Native.GetParent(window))
            if(Native.ClassName(window)=="DirectUIHWND" && Native.ClassName(Native.GetParent(window))=="SHELLDLL_DefView")return window;
        return 0;
    }

    internal ExplorerScrollTarget ForAxis() => new(_view, _scroll);

    internal void BeginGesture(bool horizontalWheel = false)
    {
        var current=_scroll.Current;
        _horizontal=horizontalWheel || (!current.VerticallyScrollable && current.HorizontallyScrollable);
        if(horizontalWheel && !current.HorizontallyScrollable)
        {_pixelsPerWheelUnit=0;return;}
        double viewSize=_horizontal?current.HorizontalViewSize:current.VerticalViewSize;
        if((!current.VerticallyScrollable && !_horizontal) || viewSize is <=0 or >=100)throw new InvalidOperationException("Область больше не прокручивается.");
        var bounds=_view.Current.BoundingRectangle;
        double viewport=_horizontal?bounds.Width:bounds.Height;
        _scrollablePixels=viewport*(100/viewSize-1);
        if(viewport<=0)throw new InvalidOperationException("Нет размеров области.");
        uint lines=horizontalWheel?Native.WheelScrollChars():Native.WheelScrollLines();
        if(lines==0)throw new InvalidOperationException("Системная прокрутка отключена.");
        // A wheel line is a fixed 20 DIP distance, never the height of a thumbnail.
        double size=20*Math.Max(96,Native.GetDpiForWindow(Handle))/96.0;
        _pixelsPerWheelUnit=(lines==uint.MaxValue?viewport:size*lines)/120;
        _position=(_horizontal?current.HorizontalScrollPercent:current.VerticalScrollPercent)/100*_scrollablePixels;
    }
    internal void Move(int wheelDelta)
    {
        if(_pixelsPerWheelUnit==0)return;
        _position=Math.Clamp(_position-wheelDelta*_pixelsPerWheelUnit,0,_scrollablePixels);
        double percent=_position/_scrollablePixels*100;
        _scroll.SetScrollPercent(_horizontal?percent:ScrollPattern.NoScroll,_horizontal?ScrollPattern.NoScroll:percent);
    }
}
