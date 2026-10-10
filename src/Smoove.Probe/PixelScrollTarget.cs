using System.Windows.Automation;

namespace Smoove.Probe;

// Standard scroll providers avoid wheel-to-line rounding in supported native controls.
internal sealed class PixelScrollTarget
{
    private readonly ScrollPattern _scroll;
    private readonly AutomationElement _view;
    private readonly bool _notifyScroll;
    private double _pixelsPerWheelUnit;
    private double _scrollablePixels;
    private double _position;
    private bool _horizontal;
    internal nint Handle { get; }

    private PixelScrollTarget(AutomationElement view,ScrollPattern scroll)
    {
        _view=view;_scroll=scroll;_notifyScroll=Native.ClassName(view.Current.NativeWindowHandle).StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase); Handle=view.Current.NativeWindowHandle;
    }

    internal static PixelScrollTarget? TryCreate(nint hit)
    {
        hit=TargetHandle(hit);
        if(hit==0)return null;
        try
        {
            var view=AutomationElement.FromHandle(hit);
            object raw;
            if(!view.TryGetCurrentPattern(ScrollPattern.Pattern,out raw))
            {
                if(!Native.ClassName(hit).StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase))return null;
                var bounds=view.Current.BoundingRectangle;
                var parent=TreeWalker.RawViewWalker.GetParent(view);
                if(parent is null)return null;
                var candidates=parent.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty,true));
                AutomationElement? provider=null;
                foreach(AutomationElement candidate in candidates)
                {
                    var rectangle=candidate.Current.BoundingRectangle;
                    if(rectangle.Height>=bounds.Height*0.8 && rectangle.Top<=bounds.Top+4 && rectangle.Bottom>=bounds.Bottom-4 &&
                        rectangle.Left>=bounds.Left && rectangle.Left<=bounds.Right+4)
                    {provider=candidate;break;}
                }
                if(provider is null || !provider.TryGetCurrentPattern(ScrollPattern.Pattern,out raw))return null;
            }
            var scroll=(ScrollPattern)raw;
            var target=new PixelScrollTarget(view,scroll);
            target.BeginGesture();
            return target;
        }
        catch(Exception ex)when(ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {return null;}
    }

    internal static nint TargetHandle(nint hit)
    {
        for(nint window=hit;window!=0;window=Native.GetParent(window))
        {
            string name=Native.ClassName(window);
            if(name.StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase) ||
                name=="DirectUIHWND" && Native.ClassName(Native.GetParent(window))=="SHELLDLL_DefView")return window;
        }
        return 0;
    }

    internal PixelScrollTarget ForAxis() => new(_view, _scroll);

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
        if(!_horizontal && _notifyScroll)
        {
            // Notify the native host after changing RichEdit's offset directly.
            Native.PostMessageW(Native.GetParent(Handle),0x0111,
                unchecked((nuint)((0x0602u<<16)|(uint)(Native.GetDlgCtrlID(Handle)&0xFFFF))),Handle);
        }
    }
}
