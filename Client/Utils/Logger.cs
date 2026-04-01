using System.IO;

namespace ClassroomController.Client.Utils;

public static class Logger
{
    private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client_log.txt");

    public static void Log(string message)
    {
        try
        {
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, logEntry);
        }
        catch
        {
            // Ignore logging errors to avoid infinite loops
        }
    }
}
