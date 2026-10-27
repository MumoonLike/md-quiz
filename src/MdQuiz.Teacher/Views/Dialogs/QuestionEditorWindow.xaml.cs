using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;
using Microsoft.Win32;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class QuestionEditorWindow
    {
        private readonly QuestionEditorViewModel _vm;

        public QuestionEditorWindow(Question question)
        {
            InitializeComponent();
            Title = question == null ? "新建题目" : "编辑题目";
            _vm = new QuestionEditorViewModel(question);
            DataContext = _vm;
            Loaded += (s, e) => StemBox.Focus();
        }

        /// <summary>保存后的题目。</summary>
        public Question Result { get; private set; }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var q = _vm.Build();
            var error = q.Validate();
            if (error != null)
            {
                _vm.Error = error;
                return;
            }
            var repo = AppServices.Current.Questions;
            if (q.Id > 0) repo.Update(q);
            else repo.Insert(q);
            Result = q;
            DialogResult = true;
        }

        private void Insert(string before, string after = "")
        {
            var start = StemBox.SelectionStart;
            var selected = StemBox.SelectedText;
            var text = StemBox.Text ?? "";
            StemBox.Text = text.Substring(0, start) + before + selected + after + text.Substring(start + StemBox.SelectionLength);
            StemBox.Focus();
            StemBox.SelectionStart = start + before.Length;
            StemBox.SelectionLength = selected.Length;
        }

        private void OnInsertBlank(object sender, RoutedEventArgs e) => Insert(" ____ ");

        private void OnInsertFormula(object sender, RoutedEventArgs e) => Insert("$", "$");

        private void OnInsertBold(object sender, RoutedEventArgs e) => Insert("**", "**");

        private void OnInsertCode(object sender, RoutedEventArgs e) => Insert("\n```\n", "\n```\n");

        private void OnInsertImage(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp", Title = "插入图片" };
            if (dlg.ShowDialog() != true) return;
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 4 * 1024 * 1024 && !Ui.Confirm("图片超过 4 MB，会增加试卷下发时间，建议压缩后再插入。仍要插入吗？")) return;
            var hash = AppServices.Current.Resources.Save(File.ReadAllBytes(dlg.FileName), ResourceRepository.MimeFromExtension(info.Extension));
            Insert($"\n![{Path.GetFileNameWithoutExtension(info.Name)}](res:{hash})\n");
        }
    }

    public partial class OptionRow : ObservableObject
    {
        private readonly Action<OptionRow> _correctChanged;

        public OptionRow(string text, bool correct, Action<OptionRow> correctChanged)
        {
            _text = text;
            _isCorrect = correct;
            _correctChanged = correctChanged;
        }

        [ObservableProperty] private string _letter = "";
        [ObservableProperty] private string _text = "";
        [ObservableProperty] private bool _isCorrect;

        partial void OnIsCorrectChanged(bool value) => _correctChanged?.Invoke(this);

        partial void OnTextChanged(string value) => _correctChanged?.Invoke(null);
    }

    public partial class BlankRow : ObservableObject
    {
        private readonly Action _changed;

        public BlankRow(int index, string text, Action changed)
        {
            Label = $"第 {index} 空";
            _text = text;
            _changed = changed;
        }

        public string Label { get; }

        [ObservableProperty] private string _text = "";

        partial void OnTextChanged(string value) => _changed?.Invoke();
    }

    public partial class QuestionEditorViewModel : ObservableObject
    {
        private readonly Question _original;
        private readonly DispatcherTimer _previewTimer;
        private bool _updatingCorrect;

        public QuestionEditorViewModel(Question q)
        {
            _original = q;
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                Preview = Build();
            };

            _type = q?.Type ?? QuestionType.Single;
            _stem = q?.Stem ?? "";
            _analysis = q?.Analysis ?? "";
            _tagsText = q == null ? "" : string.Join(", ", q.Tags);
            var correct = q?.CorrectKeys() ?? new HashSet<string>();
            if (q != null && QuestionTypes.HasOptions(q.Type))
            {
                for (var i = 0; i < q.Options.Count; i++) Options.Add(new OptionRow(q.Options[i], correct.Contains(Question.OptionKey(i)), OnCorrectChanged));
            }
            while (Options.Count < 4) Options.Add(new OptionRow("", false, OnCorrectChanged));
            Relabel();
            _tfTrue = q?.Type == QuestionType.TrueFalse && q.Answer == "T";
            _tfFalse = q?.Type == QuestionType.TrueFalse && q.Answer == "F";
            _shortAnswer = q?.Type == QuestionType.ShortAnswer ? q.Answer : "";
            SyncBlanks(q?.Type == QuestionType.FillBlank ? q.Blanks : new List<string>());
            foreach (var t in AppServices.Current.Questions.AllTags().Take(30)) KnownTags.Add(t);
            Preview = Build();
        }

        public ObservableCollection<OptionRow> Options { get; } = new ObservableCollection<OptionRow>();
        public ObservableCollection<BlankRow> Blanks { get; } = new ObservableCollection<BlankRow>();
        public ObservableCollection<string> KnownTags { get; } = new ObservableCollection<string>();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsChoice), nameof(IsTrueFalse), nameof(IsFill), nameof(IsShort))]
        private QuestionType _type;

        [ObservableProperty] private string _stem;
        [ObservableProperty] private string _analysis;
        [ObservableProperty] private string _tagsText;
        [ObservableProperty] private string _shortAnswer;
        [ObservableProperty] private bool _tfTrue;
        [ObservableProperty] private bool _tfFalse;
        [ObservableProperty] private string _error = "";
        [ObservableProperty] private Question _preview;

        public bool IsChoice => Type == QuestionType.Single || Type == QuestionType.Multiple;
        public bool IsTrueFalse => Type == QuestionType.TrueFalse;
        public bool IsFill => Type == QuestionType.FillBlank;
        public bool IsShort => Type == QuestionType.ShortAnswer;

        partial void OnTypeChanged(QuestionType value)
        {
            if (value == QuestionType.Single && Options.Count(o => o.IsCorrect) > 1)
            {
                _updatingCorrect = true;
                var first = true;
                foreach (var o in Options.Where(o => o.IsCorrect))
                {
                    if (!first) o.IsCorrect = false;
                    first = false;
                }
                _updatingCorrect = false;
            }
            Changed();
        }

        partial void OnStemChanged(string value)
        {
            SyncBlanks(Blanks.Select(b => b.Text).ToList());
            Changed();
        }

        partial void OnAnalysisChanged(string value) => Changed();
        partial void OnTagsTextChanged(string value) => Changed();
        partial void OnShortAnswerChanged(string value) => Changed();
        partial void OnTfTrueChanged(bool value) => Changed();
        partial void OnTfFalseChanged(bool value) => Changed();

        private void Changed()
        {
            Error = "";
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void OnCorrectChanged(OptionRow row)
        {
            if (row != null && !_updatingCorrect && row.IsCorrect && Type == QuestionType.Single)
            {
                _updatingCorrect = true;
                foreach (var o in Options.Where(o => o != row)) o.IsCorrect = false;
                _updatingCorrect = false;
            }
            Changed();
        }

        private void Relabel()
        {
            for (var i = 0; i < Options.Count; i++) Options[i].Letter = Question.OptionKey(i);
        }

        /// <summary>空位个数随题干变化，保留已填写的答案。</summary>
        private void SyncBlanks(IList<string> current)
        {
            var count = BlankMarkerCount();
            if (count == Blanks.Count && current.Count == count) return;
            Blanks.Clear();
            for (var i = 0; i < count; i++) Blanks.Add(new BlankRow(i + 1, i < current.Count ? current[i] : "", Changed));
        }

        private int BlankMarkerCount() => MdQuiz.Core.BlankMarker.Count(Stem);

        [RelayCommand]
        private void AddOption()
        {
            if (Options.Count >= Question.MaxOptions)
            {
                Error = $"最多 {Question.MaxOptions} 个选项";
                return;
            }
            Options.Add(new OptionRow("", false, OnCorrectChanged));
            Relabel();
            Changed();
        }

        [RelayCommand]
        private void RemoveOption(OptionRow row)
        {
            if (row == null || Options.Count <= 2) return;
            Options.Remove(row);
            Relabel();
            Changed();
        }

        [RelayCommand]
        private void AddTag(string tag)
        {
            var tags = SplitTags(TagsText);
            if (tags.Contains(tag)) return;
            tags.Add(tag);
            TagsText = string.Join(", ", tags);
        }

        private static List<string> SplitTags(string text) =>
            (text ?? "").Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();

        public Question Build()
        {
            var q = _original?.Clone() ?? new Question();
            q.Type = Type;
            q.Stem = (Stem ?? "").Trim();
            q.Analysis = (Analysis ?? "").Trim();
            q.Tags = SplitTags(TagsText);
            q.Options = new List<string>();
            q.Blanks = new List<string>();
            q.Answer = "";
            switch (Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                    // 去掉末尾的空选项
                    var rows = Options.ToList();
                    while (rows.Count > 0 && string.IsNullOrWhiteSpace(rows[rows.Count - 1].Text) && !rows[rows.Count - 1].IsCorrect) rows.RemoveAt(rows.Count - 1);
                    q.Options = rows.Select(o => (o.Text ?? "").Trim()).ToList();
                    q.Answer = Question.NormalizeKeys(rows.Select((o, i) => o.IsCorrect ? Question.OptionKey(i) : null).Where(k => k != null));
                    break;
                case QuestionType.TrueFalse:
                    q.Answer = TfTrue ? "T" : TfFalse ? "F" : "";
                    break;
                case QuestionType.FillBlank:
                    q.Blanks = Blanks.Select(b => string.Join("|", Question.BlankAlternatives((b.Text ?? "").Replace('｜', '|')))).ToList();
                    break;
                case QuestionType.ShortAnswer:
                    q.Answer = (ShortAnswer ?? "").Trim();
                    break;
            }
            return q;
        }
    }
}
