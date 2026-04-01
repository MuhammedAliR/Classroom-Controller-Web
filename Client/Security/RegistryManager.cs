using Microsoft.Win32;

namespace ClassroomController.Client.Security;

public class RegistryManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    public void SetAdminMode(bool isEnabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(ValueName, isEnabled ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set admin mode: {ex.Message}");
        }
    }
}
