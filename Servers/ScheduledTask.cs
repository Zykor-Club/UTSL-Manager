using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace UTSLManager.Servers;

/// <summary>一条动作（照 MCSManager 的 ScheduleActionTypeEnum：Delay / Start / Stop / Restart / Command / Kill）。</summary>
public sealed class TaskAction
{
    /// <summary>start | stop | restart | kill | command | delay</summary>
    public string Type { get; set; } = "restart";
    /// <summary>type=command 时要发的指令；type=delay 时不用。</summary>
    public string Payload { get; set; } = "";
    /// <summary>type=delay 时等待的秒数。</summary>
    public int Seconds { get; set; } = 5;

    public string Text => Type switch
    {
        "start" => "启动",
        "stop" => "停机",
        "restart" => "重启",
        "kill" => "强制结束",
        "command" => "发送 " + Payload,
        "delay" => $"等待 {Seconds}s",
        _ => Type,
    };

    public JsonObject ToJson() => new() { ["type"] = Type, ["payload"] = Payload, ["seconds"] = Seconds };

    public static TaskAction FromJson(JsonObject o) => new()
    {
        Type = o["type"]?.ToString() ?? "restart",
        Payload = o["payload"]?.ToString() ?? "",
        Seconds = int.TryParse(o["seconds"]?.ToString(), out int s) && s >= 0 ? s : 5,
    };
}

/// <summary>
/// 定时任务。**结构与字段照抄 MCSManager 的 daemon 侧实现**（daemon/src/service/system_instance_control.ts）：
///   任务属于某台实例（我们的"档"），按 Name 在该实例内唯一；
///   Type = 1 间隔（Time 是秒数）｜Type = 2 定时（Time 是 cron，6 段，第 1 段是秒）；
///   Actions 是**动作数组**，一个任务可以依次执行多条（MCSM 就是这么设计的）。
/// </summary>
public sealed class ScheduledTask
{
    /// <summary>任务名：同一档内唯一（MCSM 用 instanceUuid + name 定位任务）。</summary>
    public string Name { get; set; } = "新任务";
    /// <summary>所属档（= MCSM 的 instanceUuid）。</summary>
    public string ProfileId { get; set; } = "";
    /// <summary>1 = 间隔（秒），2 = 定时（cron）。</summary>
    public int Type { get; set; } = 2;
    /// <summary>Type=1 时是秒数；Type=2 时是 cron。</summary>
    public string Time { get; set; } = "0 0 4 * * *";
    public List<TaskAction> Actions { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string LastRunAt { get; set; } = "";
    public string LastResult { get; set; } = "";

    /// <summary>动作串，用于列表显示：`发命令 /save → 等待 5s → 重启`。</summary>
    public string ActionsText
    {
        get
        {
            if (Actions.Count == 0) { return "(没有动作)"; }
            var parts = new List<string>();
            foreach (var a in Actions) { parts.Add(a.Text); }
            return string.Join(" → ", parts);
        }
    }

    /// <summary>时间部分的显示：间隔=每 N 秒；定时=给人看的说法（看不懂就原样给 cron）。</summary>
    public string TimeText => Type == 1
        ? (int.TryParse(Time, out int sec) ? $"每 {sec} 秒" : "每 ? 秒")
        : CronText;

    /// <summary>cron 的人类可读描述。支持 5 段（分 时 日 月 周）与 6 段（秒 分 时 日 月 周，MCSM 风格）。</summary>
    public string CronText
    {
        get
        {
            var f = (Time ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool six = f.Length == 6;
            if (!six && f.Length != 5) { return Time ?? ""; }
            string sec = six ? f[0] : "0";
            string mm = six ? f[1] : f[0], hh = six ? f[2] : f[1], dom = six ? f[3] : f[2], mon = six ? f[4] : f[3], dow = six ? f[5] : f[4];
            if (sec != "0" && sec != "*") { return Time ?? ""; }          // 带秒级的复杂表达式就不翻译了
            bool star = dom == "*" && mon == "*" && dow == "*";

            if (mm == "*" && hh == "*" && star) { return "每分钟"; }
            if (mm.StartsWith("*/") && hh == "*" && star) { return $"每 {mm[2..]} 分钟"; }
            if (int.TryParse(mm, out int m1))
            {
                if (hh == "*" && star) { return m1 == 0 ? "每小时整点" : $"每小时第 {m1} 分"; }
                if (hh.StartsWith("*/") && int.TryParse(hh[2..], out int hn) && star)
                {
                    return m1 == 0 ? $"每 {hn} 小时（整点）" : $"每 {hn} 小时（第 {m1} 分）";
                }
                if (int.TryParse(hh, out int h1))
                {
                    if (star) { return $"每天 {h1:00}:{m1:00}"; }
                    if (dom == "*" && mon == "*" && dow != "*")
                    {
                        string day = dow switch
                        {
                            "0" or "7" => "周日", "1" => "周一", "2" => "周二", "3" => "周三",
                            "4" => "周四", "5" => "周五", "6" => "周六", "1-5" => "工作日", _ => "周 " + dow,
                        };
                        return $"每{day} {h1:00}:{m1:00}";
                    }
                    if (dow == "*" && mon == "*" && dom != "*") { return $"每月 {dom} 日 {h1:00}:{m1:00}"; }
                }
            }
            return Time ?? "";
        }
    }

    /// <summary>cron 匹配。5 段按"分 时 日 月 周"，6 段按"秒 分 时 日 月 周"。</summary>
    public static bool CronMatches(string expr, DateTime now)
    {
        var f = (expr ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool six = f.Length == 6;
        if (!six && f.Length != 5) { return false; }
        string sec = six ? f[0] : "0";
        string mm = six ? f[1] : f[0], hh = six ? f[2] : f[1], dom = six ? f[3] : f[2], mon = six ? f[4] : f[3], dow = six ? f[5] : f[4];
        return Match(sec, now.Second, 0, 59) && Match(mm, now.Minute, 0, 59) && Match(hh, now.Hour, 0, 23)
            && Match(dom, now.Day, 1, 31) && Match(mon, now.Month, 1, 12) && Match(dow, (int)now.DayOfWeek, 0, 7);
    }

    private static bool Match(string spec, int value, int min, int max)
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
            if (p == "*") { if (value >= min && value <= max && (value - min) % step == 0) { return true; } continue; }
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

    /// <summary>下一次触发的时刻（最多看 48 小时）；间隔任务按"上次 + N 秒"推。</summary>
    public string NextRunText(DateTime now)
    {
        if (Type == 1)
        {
            if (!int.TryParse(Time, out int sec) || sec <= 0) { return ""; }
            return $"{sec} 秒内";
        }
        try
        {
            var t = now.AddSeconds(1);
            for (int i = 0; i < 48 * 3600; i++)
            {
                if (CronMatches(Time, t))
                {
                    bool sameDay = t.Date == now.Date;
                    bool tomorrow = t.Date == now.Date.AddDays(1);
                    string hm = t.ToString("HH:mm");
                    return sameDay ? hm : (tomorrow ? "明天 " + hm : t.ToString("MM-dd HH:mm"));
                }
                t = t.AddSeconds(1);
            }
        }
        catch { }
        return "";
    }

    public JsonObject ToJson()
    {
        var acts = new JsonArray();
        foreach (var a in Actions) { acts.Add(a.ToJson()); }
        return new JsonObject
        {
            ["name"] = Name,
            ["profileId"] = ProfileId,
            ["type"] = Type,
            ["time"] = Time,
            ["enabled"] = Enabled,
            ["lastRunAt"] = LastRunAt,
            ["lastResult"] = LastResult,
            ["actions"] = acts,
        };
    }

    public static ScheduledTask FromJson(JsonObject o)
    {
        var t = new ScheduledTask
        {
            Name = o["name"]?.ToString() ?? "任务",
            ProfileId = o["profileId"]?.ToString() ?? "",
            Type = int.TryParse(o["type"]?.ToString(), out int ty) ? ty : 2,
            Time = o["time"]?.ToString() ?? "0 0 4 * * *",
            Enabled = o["enabled"]?.GetValue<bool>() ?? true,
            LastRunAt = o["lastRunAt"]?.ToString() ?? "",
            LastResult = o["lastResult"]?.ToString() ?? "",
        };
        if (o["actions"] is JsonArray arr)
        {
            foreach (var n in arr) { if (n is JsonObject ao) { t.Actions.Add(TaskAction.FromJson(ao)); } }
        }
        // 老格式兼容：没有 actions 但有 kind/command
        if (t.Actions.Count == 0 && o["kind"] is not null)
        {
            t.Actions.Add(new TaskAction { Type = o["kind"]?.ToString() ?? "restart", Payload = o["command"]?.ToString() ?? "" });
            if (o["cron"] is not null) { t.Type = 2; t.Time = o["cron"]?.ToString() ?? t.Time; }
        }
        return t;
    }
}
