using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RigUninstaller.Views;

/// <summary>
/// Grey placeholder rows with a soft light sweeping across them. Shown while a list loads, so the page keeps its
/// shape instead of jumping. The sweep stops when the control is hidden, and is off when Windows animations are off.
/// </summary>
public sealed class SkeletonRows : UserControl
{
    public static readonly DependencyProperty RowCountProperty = DependencyProperty.Register(
        nameof(RowCount), typeof(int), typeof(SkeletonRows), new PropertyMetadata(8, (d, _) => ((SkeletonRows)d).Build()));

    public static readonly DependencyProperty ShowTickProperty = DependencyProperty.Register(
        nameof(ShowTick), typeof(bool), typeof(SkeletonRows), new PropertyMetadata(true, (d, _) => ((SkeletonRows)d).Build()));

    private const double RowHeight = 42;

    // Share of the cell each bar fills, repeated down the rows so the placeholder does not look like a grid of clones.
    private static readonly double[] NameWidths = { 0.62, 0.48, 0.74, 0.55, 0.68, 0.42, 0.58 };
    private static readonly double[] PublisherWidths = { 0.55, 0.7, 0.45, 0.62, 0.5, 0.66 };

    private readonly TranslateTransform _sweep = new();
    private readonly LinearGradientBrush _shimmer;

    public SkeletonRows()
    {
        IsHitTestVisible = false;
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Top;

        _shimmer = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = _sweep,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
            },
        };

        Loaded += (_, _) => UpdateSweep();
        Unloaded += (_, _) => StopSweep();
        IsVisibleChanged += (_, _) => UpdateSweep();
        Build();
    }

    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    /// <summary>False for lists without a tick-box column (Store apps, Startup).</summary>
    public bool ShowTick
    {
        get => (bool)GetValue(ShowTickProperty);
        set => SetValue(ShowTickProperty, value);
    }

    private void Build()
    {
        var panel = new StackPanel();
        for (var i = 0; i < Math.Max(0, RowCount); i++)
        {
            panel.Children.Add(BuildRow(i));
        }

        Content = panel;
    }

    private UIElement BuildRow(int index)
    {
        var row = new Grid { Height = RowHeight };
        foreach (var width in new[]
                 {
                     new GridLength(ShowTick ? 44 : 0), new GridLength(52), new GridLength(2.2, GridUnitType.Star),
                     new GridLength(1.6, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
                 })
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        Grid.SetColumnSpan(divider, 6);
        row.Children.Add(divider);

        if (ShowTick)
        {
            var tick = Bar(16, 16, 4);
            tick.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(tick, 0);
            row.Children.Add(tick);
        }

        var tile = Bar(28, 28, 7);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(tile, 1);
        row.Children.Add(tile);

        AddBar(row, 2, 12, NameWidths[index % NameWidths.Length]);
        AddBar(row, 3, 10, PublisherWidths[index % PublisherWidths.Length]);
        AddBar(row, 4, 10, 0.5);
        AddBar(row, 5, 10, 0.4);
        return row;
    }

    private void AddBar(Grid row, int column, double height, double share)
    {
        // A grid inside the cell gives the bar a share of the cell's width.
        var cell = new Grid { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share, GridUnitType.Star) });
        var bar = Bar(double.NaN, height, 4);
        cell.Children.Add(bar);
        Grid.SetColumn(cell, column);
        row.Children.Add(cell);
    }

    private Border Bar(double width, double height, double radius)
    {
        var bar = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = new Border { Background = _shimmer },
        };
        if (!double.IsNaN(width))
        {
            bar.Width = width;
        }

        bar.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        return bar;
    }

    private void UpdateSweep()
    {
        if (IsVisible && IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1400))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            StopSweep();
        }
    }

    private void StopSweep() => _sweep.BeginAnimation(TranslateTransform.XProperty, null);
}
