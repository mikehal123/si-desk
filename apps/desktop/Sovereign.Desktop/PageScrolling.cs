using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sovereign.Desktop;

public sealed partial class MainWindow
{
    // Preserve each page's layout while making content wider than the pane reachable.
    private static ScrollViewer PageScroll(FrameworkElement content, double minimumWidth = 0, bool centered = false, bool fillHeight = false)
    {
        var scroll = new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, Visibility = content.Visibility
        };
        void Fit()
        {
            var available = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            if (available <= 0) return;
            var width = Math.Max(minimumWidth, Math.Min(content.MaxWidth, available - content.Margin.Left - content.Margin.Right));
            if (double.IsNaN(content.Width) || Math.Abs(content.Width - width) > .5) content.Width = width;
            if (fillHeight)
            {
                var height = Math.Max(content.MinHeight, scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight);
                if (height > 0 && (double.IsNaN(content.Height) || Math.Abs(content.Height - height) > .5)) content.Height = height;
            }
        }
        content.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => { scroll.Visibility = content.Visibility; Fit(); });
        scroll.Loaded += (_, _) => Fit(); scroll.SizeChanged += (_, _) => Fit();
        scroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => Fit());
        if (fillHeight) scroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, (_, _) => Fit());
        return scroll;
    }
}
