using System.Windows;
using System.Windows.Threading;
using ClassroomController.Client.Execution;

namespace ClassroomController.Client.UI;

public partial class TimerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private TimeSpan _remaining;
    private bool _isClosingForCompletion;

    public TimerWindow(TimeSpan remaining)
    {
        InitializeComponent();

        _remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left, workArea.Right - Width - 20);
            Top = workArea.Top + 20;
        };

        UpdateText();
        _timer.Start();
    }

    public void ForceClose()
    {
        _timer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _remaining = _remaining.Subtract(TimeSpan.FromSeconds(1));
        UpdateText();

        if (_remaining <= TimeSpan.Zero && !_isClosingForCompletion)
        {
            _timer.Stop();
            _isClosingForCompletion = true;
            Close();
            SystemController.LockScreen();
        }
    }

    private void UpdateText()
    {
        TimeText.Text = $"{Math.Max(0, (int)_remaining.TotalMinutes):00}:{Math.Max(0, _remaining.Seconds):00}";
    }
}
