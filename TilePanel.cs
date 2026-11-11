using System.Windows;
using System.Windows.Controls;

namespace ClaudeTaskBoard;

// Session cards in as many equal columns as the width allows, filled left to right then down (Z-wise):
// one column in a narrow board, a row of cards in a wide and short one, a grid in a large one.
// Each row is as tall as its tallest card. Cards with no height (gone, or collapsed while animating out)
// take no slot, so the others close up.
public sealed class TilePanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(TilePanel),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(TilePanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }

    public static int ColumnsFor(double width, double minItem, double gap) =>
        double.IsFinite(width) && width > 0 ? Math.Max(1, (int)((width + gap) / (minItem + gap))) : 1;

    double _itemWidth;
    int _cols = 1;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : MinItemWidth;
        _cols = ColumnsFor(width, MinItemWidth, ColumnGap);
        _itemWidth = Math.Max(0, (width - ColumnGap * (_cols - 1)) / _cols);

        double height = 0, row = 0;
        int slot = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            if (child.DesiredSize.Height <= 0) continue;
            row = Math.Max(row, child.DesiredSize.Height);
            if (++slot % _cols == 0) { height += row; row = 0; }
        }
        return new Size(width, height + row);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0, row = 0;
        int slot = 0;
        var inRow = new List<UIElement>();
        void Flush()
        {
            for (int c = 0; c < inRow.Count; c++)
                inRow[c].Arrange(new Rect(c * (_itemWidth + ColumnGap), y, _itemWidth, row));
            y += row;
            row = 0;
            inRow.Clear();
        }
        foreach (UIElement child in InternalChildren)
        {
            if (child.DesiredSize.Height <= 0) { child.Arrange(new Rect(0, y, _itemWidth, 0)); continue; }
            inRow.Add(child);
            row = Math.Max(row, child.DesiredSize.Height);
            if (++slot % _cols == 0) Flush();
        }
        if (inRow.Count > 0) Flush();
        return final;
    }
}
