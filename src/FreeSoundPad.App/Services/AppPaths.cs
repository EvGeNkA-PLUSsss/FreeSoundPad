namespace FreeSoundPad.App.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(BaseDirectory, "portable.txt"));

    public static string Root { get; } = IsPortable
        ? Path.Combine(BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSoundPad");

    public static string SoundsDirectory => Path.Combine(Root, "Sounds");
    public static string WebViewDirectory => Path.Combine(Root, "WebView2");
    public static string ConfigFile => Path.Combine(Root, "freesoundpad.json");
    public static string LogFile => Path.Combine(Root, "freesoundpad.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SoundsDirectory);
        Directory.CreateDirectory(WebViewDirectory);
    }
}
