using System.Net.NetworkInformation;

namespace ClassroomController.Client.Network;

public static class NetworkUtils
{
    public static string GetActiveMacAddress()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                         (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                          ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .OrderBy(ni => ni.NetworkInterfaceType) // Prefer Ethernet over WiFi
            .ToList();

        if (!interfaces.Any())
        {
            throw new InvalidOperationException("No active network interface found.");
        }

        var mac = interfaces.First().GetPhysicalAddress().ToString().ToUpper();
        // Format with dashes: e.g., 24FBE342F1ED -> 24-FB-E3-42-F1-ED
        return string.Join("-", Enumerable.Range(0, mac.Length / 2)
            .Select(i => mac.Substring(i * 2, 2)));
    }
}
