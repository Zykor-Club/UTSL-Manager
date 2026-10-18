using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace UTSLManager.Servers;

/// <summary>
/// 定时任务（对应 MCSManager 的「计划任务」）：按 5 段 cron 在指定档上执行 启动 / 停机 / 重启 / 发命令。
/// 存在的意义：服务器要能"凌晨 4 点自动重启一次"，而不是每天手动点。
/// </summary>
public sealed class ScheduledTask
{
    public string Id { get; set; } = "task-" + Guid.NewGuid().ToString("N")[..6];
    public string ProfileId { get; set; } = "";
    /// <summary>start | stop | restart | command</summary>
    public string Kind { get; set; } = "restart";
    /// <summary>Kind=command 时要发到控制台的指令（如 /save、/announce …）。</summary>
    public string Command { get; set; } = "";
    /// <summary>5 段 cron：分 时 日 月 周（周 0/7 = 周日）。</summary>
    public string Cron { get; set; } = "0 4 * * *";
    public bool Enabled { get; set; } = true;
    /// <summary>上次触发时间（yyyy-MM-dd HH:mm），防同一分钟重复触发、也方便界面显示。</summary>
    public string LastRunAt { get; set; } = "";
    public string LastResult { get; set; } = "";

    public string KindText => Kind switch
    {
        "start" => "启动",
        "stop" => "停机",
        "restart" => "重启",
        "command" => "发命令",
        _ => Kind,
    };

    public string Summary => Kind == "command" ? $"发命令 {Command}" : KindText;

    /// <summary>
    /// 5 段 cron 匹配。支持 `*`、`*/n`、数字、逗号列表、`a-b` 区间与 `a-b/n`。
    /// 不引入第三方 cron 库：我们要的语义就这么多，多一条依赖多一处风险。
    /// </summary>
    public static bool CronMatches(string expr, DateTime now)
    {
        var f = (expr ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) { return false; }
        return MatchField(f[0], now.Minute, 0, 59)
            && MatchField(f[1], now.Hour, 0, 23)
            && MatchField(f[2], now.Day, 1, 31)
            && MatchField(f[3], now.Month, 1, 12)
            && MatchField(f[4], (int)now.DayOfWeek, 0, 7);
    }

    private static bool MatchField(string spec, int value, int min, int max)
    {
        foreach (var part in spec.Split(','))
        {
            string p = part.Trim();
            if (p.Length == 0) { continue; }
            int step = 1;
            int slash = p.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(p[(slash + 1)..], out step) || step <= 0) { continue; }
                p = p[..slash];
            }
            if (p == "*")
            {
                if (value >= min && value <= max && (value - min) % step == 0) { return true; }
                continue;
            }
            int a, b;
            int dash = p.IndexOf('-');
            if (dash > 0)
            {
                if (!int.TryParse(p[..dash], out a) || !int.TryParse(p[(dash + 1)..], out b)) { continue; }
            }
            else
            {
                if (!int.TryParse(p, out a)) { continue; }
                b = a;
            }
            if (value >= a && value <= b && (value - a) % step == 0) { return true; }
        }
        return false;
    }

    /// <summary>人类可读的调度描述（界面用；只覆盖常见形态，看不懂就原样显示 cron）。</summary>
    public string CronText
    {
        get
        {
            var f = (Cron ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5) { return Cron ?? ""; }
            if (f[0] == "*" && f[1] == "*" && f[2] == "*" && f[3] == "*" && f[4] == "*") { return "每分钟"; }
            if (f[0].StartsWith("*/") && f[1] == "*" && f[2] == "*" && f[3] == "*" && f[4] == "*") { return $"每 {f[0][2..]} 分钟"; }
            if (f[1] == "*" && f[2] == "*" && f[3] == "*" && f[4] == "*" && int.TryParse(f[0], out int mm))
            {
                return $"每小时 {mm:00} 分";
            }
            if (f[2] == "*" && f[3] == "*" && f[4] == "*"
                && int.TryParse(f[0], out int m2) && int.TryParse(f[1], out int h2))
            {
                return $"每天 {h2:00}:{m2:00}";
            }
            return Cron ?? "";
        }
    }

    /// <summary>下一次会触发的时刻（从 now 起最多看 48 小时）；算不出来返回空串。
    /// 任务卡上显示"下次 04:00"，比只写 cron 表达式好用得多。</summary>
    public string NextRunText(DateTime now)
    {
        try
        {
            var t = now.AddSeconds(-now.Second).AddMinutes(1);
            for (int i = 0; i < 48 * 60; i++)
            {
                if (CronMatches(Cron, t))
                {
                    bool sameDay = t.Date == now.Date;
                    bool tomorrow = t.Date == now.Date.AddDays(1);
                    string hm = t.ToString("HH:mm");
                    return sameDay ? hm : (tomorrow ? "明天 " + hm : t.ToString("MM-dd HH:mm"));
                }
                t = t.AddMinutes(1);
            }
        }
        catch { }
        return "";
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["profileId"] = ProfileId,
        ["kind"] = Kind,
        ["command"] = Command,
        ["cron"] = Cron,
        ["enabled"] = Enabled,
        ["lastRunAt"] = LastRunAt,
        ["lastResult"] = LastResult,
    };

    public static ScheduledTask FromJson(JsonObject o) => new()
    {
        Id = o["id"]?.ToString() ?? "task-" + Guid.NewGuid().ToString("N")[..6],
        ProfileId = o["profileId"]?.ToString() ?? "",
        Kind = o["kind"]?.ToString() ?? "restart",
        Command = o["command"]?.ToString() ?? "",
        Cron = o["cron"]?.ToString() ?? "0 4 * * *",
        Enabled = o["enabled"]?.GetValue<bool>() ?? true,
        LastRunAt = o["lastRunAt"]?.ToString() ?? "",
        LastResult = o["lastResult"]?.ToString() ?? "",
    };
}
