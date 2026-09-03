using System.Text.Json;
using CodexQuota;

var sample = JsonDocument.Parse("""
{
  "rateLimits": {
    "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1730947200 },
    "secondary": { "usedPercent": 42, "windowDurationMins": 10080, "resetsAt": 1731552000 }
  }
}
""");

var mapped = QuotaSet.FromRateLimitsResponse(sample.RootElement);
if (mapped.FiveHour?.RemainingPercent != 75 || mapped.Week?.RemainingPercent != 58)
{
    Console.Error.WriteLine("本地 5H/周窗口映射测试失败。");
    return 1;
}

await using var client = new CodexAppServerClient();
var stage = "initialize";
try
{
    await client.StartAsync(CancellationToken.None);
    stage = "account/read";
    var account = await client.GetAccountAsync(CancellationToken.None);
    if (!account.IsSignedInWithChatGpt)
    {
        Console.Error.WriteLine("未检测到已登录的 ChatGPT Codex 账号；未继续查询额度。");
        return 2;
    }

    stage = "account/rateLimits/read";
    var quotas = await client.GetRateLimitsAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        authenticated = true,
        fiveHourWindowReturned = quotas.FiveHour is not null,
        weekWindowReturned = quotas.Week is not null
    }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"只读协议探针失败于 {stage}：{exception.GetType().Name}");
    return 3;
}
