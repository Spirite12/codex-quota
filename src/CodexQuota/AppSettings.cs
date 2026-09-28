using System.Text.Json;
using System.IO;

namespace CodexQuota;

internal sealed record AppSettings(int FallbackRefreshSeconds, double HostPollSeconds, int BottomInsetPixels)
{
    public static AppSettings Default { get; } = new(120, 0.6, 24);

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "settings.json");
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings is null)
            {
                return Default;
            }

        return new AppSettings(
            Math.Clamp(settings.FallbackRefreshSeconds, 60, 3600),
            Math.Clamp(settings.HostPollSeconds, 0.6, 10),
            Math.Clamp(settings.BottomInsetPixels, 16, 160));
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}
