using SkiaSharp;

namespace PdfStudio.Core.Formats;

public static class ImageDocumentService
{
    public static Task<SKBitmap> LoadAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(filePath);
            var decoded = SKBitmap.Decode(bytes)
                          ?? throw new InvalidDataException($"Could not decode image: {Path.GetFileName(filePath)}");
            return decoded;
        }, ct);
    }

    public static SKBitmap CloneBitmap(SKBitmap source)
    {
        var copy = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        source.CopyTo(copy);
        return copy;
    }

    public static SKBitmap Rotate90Cw(SKBitmap source)
    {
        var rotated = new SKBitmap(source.Height, source.Width, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(source.Height, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    public static SKBitmap Rotate90Ccw(SKBitmap source)
    {
        var rotated = new SKBitmap(source.Height, source.Width, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(0, source.Width);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    public static SKBitmap FlipHorizontal(SKBitmap source)
    {
        var flipped = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(flipped);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(-1, 1, source.Width * 0.5f, 0);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    public static SKBitmap FlipVertical(SKBitmap source)
    {
        var flipped = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(flipped);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(1, -1, 0, source.Height * 0.5f);
        canvas.DrawBitmap(source, 0, 0);
        return flipped;
    }

    public static SKBitmap Crop(SKBitmap source, SKRectI cropRect)
    {
        int left = Math.Clamp(cropRect.Left, 0, Math.Max(0, source.Width - 1));
        int top = Math.Clamp(cropRect.Top, 0, Math.Max(0, source.Height - 1));
        int right = Math.Clamp(cropRect.Right, left + 1, source.Width);
        int bottom = Math.Clamp(cropRect.Bottom, top + 1, source.Height);

        int w = Math.Max(1, right - left);
        int h = Math.Max(1, bottom - top);

        var cropped = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(cropped);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(left, top, right, bottom), new SKRect(0, 0, w, h));
        return cropped;
    }

    public static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        int w = Math.Clamp(width, 1, 16384);
        int h = Math.Clamp(height, 1, 16384);
        var info = new SKImageInfo(w, h, source.ColorType, source.AlphaType);
        var sampling = new SKSamplingOptions(SKCubicResampler.CatmullRom);
        var resized = source.Resize(info, sampling) ?? throw new InvalidOperationException("Image resize failed.");
        return resized;
    }

    public static byte[] EncodeBytes(SKBitmap bitmap, string extension)
    {
        string ext = extension.Trim().ToLowerInvariant();
        if (ext == ".bmp")
        {
            return EncodeBmp(bitmap);
        }

        var fmt = ext switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        int quality = fmt == SKEncodedImageFormat.Png ? 100 : 95;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(fmt, quality);
        if (data is null)
        {
            throw new InvalidOperationException($"Failed to encode image as {fmt}.");
        }
        return data.ToArray();
    }

    public static Task SaveAtomicAsync(string targetFilePath, SKBitmap bitmap, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(targetFilePath) ?? ".";
            Directory.CreateDirectory(dir);
            string tempPath = Path.Combine(dir, $".{Path.GetFileName(targetFilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                byte[] encoded = EncodeBytes(bitmap, Path.GetExtension(targetFilePath));
                File.WriteAllBytes(tempPath, encoded);

                var info = new FileInfo(tempPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new IOException("Atomic image save verification failed.");
                }

                File.Move(tempPath, targetFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                }
            }
        }, ct);
    }

    private static byte[] EncodeBmp(SKBitmap source)
    {
        // Standard 32-bit BGRA Windows BMP (top-down via negative height)
        using var bgra = source.Copy(SKColorType.Bgra8888);
        int width = bgra.Width;
        int height = bgra.Height;
        int stride = width * 4;
        int imageSize = stride * height;
        int fileSize = 54 + imageSize;

        using var ms = new MemoryStream(fileSize);
        using var bw = new BinaryWriter(ms);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write(54);

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(width);
        bw.Write(-height); // top-down
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0); // BI_RGB
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        byte[] rowBuffer = new byte[stride];
        nint basePtr = bgra.GetPixels();
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(basePtr + y * bgra.RowBytes, rowBuffer, 0, stride);
            bw.Write(rowBuffer);
        }

        return ms.ToArray();
    }
}
