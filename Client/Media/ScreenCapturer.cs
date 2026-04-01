using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ClassroomController.Client.Network;

namespace ClassroomController.Client.Media;

public class ScreenCapturer
{
    private readonly ConnectionManager _connectionManager;

    public ScreenCapturer(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task StartCaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var jpegBytes = CaptureScreen();
                await _connectionManager.SendVideoFrame(jpegBytes);
                await Task.Delay(250, cancellationToken); // ~4 FPS
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Screen capture error: {ex.Message}");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private byte[] CaptureScreen()
    {
        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen == null) return Array.Empty<byte>();

        var screenBounds = primaryScreen.Bounds;
        using var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(screenBounds.X, screenBounds.Y, 0, 0, screenBounds.Size);

        // Downscale to 640x360
        var resized = new Bitmap(640, 360);
        using var resizeGraphics = Graphics.FromImage(resized);
        resizeGraphics.DrawImage(bitmap, 0, 0, 640, 360);

        // Compress to JPEG
        using var ms = new MemoryStream();
        resized.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }
}
