using System.Text;

namespace PdfStudio.App.Services;

public static class CrashLogger
{
    private static readonly Lock Gate = new();

    public static string GetDefaultLogPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PdfStudio", "logs", "crash.log");
    }

    public static void LogException(Exception? exception, string context, string? customLogPath = null)
    {
        string path = !string.IsNullOrWhiteSpace(customLogPath) ? customLogPath : GetDefaultLogPath();

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.Append('[')
              .Append(DateTimeOffset.Now.ToString("O"))
              .Append("] [")
              .Append(context)
              .AppendLine("]");

            if (exception is not null)
            {
                sb.AppendLine($"{exception.GetType().FullName}: {exception.Message}");
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    sb.AppendLine(exception.StackTrace);
                }

                var inner = exception.InnerException;
                while (inner is not null)
                {
                    sb.AppendLine($"---> Inner {inner.GetType().FullName}: {inner.Message}");
                    if (!string.IsNullOrWhiteSpace(inner.StackTrace))
                    {
                        sb.AppendLine(inner.StackTrace);
                    }
                    inner = inner.InnerException;
                }
            }
            else
            {
                sb.AppendLine("Unknown non-CLS exception object raised.");
            }

            sb.AppendLine(new string('-', 60));

            lock (Gate)
            {
                File.AppendAllText(path, sb.ToString());
            }
        }
        catch
        {
            // Never throw from global exception logger
        }
    }
}

