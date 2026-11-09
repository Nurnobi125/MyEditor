using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;

namespace VideoEditor.Services;

public sealed record CaptureArea(int X, int Y, int Width, int Height);
public sealed record CaptureWindowInfo(IntPtr Handle, string Title, int X, int Y, int Width, int Height);

public sealed class CaptureWindowPicker : Window
{
    readonly ListBox _windows = new() { Margin = new Thickness(12), DisplayMemberPath = nameof(CaptureWindowInfo.Title) };
    public CaptureWindowInfo? SelectedWindow => _windows.SelectedItem as CaptureWindowInfo;

    public CaptureWindowPicker()
    {
        Title = "Choose a window to record";
        Width = 520; Height = 440; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23));
        Foreground = Brushes.White;
        WindowUi.Apply(this);
        var root = new DockPanel();
        var heading = new TextBlock
        {
            Text = "Select an open window · keep it visible while recording",
            Margin = new Thickness(14, 12, 14, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xCD)),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var choose = new Button { Content = "Record window", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        choose.Click += (_, _) => AcceptSelection();
        buttons.Children.Add(cancel); buttons.Children.Add(choose);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        _windows.MouseDoubleClick += (_, _) => AcceptSelection();
        root.Children.Add(_windows);
        Content = root;
        Loaded += (_, _) => RefreshWindows();
    }

    void RefreshWindows()
    {
        _windows.ItemsSource = ListOpenWindows();
        if (_windows.Items.Count > 0) _windows.SelectedIndex = 0;
    }

    void AcceptSelection()
    {
        if (SelectedWindow == null)
        {
            MessageBox.Show(this, "Choose an open window first.", "Window recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }

    static IReadOnlyList<CaptureWindowInfo> ListOpenWindows()
    {
        var items = new List<CaptureWindowInfo>();
        uint thisProcess = (uint)Environment.ProcessId;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == thisProcess) return true;
            int length = GetWindowTextLength(handle);
            if (length <= 0) return true;
            var title = new System.Text.StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (title.Length == 0 || !GetClientRect(handle, out var client)) return true;
            var origin = new NativePoint { X = 0, Y = 0 };
            if (!ClientToScreen(handle, ref origin)) return true;
            int width = client.Right - client.Left, height = client.Bottom - client.Top;
            if (width < 120 || height < 80) return true;
            items.Add(new CaptureWindowInfo(handle, title.ToString(), origin.X, origin.Y, width, height));
            return true;
        }, IntPtr.Zero);
        return items.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
}

/// <summary>Lightweight primary-display drag selector for a gdigrab capture area.</summary>
public sealed class CaptureAreaWindow : Window
{
    readonly Canvas _canvas = new();
    readonly Rectangle _selection = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x42, 0xD6, 0xE8)),
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(28, 0x42, 0xD6, 0xE8)),
        Visibility = Visibility.Collapsed
    };
    readonly TextBlock _hint = new()
    {
        Text = "Drag to select a recording area · release to confirm · Esc to cancel",
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(210, 18, 22, 28)),
        Padding = new Thickness(14, 9, 14, 9),
        FontSize = 15,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 24, 0, 0)
    };
    Point _start;
    bool _dragging;

    public CaptureArea? SelectedArea { get; private set; }

    public CaptureAreaWindow()
    {
        Title = "Select capture area";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
        Cursor = Cursors.Cross;
        var grid = new Grid();
        grid.Children.Add(_canvas);
        grid.Children.Add(_hint);
        _canvas.Children.Add(_selection);
        Content = grid;
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _dragging = true;
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelection(e.GetPosition(_canvas));
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) UpdateSelection(e.GetPosition(_canvas));
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Point end = e.GetPosition(_canvas);
        double left = Math.Min(_start.X, end.X), top = Math.Min(_start.Y, end.Y);
        double width = Math.Abs(end.X - _start.X), height = Math.Abs(end.Y - _start.Y);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        int x = (int)Math.Round(left * transform.M11), y = (int)Math.Round(top * transform.M22);
        int w = (int)Math.Round(width * transform.M11), h = (int)Math.Round(height * transform.M22);
        if (w < 160 || h < 90)
        {
            _hint.Text = "Select an area at least 160 × 90 pixels · Esc to cancel";
            _hint.Background = new SolidColorBrush(Color.FromArgb(230, 130, 30, 40));
            return;
        }
        SelectedArea = new CaptureArea(x, y, w, h);
        DialogResult = true;
        Close();
        e.Handled = true;
    }

    void UpdateSelection(Point point)
    {
        double left = Math.Min(_start.X, point.X), top = Math.Min(_start.Y, point.Y);
        _selection.Width = Math.Abs(point.X - _start.X);
        _selection.Height = Math.Abs(point.Y - _start.Y);
        Canvas.SetLeft(_selection, left);
        Canvas.SetTop(_selection, top);
        _hint.Text = $"{(int)(_selection.Width)} × {(int)(_selection.Height)} · release to confirm · Esc to cancel";
        _hint.Background = new SolidColorBrush(Color.FromArgb(210, 18, 22, 28));
    }
}
