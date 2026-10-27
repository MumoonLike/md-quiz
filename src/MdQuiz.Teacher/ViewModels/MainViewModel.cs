using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Net;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;

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

        public void UpdateAddress()
        {
            var port = AppServices.Current.Host.Port;
            var ips = Discovery.LocalIPv4Addresses();
            ServerAddress = ips.Count == 0 ? $"本机:{port}" : string.Join("\n", ips.Take(3).Select(ip => $"{ip}:{port}"));
        }
    }
}
