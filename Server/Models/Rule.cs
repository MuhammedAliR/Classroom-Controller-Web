namespace ClassroomController.Server.Models;

public class Rule
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
