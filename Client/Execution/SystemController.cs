using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using ClassroomController.Client.Native;
using ClassroomController.Client.Utils;
using ClassroomController.Client.Media;

namespace ClassroomController.Client.Execution;

public static class SystemController
{
    private const string ExplorerPolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string WindowsSystemPolicyKeyPath = @"Software\Policies\Microsoft\Windows\System";
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private static readonly WindowsAPI.HookProc KeyboardHookCallback = KeyboardHookProc;
    private static UI.LockWindow? _lockWindow;
    private static IntPtr _keyboardHook = IntPtr.Zero;
    private static volatile bool _isControlPressed;

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

        SetRegistryPolicy("DisableTaskMgr", 1);
        SetRegistryPolicy("DisableChangePassword", 1);
        SetRegistryPolicy("DisableLockWorkstation", 1);
        SetRegistryPolicy("HideFastUserSwitching", 1);
        SetMachineRegistryPolicy("HideFastUserSwitching", 1);
        SetExplorerPolicy("NoLogoff", 1);
        SetExplorerPolicy("NoClose", 1);
        InstallKeyboardHook();
        SetHardwareInputBlocked(true);
    }

    /// <summary>
    /// Unlocks the screen by closing the lock window.
    /// </summary>
    public static void UnlockScreen()
    {
        Logger.Log("SystemController: Unlocking screen");

        RestoreLockPoliciesAndInput();

        if (_lockWindow != null && _lockWindow.IsVisible)
        {
            _lockWindow.ForceClose();
        }
    }

    public static void RestoreLockPoliciesAndInput()
    {
        SetHardwareInputBlocked(false);
        RemoveKeyboardHook();
        SetRegistryPolicy("DisableTaskMgr", 0);
        SetRegistryPolicy("DisableChangePassword", 0);
        SetRegistryPolicy("DisableLockWorkstation", 0);
        SetRegistryPolicy("HideFastUserSwitching", 0);
        SetMachineRegistryPolicy("HideFastUserSwitching", 0);
        SetExplorerPolicy("NoLogoff", 0);
        SetExplorerPolicy("NoClose", 0);
    }

    public static void EnforceStudentMode(bool enforce)
    {
        var value = enforce ? 1 : 0;

        SetRegistryPolicy("DisableTaskMgr", value);
        SetRegistryPolicy("DisableRegistryTools", value);
        SetExplorerPolicy("NoControlPanel", value);
        SetExplorerPolicy("NoClose", value);
        SetWindowsSystemPolicy("DisableCMD", value);

        Logger.Log($"SystemController: Student mode enforced={enforce}");
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

    public static void HandleMouseInput(string eventType, double xPct, double yPct, int button)
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
                if (button == 0)
                {
                    WindowsAPI.mouse_event(WindowsAPI.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                }
                else if (button == 2)
                {
                    WindowsAPI.mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                }
            }
            else if (eventType == "mouseup")
            {
                if (button == 0)
                {
                    WindowsAPI.mouse_event(WindowsAPI.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                }
                else if (button == 2)
                {
                    WindowsAPI.mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleMouseInput failed - {ex.Message}");
        }
    }

    public static void SetStreamModeHigh()
    {
        try
        {
            ScreenCapturer.TargetWidth = Math.Max(1, (int)SystemParameters.PrimaryScreenWidth);
            ScreenCapturer.TargetHeight = Math.Max(1, (int)SystemParameters.PrimaryScreenHeight);
            ScreenCapturer.TargetDelayMs = 16;
            ScreenCapturer.TargetJpegQuality = 98;
            Logger.Log($"SystemController: Stream mode set to HIGH ({ScreenCapturer.TargetWidth}x{ScreenCapturer.TargetHeight} @ {ScreenCapturer.TargetDelayMs}ms, JPEG {ScreenCapturer.TargetJpegQuality})");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: SetStreamModeHigh failed - {ex.Message}");
        }
    }

    public static void SetStreamModeLow()
    {
        try
        {
            ScreenCapturer.TargetWidth = 640;
            ScreenCapturer.TargetHeight = 360;
            ScreenCapturer.TargetDelayMs = 250;
            ScreenCapturer.TargetJpegQuality = 55;
            Logger.Log($"SystemController: Stream mode set to LOW (640x360 @ 250ms, JPEG {ScreenCapturer.TargetJpegQuality})");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: SetStreamModeLow failed - {ex.Message}");
        }
    }

    public static void HandleKeyboardInput(string type, int keyCode)
    {
        try
        {
            var vk = (byte)keyCode;
            var scanCode = (byte)WindowsAPI.MapVirtualKey(vk, 0);
            var flags = type == "keyup" ? WindowsAPI.KEYEVENTF_KEYUP : WindowsAPI.KEYEVENTF_KEYDOWN;

            if (IsExtendedKey(vk))
            {
                flags |= WindowsAPI.KEYEVENTF_EXTENDEDKEY;
            }

            if (type == "keydown" || type == "keyup")
            {
                WindowsAPI.keybd_event(vk, scanCode, flags, 0);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleKeyboardInput failed - {ex.Message}");
        }
    }

    public static void HandleWebsiteBlock(string domain, bool block)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            Logger.Log("SystemController: HandleWebsiteBlock skipped because domain was empty");
            return;
        }

        try
        {
            var normalizedDomain = domain.Trim().ToLowerInvariant();
            string hostsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"drivers\etc\hosts");

            var hostsDirectory = Path.GetDirectoryName(hostsPath);
            if (!string.IsNullOrWhiteSpace(hostsDirectory) && !Directory.Exists(hostsDirectory))
            {
                Directory.CreateDirectory(hostsDirectory);
            }

            if (!File.Exists(hostsPath))
            {
                File.WriteAllText(hostsPath, string.Empty);
            }

            var lines = File.ReadAllLines(hostsPath).ToList();

            if (block)
            {
                if (!lines.Any(line => line.Contains(normalizedDomain, StringComparison.OrdinalIgnoreCase)))
                {
                    File.AppendAllLines(hostsPath, new[]
                    {
                        $"0.0.0.0 {normalizedDomain}",
                        $"0.0.0.0 www.{normalizedDomain}"
                    });
                }
            }
            else
            {
                var filteredLines = lines
                    .Where(line => !line.Contains(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                File.WriteAllLines(hostsPath, filteredLines);
            }

            Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (block)
            {
                string[] browsers = { "chrome", "msedge", "firefox", "opera", "brave" };
                foreach (var browser in browsers)
                {
                    try
                    {
                        var processes = Process.GetProcessesByName(browser);
                        foreach (var process in processes)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                        // Ignore errors if the browser isn't running or access is denied.
                    }
                }
            }

            Logger.Log($"SystemController: Website block updated for {normalizedDomain}, block={block}");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleWebsiteBlock failed for {domain} - {ex.Message}");
        }
    }

    private static bool IsExtendedKey(byte vk)
    {
        return vk is 0x21 or 0x22 or 0x23 or 0x24
            or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E
            or 0x5B or 0x5C or 0x5D
            or 0x6F
            or 0x90
            or 0xA3 or 0xA5;
    }

    private static void SetHardwareInputBlocked(bool isBlocked)
    {
        if (WindowsAPI.BlockInput(isBlocked))
        {
            Logger.Log($"SystemController: Hardware input block set to {isBlocked}");
            return;
        }

        Logger.Log($"SystemController: BlockInput({isBlocked}) failed - {Marshal.GetLastWin32Error()}");
    }

    private static void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            using var currentModule = currentProcess.MainModule;
            var moduleHandle = currentModule != null
                ? WindowsAPI.GetModuleHandle(currentModule.ModuleName)
                : IntPtr.Zero;

            _keyboardHook = WindowsAPI.SetWindowsHookEx(WindowsAPI.WH_KEYBOARD_LL, KeyboardHookCallback, moduleHandle, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                Logger.Log($"SystemController: Failed to install keyboard hook - {Marshal.GetLastWin32Error()}");
                return;
            }

            Logger.Log("SystemController: Keyboard hook installed");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: InstallKeyboardHook failed - {ex.Message}");
        }
    }

    private static void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }

        if (WindowsAPI.UnhookWindowsHookEx(_keyboardHook))
        {
            Logger.Log("SystemController: Keyboard hook removed");
        }
        else
        {
            Logger.Log($"SystemController: Failed to remove keyboard hook - {Marshal.GetLastWin32Error()}");
        }

        _keyboardHook = IntPtr.Zero;
        _isControlPressed = false;
    }

    private static void SetRegistryPolicy(string keyName, int value)
    {
        try
        {
            SetCurrentUserPolicyValue(PolicyKeyPath, keyName, value, "policy");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Failed to set {keyName}={value} - {ex.Message}");
        }
    }

    private static void SetExplorerPolicy(string keyName, int value)
    {
        try
        {
            SetCurrentUserPolicyValue(ExplorerPolicyKeyPath, keyName, value, "explorer policy");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Failed to set {keyName}={value} - {ex.Message}");
        }
    }

    private static void SetWindowsSystemPolicy(string keyName, int value)
    {
        try
        {
            SetCurrentUserPolicyValue(WindowsSystemPolicyKeyPath, keyName, value, "Windows system policy");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Failed to set {keyName}={value} - {ex.Message}");
        }
    }

    private static void SetMachineRegistryPolicy(string keyName, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PolicyKeyPath);
            if (key == null)
            {
                Logger.Log($"SystemController: Failed to open machine policy key for {keyName}");
                return;
            }

            key.SetValue(keyName, value, RegistryValueKind.DWord);
            Logger.Log($"SystemController: Set machine {keyName}={value}");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Failed to set machine {keyName}={value} - {ex.Message}");
        }
    }

    private static void SetCurrentUserPolicyValue(string keyPath, string keyName, int value, string policyKind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key == null)
        {
            Logger.Log($"SystemController: Failed to open {policyKind} key for {keyName}");
            return;
        }

        key.SetValue(keyName, value, RegistryValueKind.DWord);
        Logger.Log($"SystemController: Set {keyName}={value}");
    }

    private static IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == WindowsAPI.HC_ACTION)
        {
            var message = unchecked((int)wParam.ToInt64());
            if (message is WindowsAPI.WM_KEYDOWN or WindowsAPI.WM_KEYUP or WindowsAPI.WM_SYSKEYDOWN or WindowsAPI.WM_SYSKEYUP)
            {
                var hookData = Marshal.PtrToStructure<WindowsAPI.KBDLLHOOKSTRUCT>(lParam);
                var isKeyDown = message is WindowsAPI.WM_KEYDOWN or WindowsAPI.WM_SYSKEYDOWN;

                UpdateModifierState(hookData.vkCode, isKeyDown);

                if ((hookData.flags & WindowsAPI.LLKHF_INJECTED) == 0 && ShouldBlockKeystroke(hookData))
                {
                    return (IntPtr)1;
                }
            }
        }

        return WindowsAPI.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static void UpdateModifierState(uint virtualKey, bool isPressed)
    {
        if (virtualKey is WindowsAPI.VK_CONTROL or WindowsAPI.VK_LCONTROL or WindowsAPI.VK_RCONTROL)
        {
            _isControlPressed = isPressed;
        }
    }

    private static bool ShouldBlockKeystroke(WindowsAPI.KBDLLHOOKSTRUCT hookData)
    {
        var isAltDown = (hookData.flags & WindowsAPI.LLKHF_ALTDOWN) != 0;

        return hookData.vkCode is WindowsAPI.VK_LWIN or WindowsAPI.VK_RWIN
            || (hookData.vkCode == WindowsAPI.VK_TAB && isAltDown)
            || (hookData.vkCode == WindowsAPI.VK_ESCAPE && isAltDown)
            || (hookData.vkCode == WindowsAPI.VK_ESCAPE && _isControlPressed);
    }
}
