using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using UTSLManager.Plugins;

namespace ApiProbe;

/// <summary>
/// 管理器插件 API v3 的端到端自检：逐项调用新能力并输出真实结果。
/// 每一项独立 try/catch —— 权限被拒不会中断其余检查（这样一次就能看清哪些通、哪些被拒）。
/// 报告写到 %TEMP%\utsl-api-probe.txt，同时显示在插件视图并写进管理器终端。
/// </summary>
public sealed class ApiProbePlugin : IManagerPlugin
{
    public string Name => "API 探针";
    public string Icon => "🧪";
    public string Description => "管理器插件 API v3 端到端自检（文件/命令/HTTP/权限）";
    public string Author => "Zykor-Club";
    public int RequiredApiVersion => ManagerApi.V3;   // v3 门禁：宿主低于 v3 会跳过加载

    private IManagerContext? _ctx;
    private TextBlock? _view;

    public void OnLoad(IManagerContext ctx)
    {
        _ctx = ctx;
        ctx.RunBackground(async ct =>
        {
            await Task.Delay(1500, ct);   // 等管理器界面与隧道就绪
            string report = await RunAllAsync(ctx);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "utsl-api-probe.txt"), report, new UTF8Encoding(false));
            }
            catch { }
            ctx.Log("[API 探针] 报告已写入 " + Path.Combine(Path.GetTempPath(), "utsl-api-probe.txt"));
        }, "api-probe");
    }

    public void OnUnload() => _ctx = null;

    public Control? CreateView(IManagerContext ctx)
    {
        _view = new TextBlock
        {
            Text = "自检进行中…（结果也会写到 %TEMP%\\utsl-api-probe.txt）",
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"),
            Margin = new Avalonia.Thickness(14),
        };
        return _view;
    }

    private async Task<string> RunAllAsync(IManagerContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("UTSLManager 插件 API v3 探针  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('-', 72));

        void Head(string title) { sb.AppendLine(); sb.AppendLine("## " + title); }
        async Task Check(string name, Func<Task<string>> body)
        {
            try { sb.AppendLine("  [OK]   " + name.PadRight(26) + " " + await body()); }
            catch (Exception ex) { sb.AppendLine("  [FAIL] " + name.PadRight(26) + " " + ex.GetType().Name + ": " + ex.Message); }
        }

        Head("环境");
        await Check("FilesRoot", () => Task.FromResult(ctx.FilesRoot));
        await Check("RestBase", () => Task.FromResult(ctx.RestBase));
        await Check("Permissions", () => Task.FromResult(string.Join(",", ctx.Permissions)));
        await Check("Worlds", () => Task.FromResult(ctx.Worlds.Count + " 个世界: " + string.Join("/", ctx.Worlds.Select(w => w.Name))));

        Head("文件 API（远端走 SSH / 本机直接读盘）");
        await Check("ListFiles(\"\")", () =>
        {
            var l = ctx.ListFiles("");
            return Task.FromResult(l is null ? "null（未注入？）" : l.Count + " 项（目录 " + l.Count(x => x.IsDirectory) + " / 文件 " + l.Count(x => !x.IsDirectory) + "）");
        });
        await Check("ListFiles(\"config\")", () =>
        {
            var l = ctx.ListFiles("config");
            return Task.FromResult(l is null ? "null" : l.Count + " 项");
        });
        await Check("ReadText(config/config.json)", () =>
        {
            string t = ctx.ReadText(Path.Combine("config", "config.json"));
            return Task.FromResult(t.Length + " 字符，首非空白字符=" + (t.TrimStart().Length > 0 ? t.TrimStart()[0].ToString() : "-"));
        });
        string payload = "apiprobe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await Check("WriteText + ReadText 回读", () =>
        {
            bool w = ctx.WriteText("api_probe_test.txt", payload);
            string back = ctx.ReadText("api_probe_test.txt");
            return Task.FromResult("写=" + w + " 回读一致=" + (back == payload));
        });
        await Check("FileExists", () => Task.FromResult(ctx.FileExists("api_probe_test.txt").ToString()));
        await Check("Rename", () =>
        {
            bool r = ctx.Rename("api_probe_test.txt", "api_probe_test2.txt");
            return Task.FromResult("返回=" + r + " 旧存在=" + ctx.FileExists("api_probe_test.txt") + " 新存在=" + ctx.FileExists("api_probe_test2.txt"));
        });
        await Check("Upload/Download 往返比对", () =>
        {
            string up = Path.Combine(Path.GetTempPath(), "apiprobe_up.bin");
            byte[] buf = new byte[64 * 1024];
            new Random(20251011).NextBytes(buf);
            File.WriteAllBytes(up, buf);
            bool u = ctx.Upload(up, "api_probe_bin.dat");
            string down = Path.Combine(Path.GetTempPath(), "apiprobe_down.bin");
            if (File.Exists(down)) { File.Delete(down); }
            bool d = ctx.Download("api_probe_bin.dat", down);
            bool same = d && File.Exists(down) && File.ReadAllBytes(down).SequenceEqual(buf);
            ctx.Delete("api_probe_bin.dat");
            try { File.Delete(up); File.Delete(down); } catch { }
            return Task.FromResult("上传=" + u + " 下载=" + d + " 逐字节一致=" + same);
        });
        await Check("Delete", () =>
        {
            bool del = ctx.Delete("api_probe_test2.txt");
            return Task.FromResult("返回=" + del + " 之后仍存在=" + ctx.FileExists("api_probe_test2.txt"));
        });
        await Check("越界防护(../..)", () =>
        {
            try { ctx.ReadText("../../windows/win.ini"); return Task.FromResult("✗ 未被拦截（安全问题）"); }
            catch (IOException ex) { return Task.FromResult("✓ 已拦截: " + ex.Message); }
        });

        Head("缓存");
        await Check("InvalidateFileCache + 再列目录", () =>
        {
            ctx.InvalidateFileCache();
            var l = ctx.ListFiles("");
            return Task.FromResult("失效后 " + (l?.Count ?? -1) + " 项");
        });

        Head("命令（exec 权限）");
        await Check("RunCommand(远端/本机外壳)", async () =>
        {
            string o = await ctx.RunCommand("'probe-' + $env:COMPUTERNAME + '-' + (Get-Date).ToString('HH:mm:ss')", 30000);
            return o.Trim();
        });

        Head("HTTP（net 权限）");
        await Check("HttpGet(REST 玩家列表)", async () =>
        {
            string t = await ctx.HttpGet(ctx.RestBase + "/v2/players/list?token=" + Uri.EscapeDataString(ctx.Token));
            string one = t.Replace("\r", "").Replace("\n", " ").Trim();
            return one.Substring(0, Math.Min(90, one.Length));
        });

        Head("旧 API 回归（rest 权限）");
        await Check("RestGet(/world/list)", async () =>
        {
            string t = await ctx.RestGet("/world/list");
            return t.Length + " 字符";
        });
        await Check("RestPost(/v2/world/save)", async () =>
        {
            string t = await ctx.RestPost("/v2/world/save");
            string one = t.Replace("\r", "").Replace("\n", " ").Trim();
            return one.Substring(0, Math.Min(70, one.Length));
        });

        sb.AppendLine();
        sb.AppendLine(new string('-', 72));
        sb.AppendLine("  说明：某行 [FAIL] 为 PluginPermissionException 表示该权限未在本插件 manifest 里声明 —— 这正是权限门禁生效。");
        string text = sb.ToString();
        _view?.Dispatcher.Post(() => { if (_view is not null) { _view.Text = text; } });
        foreach (string line in text.Split('\n')) { if (line.Trim().Length > 0) { ctx.Log("[API 探针] " + line.TrimEnd()); } }
        return text;
    }
}
