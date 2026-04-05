using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ClassroomController.Client.Network;
using ClassroomController.Client.Utils;

namespace ClassroomController.Client.Media;

public class ScreenCapturer
{
    public static volatile int TargetWidth = 640;
    public static volatile int TargetHeight = 360;
    public static volatile int TargetDelayMs = 250;
    public static volatile int TargetJpegQuality = 55;

    private static readonly ImageCodecInfo? JpegCodec =
        Array.Find(ImageCodecInfo.GetImageEncoders(), codec => codec.FormatID == ImageFormat.Jpeg.Guid);

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
            }
            catch (Exception ex)
            {
                Logger.Log($"Capture error: {ex.Message}");
            }
            
            await Task.Delay(TargetDelayMs, cancellationToken);
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

        var targetWidth = Math.Max(1, TargetWidth);
        var targetHeight = Math.Max(1, TargetHeight);

        using var ms = new MemoryStream();
        if (targetWidth == screenBounds.Width && targetHeight == screenBounds.Height)
        {
            SaveJpeg(bitmap, ms);
        }
        else
        {
            using var resized = new Bitmap(targetWidth, targetHeight);
            using var resizeGraphics = Graphics.FromImage(resized);
            resizeGraphics.CompositingQuality = CompositingQuality.HighQuality;
            resizeGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            resizeGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            resizeGraphics.SmoothingMode = SmoothingMode.HighQuality;
            resizeGraphics.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
            SaveJpeg(resized, ms);
        }

        return ms.ToArray();
    }

    private static void SaveJpeg(Image image, Stream output)
    {
        if (JpegCodec != null)
        {
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(TargetJpegQuality, 0, 100));
            image.Save(output, JpegCodec, encoderParameters);
            return;
        }

        image.Save(output, ImageFormat.Jpeg);
    }
}
