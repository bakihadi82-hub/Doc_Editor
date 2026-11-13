using System.Text;
using PdfSharpCore.Fonts;

namespace PdfStudio.Editing.PdfSharp;

public static class EncodingBootstrap
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            GlobalFontSettings.FontResolver ??= new WindowsSystemFontResolver();
        }
    }

    private sealed class WindowsSystemFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string normalized = (familyName ?? DefaultFontName).Trim().ToLowerInvariant();
            string prefix = normalized switch
            {
                "courier" or "courier new" or "jetbrains mono" or "consolas" => "cour",
                "times" or "times new roman" => "times",
                "segoe ui" or "inter" => "segoe",
                _ => "arial"
            };

            string suffix = (isBold, isItalic) switch
            {
                (true, true) => "bi",
                (true, false) => "bd",
                (false, true) => "i",
                _ => string.Empty
            };

            return new FontResolverInfo($"{prefix}_{suffix}");
        }

        public byte[] GetFont(string faceName)
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string fileName = faceName switch
            {
                "cour_" => "cour.ttf",
                "cour_bd" => "courbd.ttf",
                "cour_i" => "couri.ttf",
                "cour_bi" => "courbi.ttf",
                "times_" => "times.ttf",
                "times_bd" => "timesbd.ttf",
                "times_i" => "timesi.ttf",
                "times_bi" => "timesbi.ttf",
                "segoe_" => "segoeui.ttf",
                "segoe_bd" => "segoeuib.ttf",
                "segoe_i" => "segoeuii.ttf",
                "segoe_bi" => "segoeuiz.ttf",
                "arial_bd" => "arialbd.ttf",
                "arial_i" => "ariali.ttf",
                "arial_bi" => "arialbi.ttf",
                _ => "arial.ttf"
            };

            string candidate = Path.Combine(fontsDir, fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }

            string arialFallback = Path.Combine(fontsDir, "arial.ttf");
            if (File.Exists(arialFallback))
            {
                return File.ReadAllBytes(arialFallback);
            }

            string segoeFallback = Path.Combine(fontsDir, "segoeui.ttf");
            return File.ReadAllBytes(segoeFallback);
        }
    }
}
