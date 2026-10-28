using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.ViewModels
{
    public partial class ParticipantItem : ObservableObject
    {
        public ParticipantItem(ParticipantView v)
        {
            StudentNo = v.StudentNo;
            Update(v);
        }

        public string StudentNo { get; }

        [ObservableProperty] private string _name;
        [ObservableProperty] private string _className;
        [ObservableProperty] private ParticipantStatus _status;
        [ObservableProperty] private bool _online;
        [ObservableProperty] private int _answered;
        [ObservableProperty] private int _questionCount;
        [ObservableProperty] private int _focusLost;
        [ObservableProperty] private string _machine;
        [ObservableProperty] private string _ip;
        [ObservableProperty] private string _statusText;
        [ObservableProperty] private Color _statusColor;
        [ObservableProperty] private double _progress;
        [ObservableProperty] private string _detail;
        [ObservableProperty] private bool _isOffline;

        public void Update(ParticipantView v)
        {
            Name = v.Name;
            ClassName = v.ClassName;
            Status = v.Status;
            Online = v.Online;
            Answered = v.Answered;
            QuestionCount = v.QuestionCount;
            FocusLost = v.FocusLost;
            Machine = v.Machine;
            Ip = v.Ip;
            Progress = v.QuestionCount == 0 ? 0 : 100.0 * v.Answered / v.QuestionCount;
            IsOffline = !v.Online && (v.Status == ParticipantStatus.Waiting || v.Status == ParticipantStatus.Answering);

            if (v.Status == ParticipantStatus.Submitted)
            {
                StatusText = v.Forced ? "已收卷" : "已交卷";
                StatusColor = Palette.Emerald;
            }
            else if (IsOffline)
            {
                StatusText = "已断开";
                StatusColor = Palette.Red;
            }
            else if (v.Status == ParticipantStatus.Answering)
            {
                StatusText = "答题中";
                StatusColor = Palette.Blue;
            }
            else if (v.Status == ParticipantStatus.Waiting)
            {
                StatusText = "已登录";
                StatusColor = Palette.Sky;
            }
            else
            {
                StatusText = "未登录";
                StatusColor = Palette.Gray;
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(v.Machine)) parts.Add(v.Machine);
            if (!string.IsNullOrEmpty(v.Ip)) parts.Add(v.Ip);
            if (v.Status == ParticipantStatus.Submitted && v.ObjectiveScore.HasValue) parts.Add("客观题 " + ScoreConverter.Format(v.ObjectiveScore) + " 分");
            Detail = string.Join(" · ", parts);
        }
    }

    public class LogItem
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public Color Color { get; set; }
    }

    public partial class MonitorViewModel : PageViewModel
    {
        private readonly Dictionary<string, ParticipantItem> _byNo = new Dictionary<string, ParticipantItem>();
        private readonly DispatcherTimer _timer;

        public MonitorViewModel()
        {
            ParticipantsView = CollectionViewSource.GetDefaultView(Participants);
            ParticipantsView.Filter = o => Matches((ParticipantItem)o);
            var host = S.Host;
            host.ExamStateChanged += () => Ui.OnUi(Reload);
            host.ParticipantsReset += () => Ui.OnUi(ReloadParticipants);
            host.ParticipantChanged += v => Ui.OnUi(() => ApplyParticipant(v));
            host.Log += e => Ui.OnUi(() => AddLog(e));
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdateClock();
            _timer.Start();
        }

        public ObservableCollection<ParticipantItem> Participants { get; } = new ObservableCollection<ParticipantItem>();
        public ICollectionView ParticipantsView { get; }
        public ObservableCollection<LogItem> Logs { get; } = new ObservableCollection<LogItem>();
        public ObservableCollection<ExamSummary> ReadyExams { get; } = new ObservableCollection<ExamSummary>();

        [ObservableProperty] private bool _hasExam;
        [ObservableProperty] private string _title;
        [ObservableProperty] private string _subtitle;
        [ObservableProperty] private ExamStatus _status;
        [ObservableProperty] private string _clock = "";
        [ObservableProperty] private string _clockLabel = "";
        [ObservableProperty] private bool _clockWarning;
        [ObservableProperty] private int _total;
        [ObservableProperty] private int _loggedIn;
        [ObservableProperty] private int _answering;
        [ObservableProperty] private int _submitted;
        [ObservableProperty] private int _offline;
        [ObservableProperty] private int _alerts;
        [ObservableProperty] private string _filter = "all";
        [ObservableProperty] private bool _isOpen;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private bool _isEnded;

        partial void OnFilterChanged(string value) => ParticipantsView.Refresh();

        public override void OnActivated() => Reload();

        private bool Matches(ParticipantItem p)
        {
            switch (Filter)
            {
                case "absent": return p.Status == ParticipantStatus.Absent;
                case "answering": return (p.Status == ParticipantStatus.Answering || p.Status == ParticipantStatus.Waiting) && !p.IsOffline;
                case "submitted": return p.Status == ParticipantStatus.Submitted;
                case "offline": return p.IsOffline;
                case "alerts": return p.FocusLost > 0;
                default: return true;
            }
        }

        public void Reload()
        {
            var exam = S.Host.CurrentExam;
            HasExam = exam != null;
            if (exam != null)
            {
                Title = exam.Title;
                Status = exam.Status;
                Subtitle = $"试卷：{exam.PaperName} · {exam.Questions.Count} 题 · 满分 {ScoreConverter.Format(exam.TotalScore)} · 时长 {exam.TotalMinutes} 分钟"
                           + (exam.Settings.AntiCheat ? " · 防作弊" : "");
                IsOpen = exam.Status == ExamStatus.Open;
                IsRunning = exam.Status == ExamStatus.Running;
                IsEnded = exam.Status == ExamStatus.Ended;
            }
            else
            {
                IsOpen = IsRunning = IsEnded = false;
                ReadyExams.Clear();
                foreach (var e in S.Exams.List().Where(x => x.Status == ExamStatus.Draft)) ReadyExams.Add(e);
            }
            ReloadParticipants();
            UpdateClock();
        }

        private void ReloadParticipants()
        {
            Participants.Clear();
            _byNo.Clear();
            foreach (var v in S.Host.GetParticipants())
            {
                var item = new ParticipantItem(v);
                _byNo[v.StudentNo] = item;
                Participants.Add(item);
            }
            UpdateCounts();
        }

        private void ApplyParticipant(ParticipantView v)
        {
            if (_byNo.TryGetValue(v.StudentNo, out var item))
            {
                item.Update(v);
                ParticipantsView.Refresh();
            }
            else
            {
                item = new ParticipantItem(v);
                _byNo[v.StudentNo] = item;
                Participants.Add(item);
            }
            UpdateCounts();
        }

        private void UpdateCounts()
        {
            Total = Participants.Count;
            LoggedIn = Participants.Count(p => p.Status != ParticipantStatus.Absent);
            Answering = Participants.Count(p => p.Status == ParticipantStatus.Answering && !p.IsOffline);
            Submitted = Participants.Count(p => p.Status == ParticipantStatus.Submitted);
            Offline = Participants.Count(p => p.IsOffline);
            Alerts = Participants.Count(p => p.FocusLost > 0);
        }

        private void AddLog(HostLogEntry e)
        {
            Color color;
            switch (e.Level)
            {
                case HostLogLevel.Warning: color = Palette.Amber; break;
                case HostLogLevel.Success: color = Palette.Emerald; break;
                default: color = Palette.Blue; break;
            }
            Logs.Insert(0, new LogItem { Time = e.Time.ToString("HH:mm:ss"), Message = e.Message, Color = color });
            while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
            Log.Info("[考试] " + e.Message);
        }

        private void UpdateClock()
        {
            var exam = S.Host.CurrentExam;
            ClockWarning = false;
            if (exam == null)
            {
                Clock = "";
                ClockLabel = "";
                return;
            }
            switch (exam.Status)
            {
                case ExamStatus.Open:
                    Clock = $"{exam.TotalMinutes}:00";
                    ClockLabel = "等待开考";
                    break;
                case ExamStatus.Running:
                    var left = (exam.DeadlineUtc ?? DateTime.UtcNow) - DateTime.UtcNow;
                    if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                    Clock = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
                    ClockLabel = exam.DeadlineUtc.HasValue ? $"剩余时间 · {exam.DeadlineUtc.Value.ToLocalTime():HH:mm} 结束" : "剩余时间";
                    ClockWarning = left.TotalMinutes < 5;
                    break;
                default:
                    Clock = "00:00";
                    ClockLabel = exam.EndedAt.HasValue ? $"考试已结束 · {exam.EndedAt.Value.ToLocalTime():HH:mm}" : "考试已结束";
                    break;
            }
        }

        [RelayCommand]
        private void Start()
        {
            var exam = S.Host.CurrentExam;
            if (exam == null) return;
            var waiting = Participants.Count(p => p.Status == ParticipantStatus.Waiting);
            if (!Ui.Confirm($"现在开始考试“{exam.Title}”吗？\n\n已登录 {waiting}/{Participants.Count} 人。开始后将向学生下发试卷并开始 {exam.TotalMinutes} 分钟倒计时，迟到的学生登录后可直接答题。"))
                return;
            S.Host.StartExam();
            Ui.Toast("考试已开始");
        }

        /// <summary>快捷加减时间：参数为分钟数，负数表示缩短。</summary>
        [RelayCommand]
        private void AdjustTime(string minutes)
        {
            if (!int.TryParse(minutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) || m == 0) return;
            ApplyTimeChange(m, confirm: m < 0);
        }

        [RelayCommand]
        private void CustomTime()
        {
            var dialog = new TimeAdjustWindow();
            if (Ui.ShowDialog(dialog) != true) return;
            ApplyTimeChange(dialog.Minutes, confirm: false);
        }

        private void ApplyTimeChange(int minutes, bool confirm)
        {
            var exam = S.Host.CurrentExam;
            if (exam?.DeadlineUtc == null) return;
            var newEnd = exam.DeadlineUtc.Value.AddMinutes(minutes).ToLocalTime();
            if (confirm && !Ui.ConfirmDanger($"确定将考试时间缩短 {-minutes} 分钟吗？\n\n调整后 {newEnd:HH:mm} 结束，学生端倒计时会立即同步。", "缩短考试时间"))
                return;
            try
            {
                var deadline = S.Host.AdjustTime(minutes);
                Ui.Toast($"已{(minutes > 0 ? "延长" : "缩短")} {Math.Abs(minutes)} 分钟，{deadline.ToLocalTime():HH:mm} 结束");
            }
            catch (InvalidOperationException ex)
            {
                Ui.Error(ex.Message, "无法调整时间");
            }
        }

        [RelayCommand]
        private void End()
        {
            var notSubmitted = Participants.Count(p => p.Status == ParticipantStatus.Answering || p.Status == ParticipantStatus.Waiting);
            var message = notSubmitted > 0
                ? $"还有 {notSubmitted} 名学生没有交卷，结束考试将自动收卷（以他们已保存的答案为准）。确定结束吗？"
                : "确定结束考试吗？";
            if (!Ui.ConfirmDanger(message, "结束考试")) return;
            S.Host.EndExam();
        }

        [RelayCommand]
        private void Close()
        {
            var exam = S.Host.CurrentExam;
            if (exam == null) return;
            if (exam.Status == ExamStatus.Open && !Ui.Confirm("取消后已登录的学生会退出，考试回到“未开始”状态，可以之后重新开放。确定取消吗？", "取消开放"))
                return;
            S.Host.CloseExam();
            Reload();
        }

        [RelayCommand]
        private void OpenExam(ExamSummary exam)
        {
            if (exam == null) return;
            S.Host.OpenExam(exam.Id);
            Ui.Toast($"“{exam.Title}”已开放登录");
        }

        [RelayCommand]
        private void CreateExam()
        {
            Shell.Navigate("exams");
            Shell.Exams.CreateCommand.Execute(null);
        }

        [RelayCommand]
        private void Release(ParticipantItem p)
        {
            if (p == null) return;
            if (!Ui.Confirm($"允许 {p.Name}（{p.StudentNo}）换到其他电脑登录吗？\n\n当前电脑会退出，已保存的答案会保留。")) return;
            S.Host.ReleaseStudent(p.StudentNo);
        }

        [RelayCommand]
        private void SetFilter(string filter) => Filter = filter ?? "all";

        [RelayCommand]
        private void ViewResults()
        {
            var exam = S.Host.CurrentExam;
            if (exam == null) return;
            Shell.Navigate("results");
            Shell.Results.Select(exam.Id);
        }
    }
}
