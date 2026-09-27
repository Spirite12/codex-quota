using System.Globalization;
using CodexQuota.Localization;

namespace CodexQuota;

internal readonly record struct QuotaDisplayValue(string Value, string ResetInfo);

internal static class QuotaDisplayFormatter
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    public static QuotaDisplayValue Format(QuotaWindow? quota, bool isWeek, DateTimeOffset now)
    {
        if (quota is not QuotaWindow currentQuota)
        {
            return new QuotaDisplayValue("—", string.Empty);
        }

        if (currentQuota.ResetsAt is not long resetsAt)
        {
            return new QuotaDisplayValue($"{currentQuota.RemainingPercent}%", string.Empty);
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetsAt).ToLocalTime();
        if (currentQuota.RemainingPercent == 0)
        {
            return new QuotaDisplayValue(FormatZeroQuotaReset(resetAt, isWeek), string.Empty);
        }

        var nowLocal = now.ToLocalTime();
        var remaining = resetAt - nowLocal;
        var percentage = $"{currentQuota.RemainingPercent}%";

        if (!isWeek)
        {
            var withinOneHour = IsWithin(remaining, TimeSpan.FromHours(1));
            return currentQuota.RemainingPercent < 20 || withinOneHour
                ? new QuotaDisplayValue(percentage, FormatTime(resetAt))
                : new QuotaDisplayValue(percentage, string.Empty);
        }

        var withinFiveHours = IsWithin(remaining, FiveHours);
        var withinOneDay = !withinFiveHours && IsWithin(remaining, OneDay);
        if (currentQuota.RemainingPercent >= 20 && !withinFiveHours && !withinOneDay)
        {
            return new QuotaDisplayValue(percentage, string.Empty);
        }

        return new QuotaDisplayValue(percentage, FormatWeekReset(resetAt, nowLocal, remaining));
    }

    private static bool IsWithin(TimeSpan remaining, TimeSpan threshold)
    {
        return remaining >= TimeSpan.Zero && remaining <= threshold;
    }

    private static string FormatWeekReset(DateTimeOffset resetAt, DateTimeOffset now, TimeSpan remaining)
    {
        if (IsWithin(remaining, FourHours))
        {
            return FormatTime(resetAt);
        }

        if (resetAt.Date == now.Date)
        {
            return UiText.T("今日", "Today");
        }

        if (resetAt.Date == now.Date.AddDays(1))
        {
            return UiText.T("明日", "Tomorrow");
        }

        return UiText.T(
            $"{resetAt.Month}.{resetAt.Day}",
            resetAt.ToString("MMM d", CultureInfo.CurrentUICulture));
    }

    private static string FormatZeroQuotaReset(DateTimeOffset resetAt, bool isWeek)
    {
        return isWeek
            ? UiText.T(
                $"{resetAt.Month}月{resetAt.Day}日",
                resetAt.ToString("MMM d", CultureInfo.CurrentUICulture))
            : FormatTime(resetAt);
    }

    private static string FormatTime(DateTimeOffset resetAt)
    {
        return UiText.T(
            resetAt.ToString("HH:mm", CultureInfo.InvariantCulture),
            resetAt.ToString("t", CultureInfo.CurrentUICulture));
    }
}
