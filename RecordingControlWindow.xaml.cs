using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VideoEditor;

public partial class RecordingControlWindow : Window
{
    bool _allowClose;
    readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    public event EventHandler? PauseResumeRequested;
    public event EventHandler? StopRequested;

    public RecordingControlWindow(string outputPath, string recordingDescription)
    {
        InitializeComponent();
        Closing += ControlWindow_Closing;
        SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x00000011);
        _autoHideTimer.Tick += (_, _) => { _autoHideTimer.Stop(); if (!IsMouseOver) Hide(); };
        MouseEnter += (_, _) => _autoHideTimer.Stop();
        MouseLeave += (_, _) => StartAutoHide();
        FileText.Text = System.IO.Path.GetFileName(outputPath) + " · " + recordingDescription;
        Loaded += (_, _) =>
        {
            Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2);
            Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height - 28);
        };
    }

    public void UpdateRecordingState(bool paused, double elapsedSeconds)
    {
        StateText.Text = paused ? "PAUSED" : "RECORDING";
        StateDot.Foreground = paused ? System.Windows.Media.Brushes.Gold : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x4B, 0x58));
        ElapsedText.Text = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds)).ToString(@"hh\:mm\:ss");
        PauseResumeButton.Content = paused ? "▶  Resume" : "Ⅱ  Pause";
    }

    /// <summary>Live line under the timer, e.g. "128.4 MB · 0 dropped · 1.00x".</summary>
    public void UpdateStats(string text, bool warning)
    {
        StatsText.Text = text;
        StatsText.Foreground = warning
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB4, 0x54))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x62, 0xD5, 0xE1));
    }

    public void StartAutoHide() { _autoHideTimer.Stop(); if (IsVisible && !IsMouseOver) _autoHideTimer.Start(); }

    public void ShowTemporarily()
    {
        if (!IsVisible) Show();
        Activate();
        StartAutoHide();
    }

    void PauseResume_Click(object sender, RoutedEventArgs e) => PauseResumeRequested?.Invoke(this, EventArgs.Empty);
    void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    public void CloseAfterRecordingStops()
    {
        _allowClose = true;
        Close();
    }

    void ControlWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
