namespace ClassroomController.Server.Models;

public class Device
{
    // Primary key: device MAC address
    public string MacAddress { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Status { get; set; } = "Offline";

    // Optional timer window for remote session or lock behavior
    public DateTime? TimerStart { get; set; }
    public DateTime? TimerEnd { get; set; }
}
