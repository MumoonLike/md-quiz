using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Client;
using MdQuiz.Core.Models;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Student.ViewModels
{
    public partial class OptionVm : ObservableObject
    {
        private readonly QuestionVm _owner;

        public OptionVm(QuestionVm owner, string key, string label, string text)
        {
            _owner = owner;
            Key = key;
            Label = label;
            Text = text;
        }

        /// <summary>原始选项字母（作答按它记录）。</summary>
        public string Key { get; }
        /// <summary>显示的字母（按显示顺序 A、B、C…）。</summary>
        public string Label { get; }
        public string Text { get; }
        public bool IsMultiple => _owner.IsMultiple;

        [ObservableProperty] private bool _isSelected;

        [RelayCommand]
        private void Toggle() => _owner.Choose(this);
    }

    public partial class BlankVm : ObservableObject
    {
        private readonly QuestionVm _owner;

        public BlankVm(QuestionVm owner, int index)
        {
            _owner = owner;
            Label = $"第 {index} 空";
        }

        public string Label { get; }

        [ObservableProperty] private string _text = "";

        partial void OnTextChanged(string value) => _owner.OnTextChanged();
    }

    public partial class QuestionVm : ObservableObject
    {
        private readonly Action<QuestionVm, bool> _changed;
        private bool _loading;

        public QuestionVm(StudentQuestion q, Action<QuestionVm, bool> changed)
        {
            Question = q;
            _changed = changed;
            if (q.Type == QuestionType.TrueFalse)
            {
                // 判断题也用“选项”表示，每道题生成独立的单选按钮，避免切换题目时复用控件串题
                Options.Add(new OptionVm(this, "T", "√", "正确"));
                Options.Add(new OptionVm(this, "F", "×", "错误"));
            }
            else
            {
                for (var i = 0; i < q.Options.Count; i++)
                    Options.Add(new OptionVm(this, q.Options[i].Key, MdQuiz.Core.Models.Question.OptionKey(i), q.Options[i].Text));
            }
            for (var i = 0; i < q.BlankCount; i++) Blanks.Add(new BlankVm(this, i + 1));
        }

        public StudentQuestion Question { get; }
        public long Id => Question.Id;
        public int Number => Question.Number;
        public QuestionType Type => Question.Type;
        public string Stem => Question.Stem;
        public string TypeName => QuestionTypes.DisplayName(Type);
        public Color TypeColor => QuestionTypeColorConverter.ColorOf(Type);
        public string ScoreText => ScoreConverter.Format(Question.Score) + " 分";
        public string Hint
        {
            get
            {
                switch (Type)
                {
                    case QuestionType.Multiple: return "多选题：请选择所有正确的选项";
                    case QuestionType.FillBlank: return "在下方依次填写每个空的答案";
                    case QuestionType.ShortAnswer: return "在下方输入你的答案";
                    default: return "";
                }
            }
        }

        public bool IsSingle => Type == QuestionType.Single;
        public bool IsMultiple => Type == QuestionType.Multiple;
        public bool IsChoice => Type == QuestionType.Single || Type == QuestionType.Multiple;
        public bool IsTrueFalse => Type == QuestionType.TrueFalse;
        public bool IsFill => Type == QuestionType.FillBlank;
        public bool IsShort => Type == QuestionType.ShortAnswer;

        public ObservableCollection<OptionVm> Options { get; } = new ObservableCollection<OptionVm>();
        public ObservableCollection<BlankVm> Blanks { get; } = new ObservableCollection<BlankVm>();

        [ObservableProperty] private string _shortText = "";
        [ObservableProperty] private bool _isAnswered;
        [ObservableProperty] private bool _isFlagged;
        [ObservableProperty] private bool _isCurrent;
        [ObservableProperty] private int _charCount;

        partial void OnShortTextChanged(string value)
        {
            CharCount = (value ?? "").Length;
            OnTextChanged();
        }

        /// <summary>点击选项：多选题切换该选项；单选、判断题选中该选项并取消其他选项（再次点击已选项保持选中）。</summary>
        internal void Choose(OptionVm option)
        {
            if (option == null || !Options.Contains(option)) return;
            if (Type == QuestionType.Multiple)
            {
                option.IsSelected = !option.IsSelected;
            }
            else
            {
                if (option.IsSelected) return;
                foreach (var o in Options) o.IsSelected = o == option;
            }
            Changed(false);
        }

        internal void OnTextChanged()
        {
            if (!_loading) Changed(true);
        }

        private void Changed(bool isText)
        {
            IsAnswered = !AnswerValues.IsEmpty(GetValues());
            _changed?.Invoke(this, isText);
        }

        public List<string> GetValues()
        {
            switch (Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                case QuestionType.TrueFalse:
                    return Options.Where(o => o.IsSelected).Select(o => o.Key).OrderBy(k => k).ToList();
                case QuestionType.FillBlank:
                    return Blanks.Select(b => b.Text ?? "").ToList();
                default:
                    return string.IsNullOrEmpty(ShortText) ? new List<string>() : new List<string> { ShortText };
            }
        }

        public void Load(IList<string> values)
        {
            _loading = true;
            try
            {
                values = values ?? new List<string>();
                switch (Type)
                {
                    case QuestionType.Single:
                    case QuestionType.Multiple:
                    case QuestionType.TrueFalse:
                        foreach (var o in Options) o.IsSelected = values.Contains(o.Key);
                        break;
                    case QuestionType.FillBlank:
                        for (var i = 0; i < Blanks.Count; i++) Blanks[i].Text = i < values.Count ? values[i] : "";
                        break;
                    default:
                        ShortText = values.FirstOrDefault() ?? "";
                        break;
                }
                IsAnswered = !AnswerValues.IsEmpty(GetValues());
            }
            finally
            {
                _loading = false;
            }
        }
    }

    public class SectionVm
    {
        public string Title { get; set; }
        public List<QuestionVm> Questions { get; set; }
    }

    public partial class ExamViewModel : ObservableObject
    {
        private static readonly string[] ChineseNumbers = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        private readonly ShellViewModel _shell;
        private readonly DispatcherTimer _clock;
        private readonly DispatcherTimer _textFlush;
        private readonly HashSet<QuestionVm> _dirtyText = new HashSet<QuestionVm>();
        private bool _warned5;
        private bool _warned1;

        public ExamViewModel(ShellViewModel shell)
        {
            _shell = shell;
            _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _clock.Tick += (s, e) => UpdateClock();
            _textFlush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _textFlush.Tick += (s, e) => FlushText();
        }

        private ExamClient Client => _shell.Client;

        public ObservableCollection<QuestionVm> Questions { get; } = new ObservableCollection<QuestionVm>();
        public ObservableCollection<SectionVm> Sections { get; } = new ObservableCollection<SectionVm>();

        [ObservableProperty] private QuestionVm _current;
        [ObservableProperty] private string _title = "";
        [ObservableProperty] private string _studentText = "";
        [ObservableProperty] private string _clockText = "--:--";
        [ObservableProperty] private bool _clockWarning;
        [ObservableProperty] private int _answeredCount;
        [ObservableProperty] private string _progressText = "";
        [ObservableProperty] private double _progress;
        [ObservableProperty] private string _syncText = "";
        [ObservableProperty] private int _syncState; // 0 已保存，1 同步中，2 离线
        [ObservableProperty] private bool _isFirst;
        [ObservableProperty] private bool _isLast;
        [ObservableProperty] private bool _isSubmitting;
        [ObservableProperty] private bool _timeUp;

        partial void OnCurrentChanged(QuestionVm oldValue, QuestionVm newValue)
        {
            FlushText();
            if (oldValue != null) oldValue.IsCurrent = false;
            if (newValue != null) newValue.IsCurrent = true;
            var index = newValue == null ? -1 : Questions.IndexOf(newValue);
            IsFirst = index <= 0;
            IsLast = index == Questions.Count - 1;
        }

        public void Load(StudentPaper paper)
        {
            var keepId = Current?.Id;
            Questions.Clear();
            Sections.Clear();
            _dirtyText.Clear();
            var answers = Client.AnswersSnapshot();
            foreach (var q in paper.Questions)
            {
                var vm = new QuestionVm(q, OnAnswerChanged);
                if (answers.TryGetValue(q.Id, out var a)) vm.Load(a.Values);
                Questions.Add(vm);
            }
            // 按连续的同题型分组：一、单选题  二、多选题 …
            var groupIndex = 0;
            var i = 0;
            while (i < Questions.Count)
            {
                var type = Questions[i].Type;
                var group = Questions.Skip(i).TakeWhile(q => q.Type == type).ToList();
                var number = groupIndex < ChineseNumbers.Length ? ChineseNumbers[groupIndex] : (groupIndex + 1).ToString();
                Sections.Add(new SectionVm
                {
                    Title = $"{number}、{QuestionTypes.DisplayName(type)}（{group.Count} 题，共 {ScoreConverter.Format(group.Sum(g => g.Question.Score))} 分）",
                    Questions = group,
                });
                groupIndex++;
                i += group.Count;
            }
            Title = paper.Title;
            var login = Client.Login;
            StudentText = login == null ? "" : $"{login.Name} · {login.StudentNo}";
            Current = Questions.FirstOrDefault(q => q.Id == keepId) ?? Questions.FirstOrDefault();
            _warned5 = _warned1 = false;
            TimeUp = false;
            UpdateProgress();
            UpdateSync();
            UpdateClock();
            _clock.Start();
        }

        public void StopTimers()
        {
            FlushText();
            _clock.Stop();
            _textFlush.Stop();
        }

        private void OnAnswerChanged(QuestionVm q, bool isText)
        {
            if (isText)
            {
                // 文字输入稍后批量提交，避免每个按键都发送
                _dirtyText.Add(q);
                _textFlush.Stop();
                _textFlush.Start();
            }
            else
            {
                Client.SetAnswer(q.Id, q.GetValues());
            }
            UpdateProgress();
        }

        private void FlushText()
        {
            _textFlush.Stop();
            if (_dirtyText.Count == 0) return;
            foreach (var q in _dirtyText.ToList()) Client.SetAnswer(q.Id, q.GetValues());
            _dirtyText.Clear();
        }

        private void UpdateProgress()
        {
            AnsweredCount = Questions.Count(q => q.IsAnswered);
            ProgressText = $"已答 {AnsweredCount} / {Questions.Count}";
            Progress = Questions.Count == 0 ? 0 : 100.0 * AnsweredCount / Questions.Count;
        }

        public void UpdateSync()
        {
            if (Client.State == ConnectionState.Reconnecting || Client.State == ConnectionState.Disconnected)
            {
                SyncState = 2;
                SyncText = "连接中断 · 答案已保存在本机";
                return;
            }
            var pending = Client.PendingSyncCount + _dirtyText.Count;
            SyncState = pending > 0 ? 1 : 0;
            SyncText = pending > 0 ? $"正在保存 {pending} 题…" : "答案已自动保存";
        }

        private void UpdateClock()
        {
            var deadline = Client.DeadlineUtc;
            if (deadline == null)
            {
                ClockText = "--:--";
                return;
            }
            var left = deadline.Value - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            ClockText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
            ClockWarning = left.TotalMinutes < 5;
            // 老师延长时间后剩余时间又变多：重新允许提醒
            if (left.TotalMinutes > 5.5) _warned5 = false;
            if (left.TotalMinutes > 1.5) _warned1 = false;
            if (!_warned5 && left.TotalMinutes < 5 && left.TotalMinutes > 4)
            {
                _warned5 = true;
                Ui.ToastWarning("距离考试结束还有 5 分钟，请注意检查答案");
            }
            if (!_warned1 && left.TotalMinutes < 1 && left.TotalSeconds > 50)
            {
                _warned1 = true;
                Ui.ToastWarning("距离考试结束还有 1 分钟");
            }
            if (left == TimeSpan.Zero && !TimeUp)
            {
                TimeUp = true;
                FlushText();
            }
        }

        [RelayCommand]
        private void Go(QuestionVm q)
        {
            if (q != null) Current = q;
        }

        [RelayCommand]
        private void Previous()
        {
            var i = Questions.IndexOf(Current);
            if (i > 0) Current = Questions[i - 1];
        }

        [RelayCommand]
        private void Next()
        {
            var i = Questions.IndexOf(Current);
            if (i >= 0 && i < Questions.Count - 1) Current = Questions[i + 1];
        }

        [RelayCommand]
        private void ToggleFlag()
        {
            if (Current != null) Current.IsFlagged = !Current.IsFlagged;
        }

        [RelayCommand]
        private async Task SubmitAsync()
        {
            if (IsSubmitting) return;
            FlushText();
            var unanswered = Questions.Where(q => !q.IsAnswered).ToList();
            var flagged = Questions.Where(q => q.IsFlagged).ToList();
            var message = "交卷后不能再修改答案，确定交卷吗？";
            if (unanswered.Count > 0)
            {
                var list = string.Join("、", unanswered.Take(12).Select(q => q.Number)) + (unanswered.Count > 12 ? " 等" : "");
                message = $"还有 {unanswered.Count} 题没有作答（第 {list} 题）。\n\n" + message;
            }
            else if (flagged.Count > 0)
            {
                message = $"你标记了 {flagged.Count} 道题需要检查（第 {string.Join("、", flagged.Select(q => q.Number))} 题）。\n\n" + message;
            }
            if (!Ui.Confirm(message, "确认交卷")) return;
            IsSubmitting = true;
            try
            {
                var result = await Client.SubmitAsync();
                if (!result.Ok) Ui.Error(result.Error ?? "交卷失败，请重试", "交卷失败");
                // 成功时由 Submitted 事件切换到结果页
            }
            catch (Exception ex)
            {
                Log.Warn("交卷失败：" + ex.Message);
                Ui.Error("交卷失败：" + ex.Message + "\n\n你的答案已保存在本机，请检查网络后再次点击“交卷”。", "交卷失败");
            }
            finally
            {
                IsSubmitting = false;
            }
        }
    }
}
