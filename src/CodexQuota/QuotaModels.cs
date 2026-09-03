using System.Text.Json;

namespace CodexQuota;

internal readonly record struct QuotaWindow(int? DurationMinutes, int UsedPercent, long? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed class QuotaSet
{
    private readonly IReadOnlyList<QuotaWindow> _windows;

    private QuotaSet(IReadOnlyList<QuotaWindow> windows)
    {
        _windows = windows;
    }

    public QuotaWindow? FiveHour => SelectExactWindow(300);

    public QuotaWindow? Week => SelectClosestWindow(10_080, 720);

    public static QuotaSet FromRateLimitsResponse(JsonElement result)
    {
        var windows = new List<QuotaWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var snapshot in EnumerateSnapshots(result))
        {
            AddWindow(snapshot, "primary", windows, seen);
            AddWindow(snapshot, "secondary", windows, seen);
        }

        return new QuotaSet(windows);
    }

    private QuotaWindow? SelectClosestWindow(int expectedDurationMinutes, int toleranceMinutes)
    {
        var candidates = _windows
            .Where(window => window.DurationMinutes is int duration && Math.Abs(duration - expectedDurationMinutes) <= toleranceMinutes)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - expectedDurationMinutes))
            .ToList();

        return candidates.Count == 0 ? null : candidates[0];
    }

    private QuotaWindow? SelectExactWindow(int expectedDurationMinutes)
    {
        foreach (var window in _windows)
        {
            if (window.DurationMinutes == expectedDurationMinutes)
            {
                return window;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateSnapshots(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var many) && many.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in many.EnumerateObject())
            {
                yield return property.Value;
            }

            yield break;
        }

        if (result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            yield return single;
        }
    }

    private static void AddWindow(JsonElement snapshot, string propertyName, ICollection<QuotaWindow> windows, ISet<string> seen)
    {
        if (!snapshot.TryGetProperty(propertyName, out var rawWindow) || rawWindow.ValueKind != JsonValueKind.Object ||
            !rawWindow.TryGetProperty("usedPercent", out var rawUsedPercent) || !rawUsedPercent.TryGetInt32(out var usedPercent))
        {
            return;
        }

        int? duration = rawWindow.TryGetProperty("windowDurationMins", out var rawDuration) && rawDuration.TryGetInt32(out var durationValue)
            ? durationValue
            : null;
        long? resetsAt = rawWindow.TryGetProperty("resetsAt", out var rawResetsAt) && rawResetsAt.TryGetInt64(out var resetsAtValue)
            ? resetsAtValue
            : null;

        var key = $"{duration}:{usedPercent}:{resetsAt}";
        if (seen.Add(key))
        {
            windows.Add(new QuotaWindow(duration, usedPercent, resetsAt));
        }
    }
}
