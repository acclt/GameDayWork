using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GameOrchestrator.Models;
using Forms = System.Windows.Forms;

namespace GameOrchestrator.Services;

public sealed class DesktopScreenshotService : IScreenshotService
{
    private const int MaximumImageBytes = 1_900_000;
    private const uint PwRenderFullContent = 0x00000002;

    public Task<ScreenshotPayload?> CaptureForTaskAsync(RuntimeSession session, CancellationToken token = default) =>
        CaptureDesktopAsync(token);

    public Task<ScreenshotPayload?> CaptureWindowAsync(nint windowHandle, CancellationToken token = default) =>
        CaptureDesktopAsync(token);

    public Task<ScreenshotPayload?> CaptureDesktopAsync(CancellationToken token = default) =>
        Task.Run<ScreenshotPayload?>(() => CaptureDesktop(token), token);

    private static ScreenshotPayload CaptureDesktop(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bounds = Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("未找到可截图的显示区域");

        using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(desktop))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            CompositeTaskbars(graphics, bounds, token);
        }

        return EncodeWithinLimit(desktop, token);
    }

    private static void CompositeTaskbars(Graphics destination, Rectangle desktopBounds, CancellationToken token)
    {
        var taskbars = EnumerateTaskbars();
        if (taskbars.Count == 0) throw new InvalidOperationException("未找到 Windows 任务栏窗口");
        var captured = 0;
        foreach (var taskbar in taskbars)
        {
            token.ThrowIfCancellationRequested();
            if (!GetWindowRect(taskbar, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 1 || height <= 1) continue;

            using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(taskbar, hdc, PwRenderFullContent)) continue;
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            destination.DrawImageUnscaled(image, rect.Left - desktopBounds.Left, rect.Top - desktopBounds.Top);
            captured++;
        }
        if (captured == 0) throw new InvalidOperationException("无法捕获 Windows 任务栏窗口");
    }

    private static IReadOnlyList<nint> EnumerateTaskbars()
    {
        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            var className = new char[64];
            var length = GetClassName(window, className, className.Length);
            if (length <= 0) return true;
            var value = new string(className, 0, length);
            if (value is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") handles.Add(window);
            return true;
        }, nint.Zero);
        return handles;
    }

    private static ScreenshotPayload EncodeWithinLimit(Bitmap original, CancellationToken token)
    {
        var png = EncodePng(original);
        if (png.Length <= MaximumImageBytes)
            return new(png, "image/png", "PNG", original.Width, original.Height);

        using var working = new Bitmap(original);
        Bitmap current = working;
        var ownsCurrent = false;
        try
        {
            while (current.Width >= 640 && current.Height >= 360)
            {
                foreach (var quality in new long[] { 88, 80, 72, 64, 56, 48 })
                {
                    token.ThrowIfCancellationRequested();
                    var jpeg = EncodeJpeg(current, quality);
                    if (jpeg.Length <= MaximumImageBytes)
                        return new(jpeg, "image/jpeg", "JPEG", current.Width, current.Height);
                }

                var nextWidth = Math.Max(1, (int)Math.Floor(current.Width * 0.85));
                var nextHeight = Math.Max(1, (int)Math.Floor(current.Height * 0.85));
                var resized = new Bitmap(nextWidth, nextHeight, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(current, 0, 0, nextWidth, nextHeight);
                }
                if (ownsCurrent) current.Dispose();
                current = resized;
                ownsCurrent = true;
            }
        }
        finally
        {
            if (ownsCurrent) current.Dispose();
        }

        throw new InvalidOperationException("截图压缩后仍超过企业微信 2 MB 限制");
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint targetDc, uint flags);
}
