using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomController.Client.Network;
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
    private const string HostsSectionStart = "# ClassroomController Web Filter START";
    private const string HostsSectionEnd = "# ClassroomController Web Filter END";
    private static readonly string[] KnownFilterDomains =
    {
        "youtube.com",
        "instagram.com",
        "tiktok.com",
        "discord.com"
    };
    private static readonly WindowsAPI.HookProc KeyboardHookCallback = KeyboardHookProc;
    private static UI.LockWindow? _lockWindow;
    private static UI.SoftLockWindow? _softLockWindow;
    private static UI.TimerWindow? _timerWindow;
    private static IntPtr _keyboardHook = IntPtr.Zero;
    private static volatile bool _isControlPressed;
    private static volatile bool _isStudentModeEnforced;
    private static volatile bool _isLockPoliciesEnforced;

    /// <summary>
    /// Locks the screen by displaying a full-screen lock window.
    /// </summary>
    public static void LockScreen()
    {
        Logger.Log("SystemController: Locking screen");
        StopTimer();
        StopSoftLock();
        
        if (_lockWindow == null || !_lockWindow.IsVisible)
        {
            _lockWindow = new UI.LockWindow();
            _lockWindow.Show();
        }

        _isLockPoliciesEnforced = true;
        ApplyCurrentPolicyState();
        InstallKeyboardHook();
        SetHardwareInputBlocked(true);
    }

    /// <summary>
    /// Unlocks the screen by closing the lock window.
    /// </summary>
    public static void UnlockScreen()
    {
        Logger.Log("SystemController: Unlocking screen");

        SetHardwareInputBlocked(false);
        RemoveKeyboardHook();
        _isLockPoliciesEnforced = false;
        ApplyCurrentPolicyState();

        if (_lockWindow != null && _lockWindow.IsVisible)
        {
            _lockWindow.ForceClose();
        }
    }

    public static void RestoreLockPoliciesAndInput()
    {
        StopTimer();
        StopSoftLock();
        SetHardwareInputBlocked(false);
        RemoveKeyboardHook();
        _isLockPoliciesEnforced = false;
        _isStudentModeEnforced = false;
        ApplyCurrentPolicyState();
    }

    public static void EnforceStudentMode(bool enforce)
    {
        _isStudentModeEnforced = enforce;
        ApplyCurrentPolicyState();

        Logger.Log($"SystemController: Student mode enforced={enforce}");
    }

    public static void ApplySyncState(bool isLocked, bool isFrozen, bool isAdminMode, int? timerRemainingSeconds, string blockedWebsites)
    {
        Logger.Log(
            $"SystemController: Applying sync state lock={isLocked}, freeze={isFrozen}, admin={isAdminMode}, timerSeconds={timerRemainingSeconds?.ToString() ?? "null"}, websites={blockedWebsites}");

        EnforceStudentMode(!isAdminMode);
        ApplyBlockedWebsiteState(ParseBlockedWebsiteList(blockedWebsites), killBrowsersIfBlocked: true);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (isLocked)
            {
                LockScreen();
                return;
            }

            UnlockScreen();

            if (isFrozen)
            {
                StartSoftLock();
            }
            else
            {
                StopSoftLock();
            }

            if (timerRemainingSeconds.HasValue && timerRemainingSeconds.Value > 0)
            {
                StartTimer(timerRemainingSeconds.Value);
            }
            else
            {
                StopTimer();
            }
        });
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
            var absoluteX = (int)Math.Round(clampedXPct * 65535d);
            var absoluteY = (int)Math.Round(clampedYPct * 65535d);
            var inputs = new List<WindowsAPI.INPUT>
            {
                CreateMouseInput(absoluteX, absoluteY, WindowsAPI.MOUSEEVENTF_MOVE | WindowsAPI.MOUSEEVENTF_ABSOLUTE)
            };

            if (eventType == "mousedown")
            {
                if (button == 0)
                {
                    inputs.Add(CreateMouseInput(0, 0, WindowsAPI.MOUSEEVENTF_LEFTDOWN));
                }
                else if (button == 2)
                {
                    inputs.Add(CreateMouseInput(0, 0, WindowsAPI.MOUSEEVENTF_RIGHTDOWN));
                }
            }
            else if (eventType == "mouseup")
            {
                if (button == 0)
                {
                    inputs.Add(CreateMouseInput(0, 0, WindowsAPI.MOUSEEVENTF_LEFTUP));
                }
                else if (button == 2)
                {
                    inputs.Add(CreateMouseInput(0, 0, WindowsAPI.MOUSEEVENTF_RIGHTUP));
                }
            }

            SendInputs(inputs);
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
            var vk = (ushort)(byte)keyCode;
            var scanCode = (ushort)WindowsAPI.MapVirtualKey(vk, 0);
            var flags = type == "keyup" ? WindowsAPI.KEYEVENTF_KEYUP : WindowsAPI.KEYEVENTF_KEYDOWN;

            if (IsExtendedKey((byte)vk))
            {
                flags |= WindowsAPI.KEYEVENTF_EXTENDEDKEY;
            }

            if (type == "keydown" || type == "keyup")
            {
                SendInputs(new[]
                {
                    new WindowsAPI.INPUT
                    {
                        type = WindowsAPI.INPUT_KEYBOARD,
                        u = new WindowsAPI.InputUnion
                        {
                            ki = new WindowsAPI.KEYBDINPUT
                            {
                                wVk = vk,
                                wScan = scanCode,
                                dwFlags = flags,
                                time = 0,
                                dwExtraInfo = IntPtr.Zero
                            }
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleKeyboardInput failed - {ex.Message}");
        }
    }

    public static void StartSoftLock()
    {
        Logger.Log("SystemController: Starting soft lock");

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StopSoftLock();

            var screenshot = CapturePrimaryScreenImageSource();
            if (screenshot == null)
            {
                Logger.Log("SystemController: Soft lock screenshot capture failed");
                return;
            }

            _softLockWindow = new UI.SoftLockWindow();
            _softLockWindow.BackgroundImage.Source = screenshot;
            _softLockWindow.Show();
            SetHardwareInputBlocked(true);
        });
    }

    public static void StopSoftLock()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var softLockWindow = _softLockWindow;
            _softLockWindow = null;

            if (softLockWindow != null && softLockWindow.IsVisible)
            {
                softLockWindow.ForceClose();
            }

            SetHardwareInputBlocked(false);
        });
    }

    public static void StartTimer(int totalSeconds)
    {
        Logger.Log($"SystemController: Starting timer for {totalSeconds} seconds");

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StopTimer();

            if (totalSeconds <= 0)
            {
                LockScreen();
                return;
            }

            var timerWindow = new UI.TimerWindow(totalSeconds);
            timerWindow.TimerFinished += () =>
            {
                _ = ConnectionManager.NotifyTimerExpired();
            };

            _timerWindow = timerWindow;
            timerWindow.Show();
        });
    }

    public static void StartTimer(TimeSpan remaining)
    {
        StartTimer(Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)));
    }

    public static void StopTimer()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var timerWindow = _timerWindow;
            _timerWindow = null;

            if (timerWindow != null && timerWindow.IsVisible)
            {
                timerWindow.ForceClose();
            }
        });
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
            var blockedDomains = ReadBlockedDomainsFromHosts(KnownFilterDomains.Append(normalizedDomain));

            if (block)
            {
                blockedDomains.Add(normalizedDomain);
            }
            else
            {
                blockedDomains.Remove(normalizedDomain);
            }

            ApplyBlockedWebsiteState(blockedDomains, new[] { normalizedDomain }, killBrowsersIfBlocked: block);

            Logger.Log($"SystemController: Website block updated for {normalizedDomain}, block={block}");
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: HandleWebsiteBlock failed for {domain} - {ex.Message}");
        }
    }

    private static void ApplyBlockedWebsiteState(IEnumerable<string> blockedDomains, IEnumerable<string>? domainsToClean = null, bool killBrowsersIfBlocked = false)
    {
        var normalizedBlockedDomains = blockedDomains
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cleanupDomains = KnownFilterDomains
            .Concat(domainsToClean ?? Array.Empty<string>())
            .Concat(normalizedBlockedDomains)
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hostsPath = EnsureHostsFilePath();
        var existingLines = File.Exists(hostsPath) ? File.ReadAllLines(hostsPath) : Array.Empty<string>();
        var preservedLines = StripManagedHostsEntries(existingLines, cleanupDomains);
        var updatedLines = preservedLines.ToList();

        if (normalizedBlockedDomains.Length > 0)
        {
            if (updatedLines.Count > 0 && !string.IsNullOrWhiteSpace(updatedLines[^1]))
            {
                updatedLines.Add(string.Empty);
            }

            updatedLines.Add(HostsSectionStart);
            foreach (var domain in normalizedBlockedDomains)
            {
                updatedLines.Add($"0.0.0.0 {domain}");
                updatedLines.Add($"0.0.0.0 www.{domain}");
            }
            updatedLines.Add(HostsSectionEnd);
        }

        File.WriteAllLines(hostsPath, updatedLines);
        FlushDns();

        if (killBrowsersIfBlocked && normalizedBlockedDomains.Length > 0)
        {
            KillStandardBrowsers();
        }
    }

    private static HashSet<string> ReadBlockedDomainsFromHosts(IEnumerable<string> candidateDomains)
    {
        var hostsPath = EnsureHostsFilePath();
        var blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(hostsPath))
        {
            return blockedDomains;
        }

        var lines = File.ReadAllLines(hostsPath);
        foreach (var domain in candidateDomains
                     .Select(value => value.Trim().ToLowerInvariant())
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (lines.Any(line =>
                    line.Contains($" {domain}", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains($" www.{domain}", StringComparison.OrdinalIgnoreCase)))
            {
                blockedDomains.Add(domain);
            }
        }

        return blockedDomains;
    }

    private static string[] StripManagedHostsEntries(IEnumerable<string> lines, IEnumerable<string> cleanupDomains)
    {
        var domains = cleanupDomains.ToArray();
        var preservedLines = new List<string>();
        var isInsideManagedSection = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.Equals(trimmedLine, HostsSectionStart, StringComparison.Ordinal))
            {
                isInsideManagedSection = true;
                continue;
            }

            if (string.Equals(trimmedLine, HostsSectionEnd, StringComparison.Ordinal))
            {
                isInsideManagedSection = false;
                continue;
            }

            if (isInsideManagedSection)
            {
                continue;
            }

            if (domains.Any(domain => line.Contains(domain, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            preservedLines.Add(line);
        }

        return preservedLines.ToArray();
    }

    private static string EnsureHostsFilePath()
    {
        var hostsPath = Path.Combine(
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

        return hostsPath;
    }

    private static void FlushDns()
    {
        Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static void KillStandardBrowsers()
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

    private static string[] ParseBlockedWebsiteList(string blockedWebsites)
    {
        if (string.IsNullOrWhiteSpace(blockedWebsites))
        {
            return Array.Empty<string>();
        }

        return blockedWebsites
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static WindowsAPI.INPUT CreateMouseInput(int dx, int dy, uint flags)
    {
        return new WindowsAPI.INPUT
        {
            type = WindowsAPI.INPUT_MOUSE,
            u = new WindowsAPI.InputUnion
            {
                mi = new WindowsAPI.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static void SendInputs(IEnumerable<WindowsAPI.INPUT> inputs)
    {
        var inputArray = inputs.ToArray();
        if (inputArray.Length == 0)
        {
            return;
        }

        var sent = WindowsAPI.SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<WindowsAPI.INPUT>());
        if (sent != inputArray.Length)
        {
            Logger.Log($"SystemController: SendInput sent {sent}/{inputArray.Length} inputs - {Marshal.GetLastWin32Error()}");
        }
    }

    private static ImageSource? CapturePrimaryScreenImageSource()
    {
        var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen;
        if (primaryScreen == null)
        {
            return null;
        }

        using var bitmap = new Bitmap(primaryScreen.Bounds.Width, primaryScreen.Bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(primaryScreen.Bounds.X, primaryScreen.Bounds.Y, 0, 0, primaryScreen.Bounds.Size);

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();
            return bitmapSource;
        }
        finally
        {
            WindowsAPI.DeleteObject(hBitmap);
        }
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

    private static void ApplyCurrentPolicyState()
    {
        ApplyStudentModePolicies(_isStudentModeEnforced);
        ApplyLockPolicies(_isLockPoliciesEnforced);
        NotifyPolicyChanged();
    }

    private static void ApplyStudentModePolicies(bool enabled)
    {
        var value = enabled ? 1 : 0;
        SetRegistryPolicy("DisableTaskMgr", value);
        SetRegistryPolicy("DisableRegistryTools", value);
        SetExplorerPolicy("NoControlPanel", value);
        SetExplorerPolicy("NoClose", value);
        SetWindowsSystemPolicy("DisableCMD", value);
    }

    private static void ApplyLockPolicies(bool enabled)
    {
        var value = enabled ? 1 : 0;
        SetRegistryPolicy("DisableChangePassword", value);
        SetRegistryPolicy("DisableLockWorkstation", value);
        SetRegistryPolicy("HideFastUserSwitching", value);
        SetMachineRegistryPolicy("HideFastUserSwitching", value);
        SetExplorerPolicy("NoLogoff", value);

        if (enabled)
        {
            SetExplorerPolicy("NoClose", 1);
        }
        else if (!_isStudentModeEnforced)
        {
            SetExplorerPolicy("NoClose", 0);
        }
    }

    private static void NotifyPolicyChanged()
    {
        try
        {
            _ = WindowsAPI.SendMessageTimeout(
                WindowsAPI.HWND_BROADCAST,
                WindowsAPI.WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Policy",
                WindowsAPI.SMTO_ABORTIFHUNG,
                100,
                out _);
        }
        catch (Exception ex)
        {
            Logger.Log($"SystemController: Failed to broadcast policy refresh - {ex.Message}");
        }
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
