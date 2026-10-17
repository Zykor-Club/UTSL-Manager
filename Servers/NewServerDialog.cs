using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UTSLManager.Servers;

/// <summary>
/// 「新增服务器」对话框（纯 C# 建控件，和连接设置页同一套深色配色）。
/// 只负责收集字段并造一个 <see cref="ServerProfile"/>；端口由调用方预先分配好传进来，
/// 落盘/切档仍由 MainWindow 做（对话框不碰配置文件和网络）。
/// </summary>
internal static class NewServerDialog
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static async Task<ServerProfile?> ShowAsync(Window owner, ServerProfile tpl, int restPort, int agentPort, string defaultName)
    {
        var win = new Window
        {
            Title = "新增服务器",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("#0C0E12"),
        };

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 2 };

        TextBlock Label(string s) => new() { Text = s, FontSize = 11, Foreground = B("#9BA3AE"), Margin = new Thickness(0, 8, 0, 2) };
        TextBox Field(string value) => new() { Text = value, FontSize = 12, CornerRadius = new CornerRadius(8) };

        root.Children.Add(new TextBlock { Text = "新增服务器", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = B("#F2F2F5") });
        root.Children.Add(new TextBlock
        {
            Text = $"本地隧道端口自动分配（本档 REST {restPort} / agent {agentPort}）—— 多台各有各的一对端口，不会串到同一台上去。",
            FontSize = 11, Foreground = B("#848D9A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        });

        root.Children.Add(Label("名称（随便起，只影响卡片显示）"));
        var tbName = Field(defaultName);
        root.Children.Add(tbName);

        var mode = new ToggleSwitch
        {
            OnContent = "远端（走 SSH 隧道）",
            OffContent = "本机（管本机目录）",
            IsChecked = tpl.HasSsh,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
        };
        root.Children.Add(mode);

        // 远端字段（整组显隐）
        var remote = new StackPanel();
        remote.Children.Add(Label("SSH 主机"));
        var tbSshHost = Field(tpl.SshHost); remote.Children.Add(tbSshHost);
        var rowPortUser = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var pPort = new StackPanel(); pPort.Children.Add(Label("SSH 端口")); var tbSshPort = Field(tpl.SshPort.ToString()); pPort.Children.Add(tbSshPort);
        var pUser = new StackPanel(); pUser.Children.Add(Label("SSH 用户")); var tbSshUser = Field(tpl.SshUser); pUser.Children.Add(tbSshUser);
        Grid.SetColumn(pPort, 0); Grid.SetColumn(pUser, 2);
        rowPortUser.Children.Add(pPort); rowPortUser.Children.Add(pUser); remote.Children.Add(rowPortUser);
        remote.Children.Add(Label("SSH 私钥路径"));
        var tbKey = Field(tpl.SshKey); remote.Children.Add(tbKey);
        remote.Children.Add(Label("远端部署目录（= agent 的 --root）"));
        var tbRemoteDir = Field(tpl.RemoteDir); remote.Children.Add(tbRemoteDir);
        root.Children.Add(remote);

        // 本机字段（本机模式没有隧道，REST 端口必须指向本机服务端自己的端口）
        var local = new StackPanel();
        local.Children.Add(Label("本机部署目录"));
        var tbLocalDir = Field(tpl.LocalDir); local.Children.Add(tbLocalDir);
        local.Children.Add(Label("本机 REST 端口（UnifierTSL 的 config.json 里那个）"));
        var tbLocalPort = Field(tpl.IsRemote ? "7878" : tpl.Port.ToString());
        local.Children.Add(tbLocalPort);
        root.Children.Add(local);

        root.Children.Add(new TextBlock
        {
            Text = "REST 令牌先留空也行：创建后切到该服务器，在「连接设置 → 自动检测」里从服务端配置读，再点「保存并应用」。",
            FontSize = 11, Foreground = B("#848D9A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });

        var err = new TextBlock { FontSize = 11, Foreground = B("#FF7B72"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(err);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        var btnCancel = new Button { Content = "取消", FontSize = 12, Padding = new Thickness(16, 5), CornerRadius = new CornerRadius(9) };
        var btnOk = new Button { Content = "创建并切过去", FontSize = 12, Padding = new Thickness(16, 5), CornerRadius = new CornerRadius(9), Background = B("#66CCFF"), Foreground = B("#00263A") };
        btns.Children.Add(btnCancel); btns.Children.Add(btnOk);
        root.Children.Add(btns);

        void Sync()
        {
            bool isRemote = mode.IsChecked == true;
            remote.IsVisible = isRemote;
            local.IsVisible = !isRemote;
        }
        mode.IsCheckedChanged += (_, _) => Sync();
        Sync();

        win.Content = root;

        ServerProfile? result = null;
        btnCancel.Click += (_, _) => win.Close();
        btnOk.Click += (_, _) =>
        {
            string name = (tbName.Text ?? "").Trim();
            if (name.Length == 0) { err.Text = "名称不能为空"; return; }
            bool isRemote = mode.IsChecked == true;
            string sshHost = (tbSshHost.Text ?? "").Trim();
            string key = (tbKey.Text ?? "").Trim();
            if (isRemote && sshHost.Length == 0) { err.Text = "远端模式要填 SSH 主机"; return; }
            if (isRemote && key.Length == 0) { err.Text = "远端模式要填 SSH 私钥路径"; return; }
            if (!isRemote && (tbLocalDir.Text ?? "").Trim().Length == 0) { err.Text = "本机模式要填本机部署目录"; return; }

            result = new ServerProfile
            {
                Id = ServersConfig.NewId(),
                Name = name,
                Host = "127.0.0.1",
                // 远端档：Port 是本地隧道入口（自动分配的 restPort）；本机档：Port 必须指向本机服务端自己的 REST 端口
                Port = isRemote ? restPort : (int.TryParse(tbLocalPort.Text, out int lp) && lp > 0 ? lp : 7878),
                AgentPort = agentPort,
                SshHost = isRemote ? sshHost : "",
                SshPort = int.TryParse(tbSshPort.Text, out int sp) && sp > 0 ? sp : 22,
                SshUser = (tbSshUser.Text ?? "").Trim().Length > 0 ? (tbSshUser.Text ?? "").Trim() : "Administrator",
                SshKey = isRemote ? key : "",
                RemoteDir = (tbRemoteDir.Text ?? "").Trim().Length > 0 ? (tbRemoteDir.Text ?? "").Trim() : tpl.RemoteDir,
                RemoteDotnet = tpl.RemoteDotnet,
                RemoteLauncher = tpl.RemoteLauncher,
                RemoteRestPort = tpl.RemoteRestPort,
                LocalDir = (tbLocalDir.Text ?? "").Trim().Length > 0 ? (tbLocalDir.Text ?? "").Trim() : tpl.LocalDir,
                ForceLocal = !isRemote,
                Watchdog = tpl.Watchdog,
                WorldConsoles = tpl.WorldConsoles,
                Token = "",          // 先留空，创建后到连接设置里「自动检测」读服务端配置
            };
            win.Close();
        };

        await win.ShowDialog(owner);
        return result;
    }
}
