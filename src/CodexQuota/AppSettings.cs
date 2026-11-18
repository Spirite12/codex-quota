using System.Text.Json;
using System.IO;

namespace CodexQuota;

internal sealed record AppSettings(int FallbackRefreshSeconds, double HostPollSeconds)
{
    public static AppSettings Default { get; } = new(60, 0.6);

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
            Math.Clamp(settings.HostPollSeconds, 0.6, 10));
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}
