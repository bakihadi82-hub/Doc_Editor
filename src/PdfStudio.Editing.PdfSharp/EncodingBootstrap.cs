using System.Collections.Concurrent;
using System.Text;
using PdfSharpCore.Fonts;
using SkiaSharp;

namespace PdfStudio.Editing.PdfSharp;

public static class EncodingBootstrap
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var current = GlobalFontSettings.FontResolver;
            if (current is null ||
                string.Equals(current.GetType().FullName, "PdfSharpCore.Utils.FontResolver", StringComparison.Ordinal))
            {
                try
                {
                    GlobalFontSettings.FontResolver = new CrossPlatformFontResolver();
                }
                catch (InvalidOperationException)
                {
                    // FontFactory already initialized and locked GlobalFontSettings.FontResolver.
                }
            }
        }
    }

    internal sealed class CrossPlatformFontResolver : IFontResolver
    {
        private const string FallbackFontName = "Arial";
        private readonly ConcurrentDictionary<string, byte[]> _fontCache = new(StringComparer.Ordinal);
        private static readonly Lazy<IReadOnlyDictionary<string, string>> IndexedFontFiles = new(BuildFontFileIndex);

        public string DefaultFontName => FallbackFontName;

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string stripped = StripSubsetPrefix(familyName);
            string normalized = stripped.ToLowerInvariant();
            string prefix = normalized switch
            {
                "courier" or "courier new" or "couriernew" or "jetbrains mono" or "consolas" or "liberation mono" or "dejavu sans mono" or "noto sans mono" or "fira code" => "cour",
                "times" or "times roman" or "times-roman" or "times new roman" or "timesnewroman" or "liberation serif" or "dejavu serif" or "noto serif" or "georgia" or "cambria" => "times",
                "segoe ui" or "segoeui" or "inter" or "calibri" => "segoe",
                "arial" or "arialmt" or "helvetica" or "helvetica neue" or "helveticaneue" or "nimbus sans" or "liberation sans" or "dejavu sans" or "noto sans" or "roboto" or "" => "arial",
                _ => $"custom:{stripped}"
            };

            string suffix = (isBold, isItalic) switch
            {
                (true, true) => "bi",
                (true, false) => "bd",
                (false, true) => "i",
                _ => string.Empty
            };

            return new FontResolverInfo($"{prefix}|{suffix}");
        }

        public byte[] GetFont(string faceName)
        {
            return _fontCache.GetOrAdd(faceName, LoadFontBytes);
        }

        private static byte[] LoadFontBytes(string faceName)
        {
            string prefix = faceName;
            string suffix = string.Empty;
            int sep = faceName.IndexOf('|');
            if (sep >= 0)
            {
                prefix = faceName[..sep];
                suffix = faceName[(sep + 1)..];
            }

            if (prefix.StartsWith("custom:", StringComparison.Ordinal))
            {
                string requestedFamily = prefix["custom:".Length..];
                if (TryLoadViaSkia(requestedFamily, suffix, out var customBytes))
                {
                    return customBytes;
                }
                prefix = "arial";
            }

            foreach (string candidateFile in GetCandidateFileNames(prefix, suffix))
            {
                if (TryReadIndexedFontFile(candidateFile, out var fileBytes))
                {
                    return fileBytes;
                }
            }

            foreach (string skiaFamily in GetCandidateSkiaFamilies(prefix))
            {
                if (TryLoadViaSkia(skiaFamily, suffix, out var skiaBytes))
                {
                    return skiaBytes;
                }
            }

            if (TryLoadFallbackViaSkia(suffix, out var fallbackSkiaBytes))
            {
                return fallbackSkiaBytes;
            }

            foreach (var kvp in IndexedFontFiles.Value)
            {
                string fileKey = kvp.Key;
                if (fileKey.Contains("marlett", StringComparison.OrdinalIgnoreCase) ||
                    fileKey.Contains("wingding", StringComparison.OrdinalIgnoreCase) ||
                    fileKey.Contains("webding", StringComparison.OrdinalIgnoreCase) ||
                    fileKey.Contains("symbol", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (TryReadSfntFile(kvp.Value, out var anyBytes))
                {
                    return anyBytes;
                }
            }

            foreach (string path in IndexedFontFiles.Value.Values)
            {
                if (TryReadSfntFile(path, out var anyBytes))
                {
                    return anyBytes;
                }
            }

            throw new InvalidOperationException($"Unable to locate a usable TrueType/OpenType font for face '{faceName}'.");
        }

        private static string StripSubsetPrefix(string? familyName)
        {
            string trimmed = (familyName ?? FallbackFontName).Trim();
            if (trimmed.Length > 7 && trimmed[6] == '+')
            {
                bool isTag = true;
                for (int i = 0; i < 6; i++)
                {
                    char c = trimmed[i];
                    if (c < 'A' || c > 'Z')
                    {
                        isTag = false;
                        break;
                    }
                }

                if (isTag)
                {
                    trimmed = trimmed[7..].Trim();
                }
            }

            foreach (string psSuffix in (string[])[
                ",BoldItalic", ",Bold", ",Italic",
                "-BoldItalicMT", "-BoldMT", "-ItalicMT", "PS-BoldItalicMT", "PS-BoldMT", "PS-ItalicMT",
                "PSMT", "-BoldItalic", "-BoldOblique", "-Bold", "-Italic", "-Oblique", "-Regular", "MT"])
            {
                if (trimmed.EndsWith(psSuffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > psSuffix.Length)
                {
                    trimmed = trimmed[..^psSuffix.Length].Trim();
                    break;
                }
            }

            if (trimmed.EndsWith("PS", StringComparison.Ordinal) &&
                trimmed.Contains("Times", StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > 2)
            {
                trimmed = trimmed[..^2].Trim();
            }

            return string.IsNullOrWhiteSpace(trimmed) ? FallbackFontName : trimmed;
        }

        private static IEnumerable<string> GetCandidateFileNames(string prefix, string suffix)
        {
            switch (prefix)
            {
                case "cour":
                    yield return suffix switch
                    {
                        "bd" => "courbd.ttf",
                        "i" => "couri.ttf",
                        "bi" => "courbi.ttf",
                        _ => "cour.ttf"
                    };
                    yield return "LiberationMono-Regular.ttf";
                    yield return "DejaVuSansMono.ttf";
                    yield return "cour.ttf";
                    break;
                case "times":
                    yield return suffix switch
                    {
                        "bd" => "timesbd.ttf",
                        "i" => "timesi.ttf",
                        "bi" => "timesbi.ttf",
                        _ => "times.ttf"
                    };
                    yield return "LiberationSerif-Regular.ttf";
                    yield return "DejaVuSerif.ttf";
                    yield return "times.ttf";
                    break;
                case "segoe":
                    yield return suffix switch
                    {
                        "bd" => "segoeuib.ttf",
                        "i" => "segoeuii.ttf",
                        "bi" => "segoeuiz.ttf",
                        _ => "segoeui.ttf"
                    };
                    yield return "segoeui.ttf";
                    break;
            }

            yield return suffix switch
            {
                "bd" => "arialbd.ttf",
                "i" => "ariali.ttf",
                "bi" => "arialbi.ttf",
                _ => "arial.ttf"
            };
            yield return "arial.ttf";
            yield return "segoeui.ttf";
            yield return "calibri.ttf";
            yield return "tahoma.ttf";
            yield return "verdana.ttf";
            yield return "LiberationSans-Regular.ttf";
            yield return "DejaVuSans.ttf";
            yield return "NotoSans-Regular.ttf";
            yield return "Ubuntu-R.ttf";
            yield return "Roboto-Regular.ttf";
            yield return "FreeSans.ttf";
            yield return "Helvetica.ttf";
            yield return "JetBrainsMono-Regular.ttf";
        }

        private static IEnumerable<string> GetCandidateSkiaFamilies(string prefix) => prefix switch
        {
            "cour" => ["Courier New", "Liberation Mono", "DejaVu Sans Mono", "Consolas", "Arial"],
            "times" => ["Times New Roman", "Liberation Serif", "DejaVu Serif", "Georgia", "Arial"],
            "segoe" => ["Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans"],
            _ => ["Arial", "Liberation Sans", "DejaVu Sans", "Segoe UI", "Helvetica", "Noto Sans"]
        };

        private static bool TryLoadViaSkia(string familyName, string suffix, out byte[] bytes)
        {
            bytes = [];
            try
            {
                var weight = suffix is "bd" or "bi" ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                var slant = suffix is "i" or "bi" ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                using var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
                using var typeface = SKFontManager.Default.MatchFamily(familyName, style);
                if (typeface is null) return false;
                return TryExtractSfntBytes(typeface, out bytes);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLoadFallbackViaSkia(string suffix, out byte[] bytes)
        {
            bytes = [];
            try
            {
                var weight = suffix is "bd" or "bi" ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                var slant = suffix is "i" or "bi" ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                using var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
                using var typeface = SKFontManager.Default.MatchCharacter(null, style, null, 'A');
                if (typeface is null) return false;
                return TryExtractSfntBytes(typeface, out bytes);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryExtractSfntBytes(SKTypeface typeface, out byte[] bytes)
        {
            bytes = [];
            using var stream = typeface.OpenStream(out int ttcIndex);
            if (stream is null || ttcIndex > 0) return false;

            using var ms = stream.HasLength && stream.Length > 0
                ? new MemoryStream(stream.Length)
                : new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, read);
            }
            byte[] data = ms.ToArray();
            if (!IsSingleSfntFont(data)) return false;

            bytes = data;
            return true;
        }

        private static bool TryReadIndexedFontFile(string fileName, out byte[] bytes)
        {
            bytes = [];
            if (!IndexedFontFiles.Value.TryGetValue(fileName, out string? path)) return false;
            return TryReadSfntFile(path, out bytes);
        }

        private static bool TryReadSfntFile(string path, out byte[] bytes)
        {
            bytes = [];
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (!IsSingleSfntFont(data)) return false;
                bytes = data;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSingleSfntFont(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12) return false;
            // Single TTF/OTF signatures: 0x00010000, 'OTTO', 'true', 'typ1'. Reject 'ttcf' collections.
            return (data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00)
                || (data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O')
                || (data[0] == 't' && data[1] == 'r' && data[2] == 'u' && data[3] == 'e')
                || (data[0] == 't' && data[1] == 'y' && data[2] == 'p' && data[3] == '1');
        }

        private static IReadOnlyDictionary<string, string> BuildFontFileIndex()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidateDirs = new List<string>();

            string sysFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrWhiteSpace(sysFonts)) candidateDirs.Add(sysFonts);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidateDirs.Add(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts"));
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                candidateDirs.Add(Path.Combine(home, ".local", "share", "fonts"));
                candidateDirs.Add(Path.Combine(home, "Library", "Fonts"));
            }

            candidateDirs.Add("/usr/share/fonts");
            candidateDirs.Add("/usr/local/share/fonts");
            candidateDirs.Add("/System/Library/Fonts");
            candidateDirs.Add("/Library/Fonts");
            candidateDirs.Add(AppContext.BaseDirectory);

            foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories)
                        .Concat(Directory.EnumerateFiles(dir, "*.otf", SearchOption.AllDirectories)))
                    {
                        string name = Path.GetFileName(file);
                        index.TryAdd(name, file);
                    }
                }
                catch
                {
                    // Skip unreadable directories safely.
                }
            }

            return index;
        }
    }
}
