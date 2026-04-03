using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using ClassroomController.Client.Utils;

namespace ClassroomController.Client.UI;

public partial class LockWindow : Window
{
    private bool _allowClosing = false;
    private bool _inputBlocked = false;

    // P/Invoke for Windows API to block input
    [DllImport("user32.dll")]
    private static extern bool BlockInput(bool Block);

    public LockWindow()
    {
        InitializeComponent();
        Logger.Log("LockWindow created");
        
        // Block all keyboard and mouse input
        BlockInput(true);
        _inputBlocked = true;
        Logger.Log("LockWindow: Input blocked");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        
        if (!_allowClosing)
        {
            e.Cancel = true;
            Logger.Log("LockWindow close attempt blocked");
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Suppress all keyboard input
        e.Handled = true;
        Logger.Log($"LockWindow: Keyboard input suppressed - {e.Key}");
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Suppress all mouse click input
        e.Handled = true;
        Logger.Log($"LockWindow: Mouse click suppressed");
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Prevent mouse from leaving window area
        e.Handled = false;
    }

    /// <summary>
    /// Force close the window (called by SystemController.UnlockScreen).
    /// </summary>
    public void ForceClose()
    {
        Logger.Log("LockWindow forced close");
        
        // Unblock input before closing
        if (_inputBlocked)
        {
            BlockInput(false);
            _inputBlocked = false;
            Logger.Log("LockWindow: Input unblocked");
        }

        // Avoid shutting the whole app (OnExplicitShutdown is set), but keep lock window logic safe.
        _allowClosing = true;

        // Hide first then close to avoid shutdown race conditions in WPF.
        this.Hide();
        this.Close();

        Logger.Log("LockWindow: Hidden and closed");
    }
}
