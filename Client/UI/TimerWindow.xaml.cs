using System.Windows;
using System.Windows.Threading;

namespace ClassroomController.Client.UI;

public partial class TimerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;
    private bool _isClosingForCompletion;
    public event Action? TimerFinished;

    public TimerWindow(int totalSeconds)
    {
        InitializeComponent();

        _remainingSeconds = Math.Max(0, totalSeconds);
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
        if (_remainingSeconds > 0)
        {
            _remainingSeconds--;
        }

        UpdateText();

        if (_remainingSeconds <= 0 && !_isClosingForCompletion)
        {
            _timer.Stop();
            _isClosingForCompletion = true;
            TimerFinished?.Invoke();
            Close();
        }
    }

    private void UpdateText()
    {
        var minutes = Math.Max(0, _remainingSeconds / 60);
        var seconds = Math.Max(0, _remainingSeconds % 60);
        TimeText.Text = $"{minutes:00}:{seconds:00}";
    }
}
