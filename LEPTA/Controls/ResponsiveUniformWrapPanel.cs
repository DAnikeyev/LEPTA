using System.Windows;
using System.Windows.Controls;

namespace LEPTA.Controls;

public sealed class ResponsiveUniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth),
        typeof(double),
        typeof(ResponsiveUniformWrapPanel),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(ResponsiveUniformWrapPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(ResponsiveUniformWrapPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        if (children.Length == 0)
        {
            return new Size(0, 0);
        }

        var availableWidth = NormalizeWidth(availableSize.Width, children.Length);
        var columns = CalculateColumns(availableWidth, children.Length);
        var itemWidth = CalculateItemWidth(availableWidth, columns);
        var rowHeight = 0d;
        var totalHeight = 0d;
        var columnIndex = 0;

        foreach (var child in children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            columnIndex++;
            if (columnIndex < columns)
            {
                continue;
            }

            totalHeight += rowHeight;
            rowHeight = 0;
            columnIndex = 0;
        }

        if (columnIndex > 0)
        {
            totalHeight += rowHeight;
        }

        var rowCount = (int)Math.Ceiling(children.Length / (double)columns);
        if (rowCount > 1)
        {
            totalHeight += VerticalSpacing * (rowCount - 1);
        }

        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        if (children.Length == 0)
        {
            return finalSize;
        }

        var availableWidth = NormalizeWidth(finalSize.Width, children.Length);
        var columns = CalculateColumns(availableWidth, children.Length);
        var itemWidth = CalculateItemWidth(availableWidth, columns);
        var rowHeight = 0d;
        var y = 0d;
        var columnIndex = 0;
        var rowChildren = new List<UIElement>(columns);

        foreach (var child in children)
        {
            rowChildren.Add(child);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            columnIndex++;
            if (columnIndex < columns)
            {
                continue;
            }

            ArrangeRow(rowChildren, itemWidth, rowHeight, y);
            y += rowHeight + VerticalSpacing;
            rowHeight = 0d;
            columnIndex = 0;
            rowChildren.Clear();
        }

        if (rowChildren.Count > 0)
        {
            ArrangeRow(rowChildren, itemWidth, rowHeight, y);
            y += rowHeight;
        }
        else if (y > 0)
        {
            y -= VerticalSpacing;
        }

        return new Size(availableWidth, Math.Max(y, 0));
    }

    private int CalculateColumns(double availableWidth, int itemCount)
    {
        var minWidth = Math.Max(MinItemWidth, 1d);
        var spacing = Math.Max(HorizontalSpacing, 0d);
        var rawColumns = (availableWidth + spacing) / (minWidth + spacing);
        return Math.Max(1, Math.Min(itemCount, (int)Math.Floor(rawColumns)));
    }

    private double CalculateItemWidth(double availableWidth, int columns)
    {
        if (columns <= 1)
        {
            return Math.Max(availableWidth, MinItemWidth);
        }

        return Math.Max(MinItemWidth, (availableWidth - (HorizontalSpacing * (columns - 1))) / columns);
    }

    private double NormalizeWidth(double width, int itemCount)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return (Math.Max(MinItemWidth, 1d) * Math.Max(itemCount, 1)) + (HorizontalSpacing * Math.Max(itemCount - 1, 0));
        }

        return width;
    }

    private void ArrangeRow(IReadOnlyList<UIElement> rowChildren, double itemWidth, double rowHeight, double top)
    {
        var left = 0d;
        foreach (var child in rowChildren)
        {
            child.Arrange(new Rect(left, top, itemWidth, rowHeight));
            left += itemWidth + HorizontalSpacing;
        }
    }
}



