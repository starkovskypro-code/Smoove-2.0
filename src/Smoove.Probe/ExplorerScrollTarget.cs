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

    private ExplorerScrollTarget(AutomationElement view,ScrollPattern scroll, double pixelsPerWheelUnit, double scrollablePixels)
    {
        _view=view;_scroll=scroll; _pixelsPerWheelUnit=pixelsPerWheelUnit; _scrollablePixels=scrollablePixels;
    }

    internal static ExplorerScrollTarget? TryCreate(nint hit)
    {
        if (Native.ClassName(hit)!="DirectUIHWND")return null;
        nint parent=Native.GetParent(hit);
        if (Native.ClassName(parent)!="SHELLDLL_DefView")return null;
        try
        {
            var view=AutomationElement.FromHandle(hit);
            if(!view.TryGetCurrentPattern(ScrollPattern.Pattern,out var raw))return null;
            var scroll=(ScrollPattern)raw;
            var current=scroll.Current;
            if(!current.VerticallyScrollable || current.VerticalViewSize is <=0 or >=100)return null;
            var row=view.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
            double rowHeight=row?.Current.BoundingRectangle.Height ?? 0;
            double viewport=view.Current.BoundingRectangle.Height;
            uint lines=Native.WheelScrollLines();
            if(rowHeight<=0 || viewport<=0 || lines==0 || lines==uint.MaxValue)return null;
            double extent=viewport*(100/current.VerticalViewSize-1);
            return new(view,scroll,rowHeight*lines/120,extent);
        }
        catch(Exception ex)when(ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {return null;}
    }

    internal void BeginGesture()
    {
        var current=_scroll.Current;
        if(!current.VerticallyScrollable || current.VerticalViewSize is <=0 or >=100)throw new InvalidOperationException("Область больше не прокручивается.");
        _scrollablePixels=_view.Current.BoundingRectangle.Height*(100/current.VerticalViewSize-1);
        var row=_view.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
        double height=row?.Current.BoundingRectangle.Height??0;
        if(height<=0)throw new InvalidOperationException("Нет размеров строки.");
        _pixelsPerWheelUnit=height*Native.WheelScrollLines()/120;
        _position=current.VerticalScrollPercent/100*_scrollablePixels;
    }
    internal void Move(int wheelDelta)
    {
        _position=Math.Clamp(_position-wheelDelta*_pixelsPerWheelUnit,0,_scrollablePixels);
        _scroll.SetScrollPercent(ScrollPattern.NoScroll,_position/_scrollablePixels*100);
    }
}
