using System.IO;
using System.Text.Json;

namespace ClassroomController.Client;

public class ConfigService
{
    public string ServerUrl { get; private set; } = "http://localhost:5000";

    public ConfigService()
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config?.ServerUrl != null)
                {
                    ServerUrl = config.ServerUrl;
                }
            }
            catch (Exception ex)
            {
                // Fallback to default
                Console.WriteLine($"Failed to load config: {ex.Message}");
            }
        }
    }

    private class Config
    {
        public string? ServerUrl { get; set; }
    }
}
