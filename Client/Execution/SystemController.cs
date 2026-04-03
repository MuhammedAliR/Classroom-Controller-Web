using System.Diagnostics;
using System.Windows;
using ClassroomController.Client.Native;
using ClassroomController.Client.Utils;

namespace ClassroomController.Client.Execution;

public static class SystemController
{
    private static UI.LockWindow? _lockWindow;

    /// <summary>
    /// Locks the screen by displaying a full-screen lock window.
    /// </summary>
    public static void LockScreen()
    {
        Logger.Log("SystemController: Locking screen");
        
        if (_lockWindow == null || !_lockWindow.IsVisible)
        {
            _lockWindow = new UI.LockWindow();
            _lockWindow.Show();
        }
    }

    /// <summary>
    /// Unlocks the screen by closing the lock window.
    /// </summary>
    public static void UnlockScreen()
    {
        Logger.Log("SystemController: Unlocking screen");
        
        if (_lockWindow != null && _lockWindow.IsVisible)
        {
            _lockWindow.ForceClose();
        }
    }

    /// <summary>
    /// Reboots the system via shutdown command.
    /// </summary>
    public static void Reboot()
    {
        Logger.Log("SystemController: Initiating reboot");
        
        try
        {
            var processInfo = new ProcessStartInfo("shutdown", "/r /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Reboot failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Powers off the system via shutdown command.
    /// </summary>
    public static void PowerOff()
    {
        Logger.Log("SystemController: Initiating power off");
        
        try
        {
            var processInfo = new ProcessStartInfo("shutdown", "/s /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Power off failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a message dialog to the user.
    /// </summary>
    public static void ShowMessage(string message)
    {
        Logger.Log($"SystemController: Showing message - {message}");
        
        try
        {
            System.Windows.MessageBox.Show(message, "Teacher Message", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: ShowMessage failed - {ex.Message}");
        }
    }

    public static void HandleMouseInput(string eventType, double xPct, double yPct)
    {
        try
        {
            var clampedXPct = Math.Clamp(xPct, 0d, 1d);
            var clampedYPct = Math.Clamp(yPct, 0d, 1d);
            int targetX = (int)(SystemParameters.PrimaryScreenWidth * clampedXPct);
            int targetY = (int)(SystemParameters.PrimaryScreenHeight * clampedYPct);

            WindowsAPI.SetCursorPos(targetX, targetY);

            if (eventType == "mousedown")
            {
                WindowsAPI.mouse_event(WindowsAPI.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            }
            else if (eventType == "mouseup")
            {
                WindowsAPI.mouse_event(WindowsAPI.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleMouseInput failed - {ex.Message}");
        }
    }
}
