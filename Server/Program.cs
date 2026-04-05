using System.Net.WebSockets;
using System.Text.Json;
using ClassroomController.Server.Data;
using ClassroomController.Server.Hubs;
using ClassroomController.Server.Middleware;
using ClassroomController.Server.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware", LogLevel.Warning);

// Kestrel configuration for standard ports
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000); // HTTP
    options.ListenAnyIP(5001, listenOptions => listenOptions.UseHttps()); // HTTPS
});

// Ensure app has wwwroot static file support
builder.Services.AddRouting();
builder.Services.AddDirectoryBrowser();

// Entity Framework Core - SQLite
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "app_data", "classroom.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
var connectionString = $"Data Source={dbPath}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// SignalR registration
builder.Services.AddSignalR();

var app = builder.Build();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

// Ensure DB is created and apply pending migrations if any
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Read config.json and sync devices
    var configFile = Path.Combine(app.Environment.ContentRootPath, "config.json");
    if (File.Exists(configFile))
    {
        try
        {
            var json = File.ReadAllText(configFile);
            var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config?.Devices is not null)
            {
                var configMacs = new HashSet<string>(config.Devices.Select(d => d.Mac).Where(m => !string.IsNullOrWhiteSpace(m)));

                // Remove devices not in config
                var devicesToRemove = db.Devices.Where(d => !configMacs.Contains(d.MacAddress)).ToList();
                db.Devices.RemoveRange(devicesToRemove);

                // Upsert devices from config
                foreach (var c in config.Devices)
                {
                    if (string.IsNullOrWhiteSpace(c.Mac) || string.IsNullOrWhiteSpace(c.Ip) || string.IsNullOrWhiteSpace(c.Hostname))
                        continue;

                    var existing = db.Devices.Find(c.Mac);
                    if (existing == null)
                    {
                        db.Devices.Add(new ClassroomController.Server.Models.Device
                        {
                            MacAddress = c.Mac,
                            IpAddress = c.Ip,
                            Hostname = c.Hostname,
                            Status = "Offline",
                            BlockedWebsites = string.Empty
                        });
                    }
                    else
                    {
                        existing.IpAddress = c.Ip;
                        existing.Hostname = c.Hostname;
                    }
                }
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Failed to parse config.json for device sync.");
        }
    }
    else
    {
        app.Logger.LogWarning("config.json not found at {Path}", configFile);
    }

    // Mark all devices as offline at server startup (recovery after server restart)
    var allDevices = db.Devices.ToList();
    foreach (var device in allDevices)
    {
        device.Status = "Offline";
        if (device.TimerEndTime.HasValue && device.TimerEndTime.Value <= DateTime.UtcNow)
        {
            device.IsLocked = true;
            device.TimerEndTime = null;
        }
    }
    db.SaveChanges();
    app.Logger.LogInformation("Server startup: all devices set to Offline for clean state.");
}

// SignalR and hub route
app.MapHub<CommandHub>("/commandhub");

app.UseMiddleware<StreamRelayMiddleware>();

app.MapGet("/api/devices", async (AppDbContext db) =>
{
    var devices = await db.Devices.ToListAsync();
    var hasChanges = false;

    foreach (var device in devices)
    {
        if (device.TimerEndTime.HasValue && device.TimerEndTime.Value <= DateTime.UtcNow)
        {
            device.IsLocked = true;
            device.TimerEndTime = null;
            hasChanges = true;
        }
    }

    if (hasChanges)
    {
        await db.SaveChangesAsync();
    }

    return devices;
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Helper type for config file read
internal sealed class Config
{
    public string MasterKey { get; set; } = string.Empty;
    public List<DeviceConfig> Devices { get; set; } = new();
}

internal sealed class DeviceConfig
{
    public string Mac { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
}
