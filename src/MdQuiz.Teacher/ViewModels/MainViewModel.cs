using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Net;
using MdQuiz.Server.Mcp;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.ViewModels
{
    /// <summary>页面基类：切换到该页面时刷新数据。</summary>
    public abstract class PageViewModel : ObservableObject
    {
        protected static AppServices S => AppServices.Current;

        protected static MainViewModel Shell => MainViewModel.Instance;

        public virtual void OnActivated()
        {
        }
    }

    /// <summary>MCP 调用记录（设置页显示）。</summary>
    public class McpLogItem
    {
        public string Time { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string LevelText { get; set; }
        public Color Color { get; set; }
        public string Client { get; set; }
    }

    public partial class MainViewModel : ObservableObject
    {
        public MainViewModel()
        {
            Instance = this;
            Monitor = new MonitorViewModel();
            Exams = new ExamsViewModel();
            Bank = new QuestionBankViewModel();
            Papers = new PapersViewModel();
            Students = new StudentsViewModel();
            Results = new ResultsViewModel();
            Settings = new SettingsViewModel();
        }

        public static MainViewModel Instance { get; private set; }

        public MonitorViewModel Monitor { get; }
        public ExamsViewModel Exams { get; }
        public QuestionBankViewModel Bank { get; }
        public PapersViewModel Papers { get; }
        public StudentsViewModel Students { get; }
        public ResultsViewModel Results { get; }
        public SettingsViewModel Settings { get; }

        [ObservableProperty]
        private PageViewModel _currentPage;

        [ObservableProperty]
        private string _currentKey;

        [ObservableProperty]
        private bool _serverRunning;

        [ObservableProperty]
        private string _serverStatus = "考试服务未启动";

        [ObservableProperty]
        private string _serverAddress = "";

        [ObservableProperty]
        private bool _mcpRunning;

        [ObservableProperty]
        private string _mcpStatus = "";

        public ObservableCollection<McpLogItem> McpLog { get; } = new ObservableCollection<McpLogItem>();

        [RelayCommand]
        public void Navigate(string key)
        {
            PageViewModel page;
            switch (key)
            {
                case "exams": page = Exams; break;
                case "bank": page = Bank; break;
                case "papers": page = Papers; break;
                case "students": page = Students; break;
                case "results": page = Results; break;
                case "settings": page = Settings; break;
                default:
                    key = "monitor";
                    page = Monitor;
                    break;
            }
            CurrentKey = key;
            CurrentPage = page;
            try
            {
                page.OnActivated();
            }
            catch (Exception ex)
            {
                Log.Error("刷新页面失败：" + key, ex);
                Ui.Error("加载数据失败：" + ex.Message);
            }
        }

        /// <summary>主窗口加载后：启动考试服务、恢复进行中的考试。</summary>
        public void Initialize()
        {
            StartServer();
            AppServices.Current.Mcp.Activity += a => Ui.OnUi(() => OnMcpActivity(a));
            ApplyMcp(showSuccess: false);
            var active = AppServices.Current.Exams.FindActive();
            if (active != null && ServerRunning)
            {
                try
                {
                    AppServices.Current.Host.OpenExam(active.Id);
                    Ui.ToastInfo($"已恢复上次未结束的考试“{active.Title}”");
                }
                catch (Exception ex)
                {
                    Log.Error("恢复考试失败", ex);
                    Ui.Error($"恢复考试“{active.Title}”失败：{ex.Message}");
                }
            }
            Navigate(active != null ? "monitor" : (AppServices.Current.Questions.Count() == 0 ? "bank" : "monitor"));
        }

        public void StartServer()
        {
            var s = AppServices.Current;
            var error = s.StartHost();
            if (error != null)
            {
                ServerRunning = false;
                ServerStatus = "考试服务未启动";
                ServerAddress = "端口被占用，请在设置中更换";
                Ui.Error(error);
                return;
            }
            ServerRunning = true;
            ServerStatus = s.Host.DiscoveryEnabled ? "考试服务运行中" : "运行中（自动发现不可用）";
            UpdateAddress();
        }

        public void RestartServer()
        {
            AppServices.Current.Host.Stop();
            StartServer();
        }

        /// <summary>按设置启动 / 停止 MCP 服务并刷新状态。</summary>
        public bool ApplyMcp(bool showSuccess)
        {
            var s = AppServices.Current;
            var error = s.ApplyMcp();
            UpdateMcpStatus();
            if (error != null)
            {
                Ui.ToastError(error);
                return false;
            }
            if (showSuccess) Ui.Toast(s.Mcp.IsRunning ? "MCP 服务已启动" : "MCP 服务已关闭");
            return true;
        }

        public void UpdateMcpStatus()
        {
            var mcp = AppServices.Current.Mcp;
            McpRunning = mcp.IsRunning;
            if (!mcp.IsRunning)
            {
                McpStatus = AppServices.Current.McpEnabled ? "MCP 服务未启动" : "";
                return;
            }
            var options = mcp.Options;
            McpStatus = options.AllowLan ? $"MCP 服务运行中 · 局域网 :{mcp.Port}" : $"MCP 服务运行中 · 127.0.0.1:{mcp.Port}";
        }

        private void OnMcpActivity(McpActivity a)
        {
            Color color;
            switch (a.Level)
            {
                case McpPermissions.Control: color = Palette.Red; break;
                case McpPermissions.Edit: color = Palette.Amber; break;
                default: color = Palette.Blue; break;
            }
            McpLog.Insert(0, new McpLogItem
            {
                Time = a.Time.ToString("HH:mm:ss"),
                Title = a.Title,
                Message = a.Success ? a.Message : "失败：" + a.Message,
                LevelText = a.Level == McpPermissions.Read ? "查询" : a.Level == McpPermissions.Edit ? "编辑" : "控制",
                Color = a.Success ? color : Palette.Gray,
                Client = a.Client,
            });
            while (McpLog.Count > 100) McpLog.RemoveAt(McpLog.Count - 1);
            Log.Info($"[MCP] {a.Client} {a.Tool} {(a.Success ? "成功" : "失败")}：{a.Message}");
            if (a.Level == McpPermissions.Read || !a.Success) return;

            // AI 助手修改了数据：提示老师，并刷新当前页面（正在编辑的试卷不打断）
            Ui.ToastInfo("AI 助手（MCP）：" + a.Message);
            if (CurrentPage == null || CurrentPage == Monitor || (CurrentPage == Papers && Papers.IsDirty)) return;
            try
            {
                CurrentPage.OnActivated();
            }
            catch (Exception ex)
            {
                Log.Error("MCP 操作后刷新页面失败", ex);
            }
        }

        public void UpdateAddress()
        {
            var port = AppServices.Current.Host.Port;
            var ips = Discovery.LocalIPv4Addresses();
            ServerAddress = ips.Count == 0 ? $"本机:{port}" : string.Join("\n", ips.Take(3).Select(ip => $"{ip}:{port}"));
        }
    }
}
