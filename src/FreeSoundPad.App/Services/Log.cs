namespace FreeSoundPad.App.Services;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception == null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.Ensure();
                var file = AppPaths.LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                {
                    File.Delete(file);
                }

                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
