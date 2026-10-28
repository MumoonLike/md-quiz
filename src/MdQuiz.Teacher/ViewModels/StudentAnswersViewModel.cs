using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Core.Scoring;
using MdQuiz.Server.Hosting;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.ViewModels
{
    /// <summary>答卷窗口左侧的考生。</summary>
    public partial class AnswerStudentRow : ObservableObject
    {
        public AnswerStudentRow(Participant p)
        {
            StudentNo = p.StudentNo;
            Name = p.Name;
            ClassName = p.ClassName;
        }

        public string StudentNo { get; }
        public string Name { get; }
        public string ClassName { get; }

        [ObservableProperty] private string _statusText = "";
        [ObservableProperty] private Color _statusColor;
        [ObservableProperty] private string _detail = "";
        public Participant Record { get; private set; }
        public ParticipantView Live { get; private set; }

        public void Update(Participant p, ParticipantView live, int answered, int questionCount)
        {
            Record = p;
            Live = live;
            var status = live?.Status ?? p.Status;
            var offline = live != null && !live.Online && (status == ParticipantStatus.Waiting || status == ParticipantStatus.Answering);
            switch (status)
            {
                case ParticipantStatus.Submitted:
                    StatusText = p.Forced || (live?.Forced ?? false) ? "已收卷" : "已交卷";
                    StatusColor = Palette.Emerald;
                    break;
                case ParticipantStatus.Answering:
                    StatusText = offline ? "已断开" : "答题中";
                    StatusColor = offline ? Palette.Red : Palette.Blue;
                    break;
                case ParticipantStatus.Waiting:
                    StatusText = offline ? "已断开" : "已登录";
                    StatusColor = offline ? Palette.Red : Palette.Sky;
                    break;
                default:
                    StatusText = "未登录";
                    StatusColor = Palette.Gray;
                    break;
            }
            Detail = status == ParticipantStatus.Submitted && p.TotalScore.HasValue
                ? $"总分 {ScoreConverter.Format(p.TotalScore)}" + (p.PendingManual > 0 ? " · 待批改" : "")
                : $"已答 {answered}/{questionCount}";
        }
    }

    public partial class AnswerOptionRow : ObservableObject
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Text { get; set; }
        public bool IsCorrect { get; set; }

        [ObservableProperty] private bool _isChosen;
    }

    public partial class AnswerBlankRow : ObservableObject
    {
        public string Label { get; set; }
        public string Accepted { get; set; }
        public string Blank { get; set; }

        [ObservableProperty] private string _text = "";
        [ObservableProperty] private bool _isEmpty = true;
        [ObservableProperty] private bool _isCorrect;
    }

    /// <summary>答卷中的一道题。切换考生时只更新作答相关的属性，滚动位置和控件保持不变。</summary>
    public partial class AnswerCard : ObservableObject
    {
        public AnswerCard(ExamQuestion q, int number)
        {
            Question = q;
            Number = number;
            if (q.Type == QuestionType.TrueFalse)
            {
                Options.Add(new AnswerOptionRow { Key = "T", Label = "√", Text = "正确", IsCorrect = q.Answer == "T" });
                Options.Add(new AnswerOptionRow { Key = "F", Label = "×", Text = "错误", IsCorrect = q.Answer == "F" });
            }
            else if (QuestionTypes.HasOptions(q.Type))
            {
                var correct = q.CorrectKeys();
                for (var i = 0; i < q.Options.Count; i++)
                {
                    var key = Core.Models.Question.OptionKey(i);
                    Options.Add(new AnswerOptionRow { Key = key, Label = key, Text = q.Options[i], IsCorrect = correct.Contains(key) });
                }
            }
            for (var i = 0; i < q.Blanks.Count && q.Type == QuestionType.FillBlank; i++)
                Blanks.Add(new AnswerBlankRow { Label = $"第 {i + 1} 空", Blank = q.Blanks[i], Accepted = string.Join(" / ", Core.Models.Question.BlankAlternatives(q.Blanks[i])) });
        }

        public ExamQuestion Question { get; }
        public int Number { get; }
        public string Stem => Question.Stem;
        public string TypeName => QuestionTypes.ShortName(Question.Type);
        public Color TypeColor => QuestionTypeColorConverter.ColorOf(Question.Type);
        public string MaxText => ScoreConverter.Format(Question.Score) + " 分";
        public bool IsChoice => Options.Count > 0;
        public bool IsTrueFalse => Question.Type == QuestionType.TrueFalse;
        public bool IsFill => Question.Type == QuestionType.FillBlank;
        public bool IsShort => Question.Type == QuestionType.ShortAnswer;
        public string Reference => IsShort ? Question.Answer : "";
        public string Analysis => Question.Analysis ?? "";
        public List<AnswerOptionRow> Options { get; } = new List<AnswerOptionRow>();
        public List<AnswerBlankRow> Blanks { get; } = new List<AnswerBlankRow>();

        [ObservableProperty] private bool _answered;
        [ObservableProperty] private string _shortText = "";
        [ObservableProperty] private AnswerVerdict _verdict;
        [ObservableProperty] private string _verdictText = "";
        [ObservableProperty] private Color _verdictColor;
        [ObservableProperty] private string _scoreText = "";

        public void Apply(AnswerSheetItem item)
        {
            var values = item.Values ?? new List<string>();
            Answered = item.Answered;
            foreach (var o in Options) o.IsChosen = values.Any(v => string.Equals((v ?? "").Trim(), o.Key, StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < Blanks.Count; i++)
            {
                var text = i < values.Count ? values[i] ?? "" : "";
                Blanks[i].Text = text;
                Blanks[i].IsEmpty = string.IsNullOrWhiteSpace(text);
                Blanks[i].IsCorrect = Grader.BlankMatches(Blanks[i].Blank, text);
            }
            ShortText = IsShort ? values.FirstOrDefault() ?? "" : "";
            Verdict = item.Verdict;
            switch (item.Verdict)
            {
                case AnswerVerdict.Correct: VerdictText = "正确"; VerdictColor = Palette.Green; break;
                case AnswerVerdict.Partial: VerdictText = "部分正确"; VerdictColor = Palette.Amber; break;
                case AnswerVerdict.Wrong: VerdictText = "错误"; VerdictColor = Palette.Red; break;
                case AnswerVerdict.Pending: VerdictText = "待批改"; VerdictColor = Palette.Violet; break;
                case AnswerVerdict.Graded: VerdictText = "已批改"; VerdictColor = Palette.Blue; break;
                default: VerdictText = "未作答"; VerdictColor = Palette.Gray; break;
            }
            var max = ScoreConverter.Format(Question.Score);
            ScoreText = item.Score.HasValue
                ? $"{(item.Provisional ? "预判 " : item.Manual ? "人工 " : "")}{ScoreConverter.Format(item.Score)} / {max} 分"
                : $"— / {max} 分";
        }
    }

    /// <summary>
    /// 查看考生答卷：左侧考生列表（可搜索、上下切换），右侧逐题显示作答、正确答案和得分。
    /// 考试进行中每 3 秒自动刷新。
    /// </summary>
    public partial class StudentAnswersViewModel : ObservableObject
    {
        private readonly Dictionary<string, AnswerStudentRow> _byNo = new Dictionary<string, AnswerStudentRow>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _timer;

        public StudentAnswersViewModel(Exam exam, IList<string> order)
        {
            Exam = exam;
            Title = exam.Title;
            Cards = exam.Questions.Select((q, i) => new AnswerCard(q, i + 1)).ToList();
            CardsView = CollectionViewSource.GetDefaultView(Cards);
            CardsView.Filter = o => MatchesFilter((AnswerCard)o);
            StudentsView = CollectionViewSource.GetDefaultView(Students);
            StudentsView.Filter = o => MatchesSearch((AnswerStudentRow)o);

            var participants = S.Exams.ListParticipants(exam.Id);
            var rank = (order ?? new List<string>()).Select((no, i) => new { no, i })
                .GroupBy(x => x.no, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
            foreach (var p in participants.OrderBy(p => rank.TryGetValue(p.StudentNo, out var r) ? r : int.MaxValue).ThenBy(p => p.ClassName).ThenBy(p => p.StudentNo))
            {
                var row = new AnswerStudentRow(p);
                _byNo[p.StudentNo] = row;
                Students.Add(row);
            }
            RefreshStudents();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (s, e) =>
            {
                if (!IsLive) return;
                RefreshStudents();
                LoadAnswers();
            };
            _timer.Start();
        }

        private static AppServices S => AppServices.Current;

        public Exam Exam { get; }
        public string Title { get; }
        public ObservableCollection<AnswerStudentRow> Students { get; } = new ObservableCollection<AnswerStudentRow>();
        public ICollectionView StudentsView { get; }
        public List<AnswerCard> Cards { get; }
        public ICollectionView CardsView { get; }

        [ObservableProperty] private AnswerStudentRow _selected;
        [ObservableProperty] private string _search = "";
        [ObservableProperty] private string _filter = "all";
        [ObservableProperty] private string _studentName = "";
        [ObservableProperty] private string _studentInfo = "";
        [ObservableProperty] private string _statusText = "";
        [ObservableProperty] private Color _statusColor;
        [ObservableProperty] private string _answeredText = "";
        [ObservableProperty] private string _scoreText = "";
        [ObservableProperty] private string _scoreLabel = "";
        [ObservableProperty] private int _focusLost;
        [ObservableProperty] private int _wrongCount;
        [ObservableProperty] private int _blankCount;
        [ObservableProperty] private string _hint = "";
        [ObservableProperty] private string _position = "";
        [ObservableProperty] private bool _hasSelection;

        /// <summary>这场考试正在监考中（答案和状态会实时变化）。</summary>
        public bool IsLive
        {
            get
            {
                var current = S.Host.CurrentExam;
                return current != null && current.Id == Exam.Id && current.Status != ExamStatus.Ended;
            }
        }

        partial void OnSelectedChanged(AnswerStudentRow value)
        {
            LoadAnswers();
            ScrollToTopRequested?.Invoke();
        }

        partial void OnSearchChanged(string value) => StudentsView.Refresh();

        partial void OnFilterChanged(string value) => CardsView.Refresh();

        /// <summary>切换考生后让界面滚动到第一题。</summary>
        public event Action ScrollToTopRequested;

        public void Select(string studentNo)
        {
            if (studentNo != null && _byNo.TryGetValue(studentNo, out var row)) Selected = row;
            else Selected = Selected ?? Students.FirstOrDefault();
        }

        public void Stop() => _timer.Stop();

        private bool MatchesSearch(AnswerStudentRow r)
        {
            var k = (Search ?? "").Trim();
            return k.Length == 0 || r.StudentNo.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 || r.Name.Contains(k);
        }

        private bool MatchesFilter(AnswerCard c)
        {
            switch (Filter)
            {
                case "wrong": return c.Verdict == AnswerVerdict.Wrong || c.Verdict == AnswerVerdict.Partial;
                case "blank": return c.Verdict == AnswerVerdict.Unanswered;
                case "pending": return c.Verdict == AnswerVerdict.Pending;
                default: return true;
            }
        }

        private void RefreshStudents()
        {
            var participants = S.Exams.ListParticipants(Exam.Id).ToDictionary(p => p.StudentNo, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ParticipantView> live = null;
            Dictionary<string, int> answered;
            if (IsLive)
            {
                live = S.Host.GetParticipants().ToDictionary(v => v.StudentNo, StringComparer.OrdinalIgnoreCase);
                answered = live.ToDictionary(kv => kv.Key, kv => kv.Value.Answered, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                answered = S.Exams.ListAllAnswers(Exam.Id).Where(a => !AnswerValues.IsEmpty(a.Values))
                    .GroupBy(a => a.StudentNo, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            }
            foreach (var row in Students)
            {
                if (!participants.TryGetValue(row.StudentNo, out var p)) continue;
                ParticipantView v = null;
                live?.TryGetValue(row.StudentNo, out v);
                answered.TryGetValue(row.StudentNo, out var count);
                row.Update(p, v, count, Exam.Questions.Count);
            }
        }

        private void LoadAnswers()
        {
            var row = Selected;
            HasSelection = row != null;
            if (row == null) return;
            var sheet = S.Results.BuildAnswerSheet(Exam, row.StudentNo);
            for (var i = 0; i < Cards.Count && i < sheet.Count; i++) Cards[i].Apply(sheet[i]);
            CardsView.Refresh();

            var p = row.Record;
            StudentName = row.Name;
            var parts = new List<string> { row.StudentNo };
            if (!string.IsNullOrEmpty(row.ClassName)) parts.Add(row.ClassName);
            var machine = row.Live?.Machine ?? p?.Machine;
            var ip = row.Live?.Ip ?? p?.Ip;
            if (!string.IsNullOrEmpty(machine)) parts.Add(machine);
            if (!string.IsNullOrEmpty(ip)) parts.Add(ip);
            if (p?.LoginAt != null) parts.Add($"{p.LoginAt.Value.ToLocalTime():HH:mm} 登录");
            if (p?.SubmittedAt != null) parts.Add($"{p.SubmittedAt.Value.ToLocalTime():HH:mm} 交卷");
            StudentInfo = string.Join(" · ", parts);
            StatusText = row.StatusText;
            StatusColor = row.StatusColor;
            FocusLost = row.Live?.FocusLost ?? p?.FocusLost ?? 0;

            var answeredCount = sheet.Count(s => s.Answered);
            AnsweredText = $"{answeredCount} / {sheet.Count}";
            WrongCount = sheet.Count(s => s.Verdict == AnswerVerdict.Wrong || s.Verdict == AnswerVerdict.Partial);
            BlankCount = sheet.Count(s => s.Verdict == AnswerVerdict.Unanswered);
            var submitted = p?.Status == ParticipantStatus.Submitted;
            if (submitted && p.TotalScore.HasValue)
            {
                ScoreText = ScoreConverter.Format(p.TotalScore);
                ScoreLabel = p.PendingManual > 0 ? $"总分（{p.PendingManual} 题待批改）" : "总分";
            }
            else
            {
                ScoreText = ScoreConverter.Format(sheet.Where(s => QuestionTypes.IsAutoGraded(s.Question.Type)).Sum(s => s.Score ?? 0));
                ScoreLabel = "客观题预判得分";
            }
            Hint = submitted
                ? (IsLive ? "已交卷 · 考试进行中，列表每 3 秒自动刷新" : "")
                : IsLive ? "考试进行中，答案每 3 秒自动刷新；客观题得分按当前答案预判，交卷后以正式判分为准" : "";
            var visible = StudentsView.Cast<AnswerStudentRow>().ToList();
            var index = visible.IndexOf(row);
            Position = index >= 0 ? $"{index + 1} / {visible.Count}" : "";
        }

        [RelayCommand]
        private void Previous() => Move(-1);

        [RelayCommand]
        private void Next() => Move(1);

        private void Move(int delta)
        {
            var visible = StudentsView.Cast<AnswerStudentRow>().ToList();
            if (visible.Count == 0) return;
            var index = Selected == null ? -1 : visible.IndexOf(Selected);
            var target = index < 0 ? 0 : Math.Max(0, Math.Min(visible.Count - 1, index + delta));
            Selected = visible[target];
        }

        [RelayCommand]
        private void Refresh()
        {
            RefreshStudents();
            LoadAnswers();
        }

        [RelayCommand]
        private void SetFilter(string filter) => Filter = filter ?? "all";
    }
}
