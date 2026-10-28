using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Client;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Core.Protocol;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Controls;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Student.ViewModels
{
    public enum Screen
    {
        Connect,
        Login,
        Waiting,
        Exam,
        Result,
    }

    public class StudentSettings
    {
        public string LastHost { get; set; }
        public int LastPort { get; set; }

        private static string PathOf() => Path.Combine(App.DataDir ?? AppPaths.ExeDir, "settings.json");

        public static StudentSettings Load()
        {
            try
            {
                var file = PathOf();
                if (File.Exists(file)) return Json.Deserialize<StudentSettings>(File.ReadAllText(file)) ?? new StudentSettings();
            }
            catch (Exception)
            {
                // 设置损坏时使用默认值
            }
            return new StudentSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(PathOf(), Json.Serialize(this));
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    /// <summary>学生端主界面状态机：连接 → 登录 → 候考 → 答题 → 交卷结果。</summary>
    public partial class ShellViewModel : ObservableObject
    {
        public ShellViewModel()
        {
            Settings = StudentSettings.Load();
            Client = new ExamClient(Path.Combine(App.DataDir ?? AppPaths.ExeDir, "cache"), Environment.MachineName);
            ConnectVm = new ConnectViewModel(this);
            LoginVm = new LoginViewModel(this);
            WaitingVm = new WaitingViewModel(this);
            ExamVm = new ExamViewModel(this);
            ResultVm = new ResultViewModel(this);

            Client.StateChanged += s => Ui.OnUi(() => OnConnectionState(s));
            Client.ExamChanged += e => Ui.OnUi(() => OnExamChanged(e));
            Client.PaperReceived += p => Ui.OnUi(() => OnPaper(p));
            Client.SyncChanged += () => Ui.OnUi(() => ExamVm.UpdateSync());
            Client.Submitted += r => Ui.OnUi(() => OnSubmitted(r));
            Client.SessionLost += reason => Ui.OnUi(() => OnSessionLost(reason));
            GoConnect();
        }

        public ExamClient Client { get; }
        public StudentSettings Settings { get; }
        public ConnectViewModel ConnectVm { get; }
        public LoginViewModel LoginVm { get; }
        public WaitingViewModel WaitingVm { get; }
        public ExamViewModel ExamVm { get; }
        public ResultViewModel ResultVm { get; }

        [ObservableProperty] private object _current;
        [ObservableProperty] private Screen _screen;
        [ObservableProperty] private bool _isReconnecting;

        /// <summary>是否需要全屏锁定（答题中且开启了防作弊）。</summary>
        public bool Lockdown => Screen == Screen.Exam && (Client.Exam?.AntiCheat ?? false) && !Client.IsSubmitted;

        public event Action LockdownChanged;

        partial void OnScreenChanged(Screen value)
        {
            OnPropertyChanged(nameof(Lockdown));
            LockdownChanged?.Invoke();
        }

        public void GoConnect()
        {
            Screen = Screen.Connect;
            Current = ConnectVm;
            ConnectVm.Activate();
        }

        public void GoLogin()
        {
            LoginVm.Prepare();
            Screen = Screen.Login;
            Current = LoginVm;
        }

        public void GoWaiting()
        {
            WaitingVm.Refresh();
            Screen = Screen.Waiting;
            Current = WaitingVm;
        }

        public void GoExam()
        {
            if (Screen == Screen.Exam) return;
            Screen = Screen.Exam;
            Current = ExamVm;
        }

        public void GoResult()
        {
            ExamVm.StopTimers();
            Screen = Screen.Result;
            Current = ResultVm;
        }

        public async Task<bool> ConnectToAsync(string host, int port)
        {
            try
            {
                await Client.ConnectAsync(host, port);
                Settings.LastHost = host;
                Settings.LastPort = port;
                Settings.Save();
                GoLogin();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"连接 {host}:{port} 失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>登录成功后根据考试状态进入对应页面。</summary>
        public void AfterLogin()
        {
            if (Client.IsSubmitted)
            {
                ResultVm.Load(Client.Result);
                GoResult();
            }
            else if (Client.Paper != null && Client.Exam?.Status == ExamStatus.Running)
            {
                GoExam();
            }
            else
            {
                GoWaiting();
            }
        }

        private void OnConnectionState(ConnectionState state)
        {
            IsReconnecting = state == ConnectionState.Reconnecting;
            ExamVm.UpdateSync();
            if (state == ConnectionState.Disconnected && (Screen == Screen.Login || Screen == Screen.Waiting) && !Client.IsLoggedIn)
            {
                Ui.ToastWarning("与教师机的连接已断开");
                GoConnect();
            }
        }

        private long _lastExamId;
        private int _lastDuration;

        private void OnExamChanged(ExamInfo info)
        {
            LoginVm.RefreshExam();
            WaitingVm.Refresh();
            NotifyTimeChange(info);
        }

        /// <summary>考试中老师加减了时间：提示学生（倒计时已自动同步）。</summary>
        private void NotifyTimeChange(ExamInfo info)
        {
            if (info == null) return;
            var changed = info.ExamId == _lastExamId && _lastDuration > 0 && info.DurationMinutes != _lastDuration;
            var delta = info.DurationMinutes - _lastDuration;
            _lastExamId = info.ExamId;
            _lastDuration = info.DurationMinutes;
            if (!changed || info.Status != ExamStatus.Running || Screen != Screen.Exam) return;
            if (delta > 0) Ui.ToastInfo($"老师已将考试时间延长 {delta} 分钟");
            else Ui.ToastWarning($"老师已将考试时间缩短 {-delta} 分钟，请注意剩余时间");
        }

        private void OnPaper(StudentPaper paper)
        {
            if (!Client.IsLoggedIn || Client.IsSubmitted) return;
            MarkdownView.ClearImageCache();
            MarkdownView.ResourceResolver = hash =>
                paper.Resources != null && paper.Resources.TryGetValue(hash, out var b64) ? Convert.FromBase64String(b64) : null;
            ExamVm.Load(paper);
            GoExam();
        }

        private void OnSubmitted(SubmitResult result)
        {
            ResultVm.Load(result);
            GoResult();
        }

        private void OnSessionLost(string reason)
        {
            ExamVm.StopTimers();
            Ui.Info(reason, "已退出考试");
            Client.Disconnect();
            GoConnect();
        }
    }

    // ==================== 连接教师机 ====================

    public partial class ConnectViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;
        private readonly DispatcherTimer _timer;
        private bool _searching;
        private bool _autoConnectTried;

        public ConnectViewModel(ShellViewModel shell)
        {
            _shell = shell;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (s, e) => await SearchAsync();
            var last = shell.Settings.LastHost;
            if (!string.IsNullOrEmpty(last))
                _manualAddress = shell.Settings.LastPort > 0 && shell.Settings.LastPort != Discovery.DefaultTcpPort ? $"{last}:{shell.Settings.LastPort}" : last;
        }

        public ObservableCollection<DiscoveryInfo> Servers { get; } = new ObservableCollection<DiscoveryInfo>();

        [ObservableProperty] private string _manualAddress = "";
        [ObservableProperty] private string _error = "";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _hint = "正在局域网中查找教师机…";

        public void Activate()
        {
            Error = "";
            IsBusy = false;
            _timer.Start();
            _ = SearchAsync();
        }

        private async Task SearchAsync()
        {
            if (_shell.Screen != Screen.Connect)
            {
                _timer.Stop();
                return;
            }
            if (_searching) return;
            _searching = true;
            try
            {
                var found = await Discovery.SearchAsync();
                if (_shell.Screen != Screen.Connect) return;
                Servers.Clear();
                foreach (var s in found.OrderByDescending(f => f.ExamStatus.HasValue).ThenBy(f => f.ServerName)) Servers.Add(s);
                Hint = Servers.Count == 0
                    ? "暂时没有找到教师机。请确认老师已打开教师端，或在下方手动输入地址。"
                    : "找到以下教师机，点击连接：";

                // 只有一台开放了考试的教师机时自动连接（每次启动只尝试一次）
                var open = Servers.Where(s => s.ExamStatus == ExamStatus.Open || s.ExamStatus == ExamStatus.Running).ToList();
                if (!_autoConnectTried && open.Count == 1 && Servers.Count == 1 && !IsBusy)
                {
                    _autoConnectTried = true;
                    await ConnectAsync(open[0]);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("搜索教师机失败：" + ex.Message);
            }
            finally
            {
                _searching = false;
            }
        }

        [RelayCommand]
        private async Task ConnectAsync(DiscoveryInfo server)
        {
            if (server == null || IsBusy) return;
            IsBusy = true;
            Error = "";
            var ok = await _shell.ConnectToAsync(server.Address, server.Port);
            IsBusy = false;
            if (ok) _timer.Stop();
            else Error = $"无法连接到 {server.ServerName}（{server.Address}），请稍后重试";
        }

        [RelayCommand]
        private async Task ConnectManualAsync()
        {
            if (IsBusy) return;
            var text = (ManualAddress ?? "").Trim().Replace('：', ':');
            if (text.Length == 0)
            {
                Error = "请输入教师机的 IP 地址";
                return;
            }
            var host = text;
            var port = Discovery.DefaultTcpPort;
            var colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                host = text.Substring(0, colon);
                if (!int.TryParse(text.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                {
                    Error = "端口格式不正确";
                    return;
                }
            }
            IsBusy = true;
            Error = "";
            var ok = await _shell.ConnectToAsync(host, port);
            IsBusy = false;
            if (ok) _timer.Stop();
            else Error = $"无法连接到 {host}:{port}。请检查地址是否正确，以及教师机是否已打开教师端。";
        }
    }

    // ==================== 登录 ====================

    public partial class LoginViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;

        public LoginViewModel(ShellViewModel shell)
        {
            _shell = shell;
        }

        [ObservableProperty] private string _studentNo = "";
        [ObservableProperty] private bool _confirming;
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _initial = "";
        [ObservableProperty] private string _className = "";
        [ObservableProperty] private string _error = "";
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _serverName = "";
        [ObservableProperty] private bool _hasExam;
        [ObservableProperty] private string _examTitle = "";
        [ObservableProperty] private string _examMeta = "";

        public void Prepare()
        {
            Confirming = false;
            Error = "";
            IsBusy = false;
            ServerName = _shell.Client.Welcome?.ServerName ?? _shell.Client.Host;
            RefreshExam();
        }

        public void RefreshExam()
        {
            var exam = _shell.Client.Exam;
            HasExam = exam != null && exam.Status != ExamStatus.Ended;
            ExamTitle = exam?.Title ?? "";
            ExamMeta = exam == null ? "" : $"{exam.QuestionCount} 题 · 满分 {ScoreConverter.Format(exam.TotalScore)} · {exam.DurationMinutes} 分钟";
        }

        [RelayCommand]
        private async Task NextAsync()
        {
            if (IsBusy) return;
            var no = (StudentNo ?? "").Trim();
            if (no.Length == 0)
            {
                Error = "请输入学号";
                return;
            }
            IsBusy = true;
            Error = "";
            try
            {
                var result = await _shell.Client.IdentifyAsync(no);
                if (!result.Ok)
                {
                    Error = result.Error;
                    return;
                }
                Name = result.Name;
                Initial = string.IsNullOrEmpty(result.Name) ? "?" : result.Name.Substring(0, 1);
                ClassName = result.ClassName;
                Confirming = true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void Back()
        {
            Confirming = false;
            Error = "";
        }

        [RelayCommand]
        private async Task LoginAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Error = "";
            try
            {
                var result = await _shell.Client.LoginAsync(StudentNo);
                if (!result.Ok)
                {
                    Error = result.Error;
                    return;
                }
                _shell.AfterLogin();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void SwitchServer()
        {
            _shell.Client.Disconnect();
            _shell.GoConnect();
        }
    }

    // ==================== 候考 ====================

    public partial class WaitingViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;

        public WaitingViewModel(ShellViewModel shell)
        {
            _shell = shell;
        }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _initial = "";
        [ObservableProperty] private string _detail = "";
        [ObservableProperty] private string _examTitle = "";
        [ObservableProperty] private string _duration = "";
        [ObservableProperty] private string _questionCount = "";
        [ObservableProperty] private string _totalScore = "";
        [ObservableProperty] private bool _antiCheat;
        [ObservableProperty] private string _status = "";

        public void Refresh()
        {
            var login = _shell.Client.Login;
            var exam = _shell.Client.Exam;
            Name = login?.Name ?? "";
            Initial = string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1);
            Detail = login == null ? "" : $"{login.StudentNo} · {login.ClassName}";
            ExamTitle = exam?.Title ?? "";
            Duration = exam?.DurationMinutes.ToString() ?? "-";
            QuestionCount = exam?.QuestionCount.ToString() ?? "-";
            TotalScore = exam == null ? "-" : ScoreConverter.Format(exam.TotalScore);
            AntiCheat = exam?.AntiCheat ?? false;
            Status = exam?.Status == ExamStatus.Running ? "考试已开始，正在加载试卷…" : "等待老师开始考试…";
        }
    }

    // ==================== 交卷结果 ====================

    public partial class ResultViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;

        public ResultViewModel(ShellViewModel shell)
        {
            _shell = shell;
        }

        [ObservableProperty] private string _title = "";
        [ObservableProperty] private string _subtitle = "";
        [ObservableProperty] private bool _showScore;
        [ObservableProperty] private string _score = "";
        [ObservableProperty] private string _scoreTotal = "";
        [ObservableProperty] private string _scoreLabel = "";
        [ObservableProperty] private string _note = "";
        [ObservableProperty] private string _name = "";

        public void Load(SubmitResult r)
        {
            if (r == null) return;
            var login = _shell.Client.Login;
            Name = login == null ? "" : $"{login.Name}（{login.StudentNo}）";
            Title = r.Forced ? "考试时间到，已自动交卷" : "交卷成功";
            var time = r.SubmittedAtTicks > 0 ? new DateTime(r.SubmittedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss") : "";
            Subtitle = string.IsNullOrEmpty(time) ? "你的答卷已提交给老师" : $"你的答卷已于 {time} 提交给老师";
            ShowScore = r.ShowScore;
            if (r.ShowScore)
            {
                if (r.HasPendingManual)
                {
                    Score = ScoreConverter.Format(r.ObjectiveScore);
                    ScoreTotal = "/ " + ScoreConverter.Format(r.ObjectiveTotal);
                    ScoreLabel = "客观题得分";
                    Note = "简答题需要老师批改，最终成绩以老师公布为准。";
                }
                else
                {
                    Score = ScoreConverter.Format(r.TotalScore);
                    ScoreTotal = "/ " + ScoreConverter.Format(_shell.Client.Exam?.TotalScore ?? r.ObjectiveTotal);
                    ScoreLabel = "本次得分";
                    Note = "";
                }
            }
            else
            {
                Note = "成绩将由老师统一公布。";
            }
        }

        [RelayCommand]
        private void Exit() => System.Windows.Application.Current.Shutdown();
    }
}
