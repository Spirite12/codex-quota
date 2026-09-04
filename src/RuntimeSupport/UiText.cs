using System.Globalization;

namespace CodexQuota.Localization;

internal static class UiText
{
    private static readonly bool IsChinese =
        string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);

    public static string T(string chinese, string english)
    {
        return IsChinese ? chinese : english;
    }
}
