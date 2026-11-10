using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PreySense.Frontend.Controls;

public sealed class ThemedIcon : Control
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ThemedIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    private ImageBrush? _cachedMaskBrush;

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThemedIcon icon)
        {
            if (e.NewValue is ImageSource src)
            {
                var brush = new ImageBrush(src) { Stretch = Stretch.Uniform };
                if (brush.CanFreeze) brush.Freeze();
                icon._cachedMaskBrush = brush;
            }
            else
            {
                icon._cachedMaskBrush = null;
            }
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_cachedMaskBrush == null || ActualWidth <= 0 || ActualHeight <= 0) return;

        drawingContext.PushOpacityMask(_cachedMaskBrush);
        drawingContext.DrawRectangle(Foreground, null, new Rect(0, 0, ActualWidth, ActualHeight));
        drawingContext.Pop();
    }
}
