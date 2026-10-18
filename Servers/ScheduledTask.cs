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

    /// <summary>人类可读的调度描述。覆盖常见形态；看不懂就原样显示 cron（绝不给错的中文）。
    /// ★ 之前只处理"分钟=数字 + 小时=*"，遇到 `0 */6 * * *` 会退化成原样显示，所以这里按
    ///   「分钟 / 小时 / 日 / 月 / 周」逐段解释，再拼句子。</summary>
    public string CronText
    {
        get
        {
            var f = (Cron ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 5) { return Cron ?? ""; }
            string mm = f[0], hh = f[1], dom = f[2], mon = f[3], dow = f[4];
            bool star = dom == "*" && mon == "*" && dow == "*";

            if (mm == "*" && hh == "*" && star) { return "每分钟"; }
            if (mm.StartsWith("*/") && hh == "*" && star) { return $"每 {mm[2..]} 分钟"; }

            // 小时是 */n：每 n 小时
            string hourText = hh.StartsWith("*/") && int.TryParse(hh[2..], out int hn) && hn > 0
                ? $"每 {hn} 小时"
                : (int.TryParse(hh, out int h) ? $"{h:00}" : "");

            // 分钟：数字 / 列表 / *
            string minText = mm == "*" ? ("每 " + (hh == "*" ? "分钟" : "小时内的每一分")) : (mm == "0" ? "整点" : mm);

            if (star)
            {
                if (hh == "*" && mm != "*") { return mm == "0" ? "每小时整点" : $"每小时第 {minText} 分"; }
                if (hh.StartsWith("*/")) { return minText == "整点" ? $"{hourText}（整点）" : $"{hourText}（第 {minText} 分）"; }
                if (int.TryParse(hh, out _) && int.TryParse(mm, out _)) { return $"每天 {hourText}:{int.Parse(mm):00}"; }
                if (int.TryParse(hh, out _)) { return $"每天 {hourText}:{minText}"; }
            }
            // 周几
            if (dom == "*" && mon == "*" && dow != "*")
            {
                string day = dow switch
                {
                    "0" or "7" => "周日", "1" => "周一", "2" => "周二", "3" => "周三",
                    "4" => "周四", "5" => "周五", "6" => "周六",
                    "1-5" => "工作日", "0,6" or "6,0" or "0,7" => "周末",
                    _ => "周 " + dow,
                };
                if (int.TryParse(mm, out int m3) && int.TryParse(hh, out int h3)) { return $"每{day} {h3:00}:{m3:00}"; }
                return $"每{day}（{Cron}）";
            }
            // 每月
            if (mon == "*" && dow == "*" && dom != "*")
            {
                if (int.TryParse(mm, out int m4) && int.TryParse(hh, out int h4)) { return $"每月 {dom} 日 {h4:00}:{m4:00}"; }
                return $"每月 {dom} 日（{Cron}）";
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
