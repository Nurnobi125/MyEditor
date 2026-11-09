using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoEditor.Services;

/// <summary>Shared contrast and work-area fixes for editor tool windows.</summary>
internal static class WindowUi
{
    public static void Apply(Window window)
    {
        var normal = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x33));
        var hover = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x62));
        var selected = new SolidColorBrush(Color.FromRgb(0x4A, 0x3B, 0x78));
        var text = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF2));

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, text));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, normal));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(hoverTrigger);
        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, selected));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(selectedTrigger);
        window.Resources[typeof(ComboBoxItem)] = itemStyle;

        window.Loaded += (_, _) => KeepOnScreen(window);
    }

    static void KeepOnScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        if (window.ActualWidth > area.Width) window.Width = area.Width;
        if (window.ActualHeight > area.Height) window.Height = area.Height;

        double left = double.IsFinite(window.Left) ? window.Left : area.Left;
        double top = double.IsFinite(window.Top) ? window.Top : area.Top;
        window.Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
        window.Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - window.ActualHeight));
    }
}
