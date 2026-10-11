using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Sovereign.Desktop;

internal sealed class SplitterBar : ContentControl
{
    private static readonly SolidColorBrush Idle = DeskTheme.Divider;
    private static readonly SolidColorBrush Hot = DeskTheme.Accent;
    private readonly Border _fill = new()
    {
        Background = Idle,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly bool _horizontal;
    private double _lastPosition;
    private uint? _pointerId;
    private bool _moved;

    public event Action<double>? DragDelta;
    public event Action? DragCompleted;
    public event Action? ResetWidth;
    public event Action<int>? Nudge;

    public SplitterBar(bool horizontal = false)
    {
        _horizontal = horizontal;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        if (horizontal)
        {
            Height = Client.DeskSplit.Grip;
            MinHeight = Client.DeskSplit.Grip;
            _fill.Height = 1;
            _fill.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            Width = Client.DeskSplit.Grip;
            MinWidth = Client.DeskSplit.Grip;
            _fill.Width = 1;
            _fill.HorizontalAlignment = HorizontalAlignment.Center;
        }
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // The full transparent surface receives input, rather than just the 1px line.
        Content = new Grid { Background = DeskTheme.Transparent, Children = { _fill } };
        Background = DeskTheme.Transparent;
        _fill.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(DeskTheme.MotionMilliseconds) };
        ProtectedCursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnBarPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnBarMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnBarReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnBarReleased), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnBarReleased), true);
        AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnBarDoubleTapped), true);
        Unloaded += (_, _) => FinishDrag();
    }

    private void Paint(SolidColorBrush brush)
    {
        Background = DeskTheme.Transparent;
        _fill.Background = brush;
    }

    private void OnBarPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId.HasValue || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _lastPosition = Position(e);
        if (!CapturePointer(e.Pointer)) return;
        _pointerId = e.Pointer.PointerId;
        _moved = false;
        Focus(FocusState.Pointer);
        Paint(Hot);
        e.Handled = true;
    }

    private void OnBarMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        var position = Position(e);
        var delta = position - _lastPosition;
        if (double.IsFinite(delta) && Math.Abs(delta) >= 0.5)
        {
            _lastPosition = position;
            _moved = true;
            DragDelta?.Invoke(delta);
        }
        e.Handled = true;
    }

    private void OnBarReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        FinishDrag();
        e.Handled = true;
    }

    private void OnBarDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        FinishDrag(complete: false);
        ResetWidth?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        Paint(Hot);
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (!_pointerId.HasValue) Paint(Idle);
        base.OnPointerExited(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var backward = _horizontal ? VirtualKey.Up : VirtualKey.Left;
        var forward = _horizontal ? VirtualKey.Down : VirtualKey.Right;
        if (e.Key == backward) { Nudge?.Invoke(-1); e.Handled = true; }
        else if (e.Key == forward) { Nudge?.Invoke(1); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void FinishDrag(bool complete = true)
    {
        if (!_pointerId.HasValue) return;
        var moved = _moved;
        _pointerId = null;
        _moved = false;
        ReleasePointerCaptures();
        Paint(Idle);
        if (complete && moved) DragCompleted?.Invoke();
    }

    private double Position(PointerRoutedEventArgs e)
    {
        var anchor = XamlRoot?.Content as UIElement ?? this;
        var position = e.GetCurrentPoint(anchor).Position;
        return _horizontal ? position.Y : position.X;
    }
}
