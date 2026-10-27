using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
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
                Ui.Toast("备份完成");
            }
            catch (Exception ex)
            {
                Ui.Error("备份失败：" + ex.Message);
            }
        }
    }
}
