using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSoundPad.App.Models;

namespace FreeSoundPad.App.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppData Load()
    {
        try
        {
            AppPaths.Ensure();
            if (!File.Exists(AppPaths.ConfigFile))
            {
                var fresh = new AppData();
                fresh.Bookmarks.AddRange(DefaultBookmarks());
                return fresh;
            }

            var json = File.ReadAllText(AppPaths.ConfigFile);
            var data = JsonSerializer.Deserialize<AppData>(json, Options) ?? new AppData();
            data.Pads ??= new List<PadData>();
            data.Bookmarks ??= new List<BookmarkData>();
            data.Settings ??= new SettingsData();

            if (data.Bookmarks.Count == 0) data.Bookmarks.AddRange(DefaultBookmarks());
            return data;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось загрузить настройки, использую значения по умолчанию", ex);
            TryBackupBrokenConfig();
            var fallback = new AppData();
            fallback.Bookmarks.AddRange(DefaultBookmarks());
            return fallback;
        }
    }

    public static void Save(AppData data)
    {
        try
        {
            AppPaths.Ensure();
            var json = JsonSerializer.Serialize(data, Options);
            var temp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(AppPaths.ConfigFile))
            {
                File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + ".bak", overwrite: true);
            }

            File.Move(temp, AppPaths.ConfigFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
        }
    }

    public static IReadOnlyList<BookmarkData> DefaultBookmarks() => new List<BookmarkData>
    {
        new() { Title = "YouTube Music", Url = "https://music.youtube.com", Color = "FF6B6B" },
        new() { Title = "YouTube", Url = "https://www.youtube.com", Color = "FF5C6C" },
        new() { Title = "Twitch", Url = "https://www.twitch.tv", Color = "9B8CFF" },
        new() { Title = "SoundCloud", Url = "https://soundcloud.com", Color = "FFB454" },
        new() { Title = "Spotify", Url = "https://open.spotify.com", Color = "3FE0B0" },
        new() { Title = "Telegram Web", Url = "https://web.telegram.org", Color = "4CC9F0" },
    };

    private static void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile)) return;
            var backup = AppPaths.ConfigFile + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(AppPaths.ConfigFile, backup, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }
}
