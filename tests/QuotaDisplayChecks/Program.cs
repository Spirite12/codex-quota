using System.Globalization;
using CodexQuota;

CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

var today = DateTime.Now.Date;
var tenAm = today.AddHours(10);
var now = new DateTimeOffset(tenAm, TimeZoneInfo.Local.GetUtcOffset(tenAm));
var lateNightTime = today.AddHours(23);
var lateNight = new DateTimeOffset(lateNightTime, TimeZoneInfo.Local.GetUtcOffset(lateNightTime));
var futureDate = now.AddDays(3).ToLocalTime();

var checks = new (string Name, QuotaWindow Quota, bool IsWeek, DateTimeOffset Now, string Value, string ResetInfo)[]
{
    ("5H 恰好1小时", Window(50, now.AddHours(1)), false, now, "50%", "11:00"),
    ("5H 超过1小时", Window(50, now.AddHours(1).AddSeconds(1)), false, now, "50%", string.Empty),
    ("5H 低于20%", Window(19, now.AddHours(3)), false, now, "19%", "13:00"),
    ("5H 恰好20%", Window(20, now.AddHours(2)), false, now, "20%", string.Empty),
    ("1W 恰好5小时", Window(50, now.AddHours(5)), true, now, "50%", "今日"),
    ("1W 恰好4小时", Window(50, now.AddHours(4)), true, now, "50%", "14:00"),
    ("1W 明天过期且还剩3小时", Window(50, lateNight.AddHours(3)), true, lateNight, "50%", "02:00"),
    ("1W 恰好24小时", Window(50, now.AddDays(1)), true, now, "50%", "明日"),
    ("1W 明天过期且超过4小时", Window(50, now.AddHours(18)), true, now, "50%", "明日"),
    ("1W 低于20%且提前多天", Window(19, now.AddDays(3)), true, now, "19%", $"{futureDate.Month}.{futureDate.Day}"),
    ("1W 低于20%且今天4小时内到期", Window(19, now.AddHours(3)), true, now, "19%", "13:00"),
    ("1W 恰好20%且超过一天", Window(20, now.AddDays(3)), true, now, "20%", string.Empty),
    ("5H 到0沿用旧规则", Window(0, now.AddHours(2)), false, now, "12:00", string.Empty),
    ("1W 到0沿用旧规则", Window(0, now.AddDays(3)), true, now, $"{futureDate.Month}月{futureDate.Day}日", string.Empty)
};

foreach (var check in checks)
{
    var actual = QuotaDisplayFormatter.Format(check.Quota, check.IsWeek, check.Now);
    if (actual.Value != check.Value || actual.ResetInfo != check.ResetInfo)
    {
        Console.Error.WriteLine($"{check.Name}失败：得到“{actual.Value} {actual.ResetInfo}”。");
        return 1;
    }
}

var missingReset = QuotaDisplayFormatter.Format(new QuotaWindow(300, 81, null), isWeek: false, now);
if (missingReset.Value != "19%" || !string.IsNullOrEmpty(missingReset.ResetInfo))
{
    Console.Error.WriteLine("重置时间缺失时应只显示百分比。");
    return 1;
}

Console.WriteLine($"额度胶囊显示检查通过：{checks.Length + 1} 项。");
return 0;

static QuotaWindow Window(int remainingPercent, DateTimeOffset resetAt)
{
    return new QuotaWindow(null, 100 - remainingPercent, resetAt.ToUnixTimeSeconds());
}
