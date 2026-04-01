using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

namespace ClassroomController.Server.Middleware;

public class StreamRelayMiddleware
{
    private readonly RequestDelegate _next;

    // Map teacher connections (keyed by teacher identifier or connection id)
    private static readonly ConcurrentDictionary<string, WebSocket> _teacherSockets = new();

    public StreamRelayMiddleware(RequestDelegate next)
    {
        _next = next;
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

        if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(ident))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Missing role or mac query parameters");
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[1024 * 64];

        if (role == "teacher")
        {
            var teacherKey = ident;
            _teacherSockets[teacherKey] = socket;

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Teacher disconnected", CancellationToken.None);
                        break;
                    }
                }
            }
            finally
            {
                _teacherSockets.TryRemove(teacherKey, out _);
            }

            return;
        }

        if (role == "student")
        {
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Student disconnected", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.Count > 12)
                    {
                        var payload = new ArraySegment<byte>(buffer, 0, result.Count);
                        // Forward the exact payload to all teacher sockets
                        foreach (var teacherEntry in _teacherSockets)
                        {
                            var teacherSocket = teacherEntry.Value;
                            if (teacherSocket.State == WebSocketState.Open)
                            {
                                await teacherSocket.SendAsync(payload, WebSocketMessageType.Binary, result.EndOfMessage, CancellationToken.None);
                            }
                        }
                    }
                }
            }
            finally
            {
                // no persistent student mapping required for dumb relay
            }

            return;
        }

        // invalid role
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid role", CancellationToken.None);
    }
}
