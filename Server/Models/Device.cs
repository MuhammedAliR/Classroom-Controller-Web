using System.ComponentModel.DataAnnotations.Schema;

namespace ClassroomController.Server.Models;

public class Device
{
    // Primary key: device MAC address
    public string MacAddress { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Status { get; set; } = "Offline";
    public bool IsLocked { get; set; }
    public bool IsFrozen { get; set; }
    public bool IsAdminMode { get; set; }
    public DateTime? TimerEndTime { get; set; }
    public string BlockedWebsites { get; set; } = string.Empty;

    [NotMapped]
    public int? TimerRemainingSeconds
    {
        get
        {
            if (!TimerEndTime.HasValue)
            {
                return null;
            }

            var remainingSeconds = (int)Math.Ceiling((TimerEndTime.Value - DateTime.UtcNow).TotalSeconds);
            return remainingSeconds > 0 ? remainingSeconds : null;
        }
    }

    // Optional timer window for remote session or lock behavior
    public DateTime? TimerStart { get; set; }
    public DateTime? TimerEnd { get; set; }
}
