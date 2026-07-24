using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SystemPulse.Controls;

public sealed class Sparkline : FrameworkElement
{
    private INotifyCollectionChanged? _observedCollection;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 1 || ActualHeight <= 1)
            return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1);
        drawingContext.DrawLine(gridPen, new Point(0, ActualHeight * .33), new Point(ActualWidth, ActualHeight * .33));
        drawingContext.DrawLine(gridPen, new Point(0, ActualHeight * .66), new Point(ActualWidth, ActualHeight * .66));

        var values = Values?.Cast<object>()
            .Select(value => Convert.ToDouble(value))
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .ToArray() ?? [];

        if (values.Length == 0)
        {
            var placeholderPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 149, 157, 175)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawLine(placeholderPen, new Point(0, ActualHeight / 2), new Point(ActualWidth, ActualHeight / 2));
            return;
        }

        var range = Math.Max(1, Maximum - Minimum);
        var points = values.Select((value, index) => new Point(
            values.Length == 1 ? ActualWidth : index * ActualWidth / (values.Length - 1),
            ActualHeight - Math.Clamp((value - Minimum) / range, 0, 1) * (ActualHeight - 4) - 2)).ToArray();

        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, ActualHeight), true, true);
            context.LineTo(points[0], true, false);
            foreach (var point in points.Skip(1))
                context.LineTo(point, true, false);
            context.LineTo(new Point(points[^1].X, ActualHeight), true, false);
        }
        drawingContext.DrawGeometry(Fill, null, area);

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1))
                context.LineTo(point, true, false);
        }

        drawingContext.DrawGeometry(null, new Pen(Stroke, 2), line);
        drawingContext.DrawEllipse(Stroke, null, points[^1], 3, 3);
    }

    private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var chart = (Sparkline)dependencyObject;
        chart.StopObserving();
        chart._observedCollection = eventArgs.NewValue as INotifyCollectionChanged;
        if (chart._observedCollection is not null)
            chart._observedCollection.CollectionChanged += chart.OnCollectionChanged;
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) => InvalidateVisual();

    private void StopObserving()
    {
        if (_observedCollection is not null)
            _observedCollection.CollectionChanged -= OnCollectionChanged;
        _observedCollection = null;
    }
}
