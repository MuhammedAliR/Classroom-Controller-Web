using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using ClassroomController.Server.Data;
using ClassroomController.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClassroomController.Server.Middleware;

public class StreamRelayMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StreamRelayMiddleware> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<CommandHub> _hubContext;

    // Map teacher connections by a unique socket id so refreshes do not clobber active sockets.
    private static readonly ConcurrentDictionary<string, WebSocket> _teacherSockets = new();
    private static readonly ConcurrentDictionary<string, int> _studentStreamCounts = new();

    public StreamRelayMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<StreamRelayMiddleware> logger,
        IServiceScopeFactory scopeFactory,
        IHubContext<CommandHub> hubContext)
    {
        _next = next;
        _configuration = configuration;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
    }

    public static bool IsStudentStreamConnected(string macAddress)
    {
        var normalizedMac = NormalizeMac(macAddress);
        return !string.IsNullOrWhiteSpace(normalizedMac)
            && _studentStreamCounts.TryGetValue(normalizedMac, out var count)
            && count > 0;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path != "/ws/stream" || !context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        var query = context.Request.Query;
        var role = query["role"].ToString().ToLowerInvariant();
        var ident = query["mac"].ToString();
        var providedKey = query["key"].ToString();
        var expectedKey = _configuration["MasterKey"];

        if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(ident))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing role or mac query parameters");
            return;
        }

        if (string.IsNullOrWhiteSpace(expectedKey) || !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            _logger.LogWarning("[Stream] Rejected unauthorized {Role} socket for {Identifier}", role, ident);
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[1024 * 64];

        if (role == "teacher")
        {
            var teacherSocketId = Guid.NewGuid().ToString("N");
            _teacherSockets[teacherSocketId] = socket;
            _logger.LogInformation("[Stream] Teacher {TeacherId} connected as socket {SocketId}. Teachers: {TeacherCount}", ident, teacherSocketId, _teacherSockets.Count);

            try
            {
                // Teacher connections are receive-only; wait for natural close
                while (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Teacher disconnected", CancellationToken.None);
                            break;
                        }
                        // Ignore any unexpected messages from teacher
                    }
                    catch (WebSocketException ex)
                    {
                        // Connection closed abruptly
                        _logger.LogWarning(ex, "[Stream] Teacher {TeacherId} socket {SocketId} connection error", ident, teacherSocketId);
                        break;
                    }
                }
            }
            finally
            {
                _teacherSockets.TryRemove(teacherSocketId, out _);
                _logger.LogInformation("[Stream] Teacher {TeacherId} socket {SocketId} disconnected. Teachers: {TeacherCount}", ident, teacherSocketId, _teacherSockets.Count);
            }

            return;
        }

        if (role == "student")
        {
            var normalizedStudentMac = NormalizeMac(ident);
            _studentStreamCounts.AddOrUpdate(normalizedStudentMac, 1, (_, count) => count + 1);
            await UpdateStudentStatusAsync(normalizedStudentMac, "Online");
            _logger.LogInformation("[Stream] Student {StudentId} connected", ident);
            
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var memoryStream = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Student disconnected", CancellationToken.None);
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            memoryStream.Write(buffer, 0, result.Count);
                        }

                    } while (!result.EndOfMessage && socket.State == WebSocketState.Open);

                    if (memoryStream.Length <= 12)
                    {
                        continue;
                    }

                    var payloadBytes = memoryStream.ToArray();
                    _logger.LogDebug("[Stream] Frame received from {StudentId}. Bytes: {PayloadLength}. Teachers: {TeacherCount}", ident, payloadBytes.Length, _teacherSockets.Count);

                    var payload = new ArraySegment<byte>(payloadBytes);

                    if (_teacherSockets.Count == 0)
                    {
                        _logger.LogDebug("[Stream] Dropped frame from {StudentId} because no teachers are connected", ident);
                        continue;
                    }

                    foreach (var teacherEntry in _teacherSockets)
                    {
                        var teacherSocket = teacherEntry.Value;
                        
                        if (teacherSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                await teacherSocket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
                                _logger.LogDebug("[Stream] Frame from {StudentId} sent to teacher socket {SocketId}", ident, teacherEntry.Key);
                            }
                            catch (WebSocketException wsEx)
                            {
                                // ignore broken teacher connection
                                _logger.LogWarning(wsEx, "[Stream] Failed to send frame from {StudentId} to teacher socket {SocketId}", ident, teacherEntry.Key);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("[Stream] Skipped teacher socket {SocketId} because state is {SocketState}", teacherEntry.Key, teacherSocket.State);
                        }
                    }
                }
            }
            finally
            {
                _studentStreamCounts.AddOrUpdate(normalizedStudentMac, 0, (_, count) => Math.Max(0, count - 1));
                if (_studentStreamCounts.TryGetValue(normalizedStudentMac, out var remainingCount) && remainingCount <= 0)
                {
                    _studentStreamCounts.TryRemove(normalizedStudentMac, out _);
                }

                await UpdateStudentStatusAsync(normalizedStudentMac, "Offline");
                _logger.LogInformation("[Stream] Student {StudentId} disconnected", ident);
                // no persistent student mapping required for dumb relay
            }

            return;
        }

        // invalid role
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid role", CancellationToken.None);
    }

    private async Task UpdateStudentStatusAsync(string normalizedMac, string requestedStatus)
    {
        if (string.IsNullOrWhiteSpace(normalizedMac))
        {
            return;
        }

        var effectiveStatus = requestedStatus;
        if (requestedStatus.Equals("Offline", StringComparison.OrdinalIgnoreCase)
            && CommandHub.IsDeviceConnected(normalizedMac))
        {
            effectiveStatus = "Online";
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.MacAddress.ToUpper() == normalizedMac);
        if (device == null)
        {
            _logger.LogWarning("[Stream] Unable to update status for unknown student {StudentId}", normalizedMac);
            return;
        }

        if (string.Equals(device.Status, effectiveStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        device.Status = effectiveStatus;
        await dbContext.SaveChangesAsync();
        await _hubContext.Clients.All.SendAsync("DeviceStatusChanged", normalizedMac, effectiveStatus);
        _logger.LogInformation("[Stream] Student {StudentId} status set to {Status} from stream activity", normalizedMac, effectiveStatus);
    }

    private static string NormalizeMac(string? macAddress) => macAddress?.Trim().ToUpperInvariant() ?? string.Empty;
}
