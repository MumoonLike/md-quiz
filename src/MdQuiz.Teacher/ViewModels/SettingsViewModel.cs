using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Server.Mcp;
using MdQuiz.Wpf;
using Microsoft.Win32;

namespace MdQuiz.Teacher.ViewModels
{
    public partial class SettingsViewModel : PageViewModel
    {
        public ObservableCollection<string> Addresses { get; } = new ObservableCollection<string>();

        [ObservableProperty] private string _serverName = "";
        [ObservableProperty] private string _port = "";
        [ObservableProperty] private string _dataDir = "";
        [ObservableProperty] private string _version = "";
        [ObservableProperty] private string _discoveryText = "";

        // ---------------- MCP ----------------
        public ObservableCollection<string> McpUrls { get; } = new ObservableCollection<string>();
        public ObservableCollection<McpLogItem> McpLog => Shell.McpLog;

        [ObservableProperty] private bool _mcpEnabled;
        [ObservableProperty] private string _mcpPort = "";
        [ObservableProperty] private bool _mcpAllowLan;
        [ObservableProperty] private string _mcpToken = "";
        [ObservableProperty] private bool _mcpRead = true;
        [ObservableProperty] private bool _mcpEdit;
        [ObservableProperty] private bool _mcpControl;
        [ObservableProperty] private bool _mcpRunning;
        [ObservableProperty] private string _mcpStatusText = "";
        [ObservableProperty] private string _mcpConfig = "";
        [ObservableProperty] private string _mcpCommand = "";
        [ObservableProperty] private bool _mcpDirty;

        partial void OnMcpEnabledChanged(bool value) => McpDirty = true;
        partial void OnMcpPortChanged(string value) => McpDirty = true;
        partial void OnMcpReadChanged(bool value) => McpDirty = true;
        partial void OnMcpEditChanged(bool value) => McpDirty = true;
        partial void OnMcpControlChanged(bool value) => McpDirty = true;

        partial void OnMcpAllowLanChanged(bool value)
        {
            McpDirty = true;
            if (value && string.IsNullOrWhiteSpace(McpToken)) McpToken = McpOptions.NewToken();
        }

        public override void OnActivated()
        {
            ServerName = S.ServerName;
            Port = S.ServerPort.ToString();
            DataDir = S.DataDir;
            Version = typeof(SettingsViewModel).Assembly.GetName().Version.ToString(3);
            Addresses.Clear();
            foreach (var ip in Discovery.LocalIPv4Addresses()) Addresses.Add($"{ip}:{S.Host.Port}");
            DiscoveryText = S.Host.DiscoveryEnabled
                ? $"已开启（UDP {Discovery.DefaultUdpPort}）。同一网段的学生端会自动找到本机。"
                : "未开启（UDP 端口被占用）。学生需要手动输入上面的地址。";
            LoadMcp();
        }

        private void LoadMcp()
        {
            var o = S.LoadMcpOptions();
            McpEnabled = S.McpEnabled;
            McpPort = o.Port.ToString();
            McpAllowLan = o.AllowLan;
            McpToken = o.Token ?? "";
            McpRead = o.Permissions.HasFlag(McpPermissions.Read);
            McpEdit = o.Permissions.HasFlag(McpPermissions.Edit);
            McpControl = o.Permissions.HasFlag(McpPermissions.Control);
            McpDirty = false;
            UpdateMcpInfo();
        }

        /// <summary>连接地址和客户端配置示例（按已保存并运行的设置）。</summary>
        private void UpdateMcpInfo()
        {
            Shell.UpdateMcpStatus();
            var mcp = S.Mcp;
            McpRunning = mcp.IsRunning;
            McpUrls.Clear();
            if (!mcp.IsRunning)
            {
                McpStatusText = S.McpEnabled ? "未启动（端口可能被占用）" : "已关闭";
                McpConfig = "";
                McpCommand = "";
                return;
            }
            var o = mcp.Options;
            var local = $"http://127.0.0.1:{mcp.Port}{McpServer.Endpoint}";
            McpUrls.Add(local);
            if (o.AllowLan)
                foreach (var ip in Discovery.LocalIPv4Addresses()) McpUrls.Add($"http://{ip}:{mcp.Port}{McpServer.Endpoint}");
            var perms = new List<string>();
            if (o.Permissions.HasFlag(McpPermissions.Read)) perms.Add("查询");
            if (o.Permissions.HasFlag(McpPermissions.Edit)) perms.Add("编辑");
            if (o.Permissions.HasFlag(McpPermissions.Control)) perms.Add("控制");
            McpStatusText = $"运行中 · {(o.AllowLan ? "允许局域网访问（需令牌）" : "仅本机")} · 权限：{(perms.Count == 0 ? "无" : string.Join(" / ", perms))}";
            var headers = o.RequireToken ? $",\n      \"headers\": {{ \"Authorization\": \"Bearer {o.Token}\" }}" : "";
            McpConfig = "{\n  \"mcpServers\": {\n    \"mdquiz\": {\n      \"type\": \"http\",\n      \"url\": \"" + local + "\"" + headers + "\n    }\n  }\n}";
            McpCommand = $"claude mcp add --transport http mdquiz {local}" + (o.RequireToken ? $" --header \"Authorization: Bearer {o.Token}\"" : "");
        }

        [RelayCommand]
        private void SaveMcp()
        {
            if (!int.TryParse(McpPort, out var port) || port < 1024 || port > 65535)
            {
                Ui.Error("MCP 端口应为 1024 ~ 65535 之间的数字");
                return;
            }
            if (port == S.ServerPort || port == Discovery.DefaultUdpPort)
            {
                Ui.Error($"MCP 端口不能与考试服务端口（{S.ServerPort}）或自动发现端口（{Discovery.DefaultUdpPort}）相同");
                return;
            }
            if (McpEnabled && McpAllowLan && McpControl &&
                !Ui.ConfirmDanger("你同时开启了“允许局域网访问”和“考试控制与改分”。\n\n持有令牌的设备可以开始 / 结束考试和改分。请确认令牌不会泄露给学生。继续吗？", "MCP 权限确认"))
                return;
            var permissions = McpPermissions.None;
            if (McpRead) permissions |= McpPermissions.Read;
            if (McpEdit) permissions |= McpPermissions.Edit;
            if (McpControl) permissions |= McpPermissions.Control;
            if (McpAllowLan && string.IsNullOrWhiteSpace(McpToken)) McpToken = McpOptions.NewToken();
            S.SaveMcpOptions(new McpOptions { Port = port, AllowLan = McpAllowLan, Token = McpToken.Trim(), Permissions = permissions });
            S.McpEnabled = McpEnabled;
            Shell.ApplyMcp(showSuccess: true);
            McpDirty = false;
            UpdateMcpInfo();
        }

        [RelayCommand]
        private void RegenerateToken()
        {
            if (!string.IsNullOrEmpty(McpToken) && S.Mcp.IsRunning && S.Mcp.Options.RequireToken &&
                !Ui.Confirm("重新生成后，已配置旧令牌的 AI 客户端需要更新配置。继续吗？", "重新生成令牌"))
                return;
            McpToken = McpOptions.NewToken();
            McpDirty = true;
        }

        [RelayCommand]
        private void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                Ui.Toast("已复制到剪贴板");
            }
            catch (Exception ex)
            {
                Ui.Error("复制失败：" + ex.Message);
            }
        }

        [RelayCommand]
        private void SaveServer()
        {
            if (!int.TryParse(Port, out var port) || port < 1024 || port > 65535)
            {
                Ui.Error("端口应为 1024 ~ 65535 之间的数字");
                return;
            }
            var exam = S.Host.CurrentExam;
            if (exam != null && exam.Status == ExamStatus.Running &&
                !Ui.ConfirmDanger("考试正在进行，重启服务会让所有学生短暂断线（会自动重连）。确定继续吗？")) return;
            S.ServerName = string.IsNullOrWhiteSpace(ServerName) ? Environment.MachineName : ServerName.Trim();
            S.ServerPort = port;
            Shell.RestartServer();
            OnActivated();
            if (Shell.ServerRunning) Ui.Toast("服务设置已保存并重启");
        }

        /// <summary>修改管理密码（PasswordBox 不支持绑定，由页面代码调用）。成功返回 true。</summary>
        public bool ChangePassword(string oldPassword, string newPassword, string confirm)
        {
            if (!S.Settings.VerifyPassword(oldPassword))
            {
                Ui.Error("原密码不正确");
                return false;
            }
            if ((newPassword ?? "").Length < 4)
            {
                Ui.Error("新密码至少 4 位");
                return false;
            }
            if (newPassword != confirm)
            {
                Ui.Error("两次输入的新密码不一致");
                return false;
            }
            S.Settings.SetPassword(newPassword);
            Ui.Toast("密码已修改");
            return true;
        }

        [RelayCommand]
        private void OpenDataDir()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + S.DataDir + "\"");
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        [RelayCommand]
        private void Backup()
        {
            var dlg = new SaveFileDialog { Filter = "数据库备份|*.db", FileName = $"mdquiz-备份-{DateTime.Now:yyyyMMdd-HHmm}.db" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                S.Db.BackupTo(dlg.FileName);
                Ui.ToastFile("数据库备份完成", dlg.FileName);
            }
            catch (Exception ex)
            {
                Ui.Error("备份失败：" + ex.Message);
            }
        }
    }
}
