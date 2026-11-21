using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grab.Controls;

/// <summary>
/// Timeline with a draggable start handle, end handle and a playhead.
/// Click anywhere on the bar to seek; drag the white handles to set the trim range.
/// </summary>
public class TrimTimeline : FrameworkElement
{
    const double Pad = 6;
    const double HandleW = 14;
    const double MinLength = 0.2;

    static readonly Brush TrackBrush = Make("#262626");
    static readonly Brush DimBrush = Make("#1A1A1A");
    static readonly Brush FillBrush = Make("#3A3A3A");
    static readonly Brush HandleBrush = Make("#EDEDED");
    static readonly Brush GripBrush = Make("#111111");
    static readonly Brush PlayheadBrush = Make("#7CB0FF");
    static readonly Brush LabelBrush = Make("#111111");

    static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    double _duration;
    double _start;
    double _end;
    double _position;
    bool _rangeEnabled;
    int _drag;   // 0 none, 1 start, 2 end, 3 playhead

    public event Action<double, double> RangeChanged;   // start, end
    public event Action<double> SeekRequested;          // seconds

    public TrimTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public double Duration
    {
        get => _duration;
        set { _duration = Math.Max(0, value); InvalidateVisual(); }
    }

    public double Start
    {
        get => _start;
        set { _start = Math.Max(0, value); InvalidateVisual(); }
    }

    public double End
    {
        get => _end;
        set { _end = Math.Max(0, value); InvalidateVisual(); }
    }

    public double Position
    {
        get => _position;
        set { _position = Math.Max(0, value); InvalidateVisual(); }
    }

    public bool RangeEnabled
    {
        get => _rangeEnabled;
        set { _rangeEnabled = value; InvalidateVisual(); }
    }

    double Left => Pad + HandleW;
    double Right => Math.Max(Left + 1, ActualWidth - Pad - HandleW);

    double X(double t)
    {
        if (_duration <= 0) return Left;
        return Left + Math.Min(1, Math.Max(0, t / _duration)) * (Right - Left);
    }

    double T(double x)
    {
        if (_duration <= 0) return 0;
        var f = (x - Left) / (Right - Left);
        return Math.Min(1, Math.Max(0, f)) * _duration;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // mouse hit area

        double trackH = 10;
        double trackY = (h - trackH) / 2;
        var track = new Rect(Left, trackY, Math.Max(0, Right - Left), trackH);
        dc.DrawRoundedRectangle(_rangeEnabled ? DimBrush : TrackBrush, null, track, 5, 5);

        if (_duration > 0 && _rangeEnabled)
        {
            double xs = X(_start), xe = X(Math.Min(_end > 0 ? _end : _duration, _duration));
            if (xe < xs) xe = xs;

            // selected part of the track
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(xs, trackY, xe - xs, trackH), 5, 5);

            // frame around the selection
            var framePen = new Pen(HandleBrush, 2);
            framePen.Freeze();
            double fy = 4;
            dc.DrawLine(framePen, new Point(xs, fy), new Point(xe, fy));
            dc.DrawLine(framePen, new Point(xs, h - fy), new Point(xe, h - fy));

            DrawHandle(dc, xs - HandleW, h, true);
            DrawHandle(dc, xe, h, false);
        }

        if (_duration > 0)
        {
            double xp = X(_position);
            var pen = new Pen(PlayheadBrush, 2);
            pen.Freeze();
            dc.DrawLine(pen, new Point(xp, 3), new Point(xp, h - 3));
            dc.DrawEllipse(PlayheadBrush, null, new Point(xp, 3), 4.5, 4.5);
        }
    }

    void DrawHandle(DrawingContext dc, double x, double h, bool isStart)
    {
        var rect = new Rect(x, 4, HandleW, h - 8);
        dc.DrawRoundedRectangle(HandleBrush, null, rect, 5, 5);
        var grip = new Pen(GripBrush, 2);
        grip.Freeze();
        double cx = x + HandleW / 2;
        dc.DrawLine(grip, new Point(cx, h / 2 - 7), new Point(cx, h / 2 + 7));
    }

    bool OverStartHandle(double x)
    {
        double xs = X(_start);
        return x >= xs - HandleW - 4 && x <= xs + 2;
    }

    bool OverEndHandle(double x)
    {
        double xe = X(_end > 0 ? _end : _duration);
        return x >= xe - 2 && x <= xe + HandleW + 4;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        var p = e.GetPosition(this);
        _drag = 3;
        if (_rangeEnabled)
        {
            if (OverStartHandle(p.X)) _drag = 1;
            else if (OverEndHandle(p.X)) _drag = 2;
        }
        CaptureMouse();
        Apply(p.X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag != 0 && IsMouseCaptured)
        {
            Apply(p.X);
            return;
        }
        Cursor = _rangeEnabled && (OverStartHandle(p.X) || OverEndHandle(p.X)) ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = 0;
        ReleaseMouseCapture();
    }

    void Apply(double x)
    {
        double t = T(x);
        if (_end <= 0) _end = _duration;

        if (_drag == 1)
        {
            _start = Math.Max(0, Math.Min(t, _end - MinLength));
            RangeChanged?.Invoke(_start, _end);
        }
        else if (_drag == 2)
        {
            _end = Math.Min(_duration, Math.Max(t, _start + MinLength));
            RangeChanged?.Invoke(_start, _end);
        }
        else if (_drag == 3)
        {
            _position = t;
            SeekRequested?.Invoke(t);
        }
        InvalidateVisual();
    }
}
