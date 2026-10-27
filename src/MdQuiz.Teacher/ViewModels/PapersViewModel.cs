using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.ViewModels
{
    public partial class PaperItemViewModel : ObservableObject
    {
        private readonly Action _changed;

        public PaperItemViewModel(Question q, double score, Action changed)
        {
            Question = q;
            _score = score;
            _changed = changed;
        }

        public Question Question { get; }
        public long QuestionId => Question.Id;
        public QuestionType Type => Question.Type;
        public string Summary => Question.Summary(100);

        [ObservableProperty] private int _number;
        [ObservableProperty] private double _score;

        partial void OnScoreChanged(double value) => _changed?.Invoke();
    }

    public class TypeStat
    {
        public QuestionType Type { get; set; }
        public int Count { get; set; }
        public double Score { get; set; }
        public string Text => $"{Count} 题 · {ScoreConverter.Format(Score)} 分";
    }

    public partial class PapersViewModel : PageViewModel
    {
        private bool _loading;

        public ObservableCollection<Paper> Papers { get; } = new ObservableCollection<Paper>();
        public ObservableCollection<PaperItemViewModel> Items { get; } = new ObservableCollection<PaperItemViewModel>();
        public ObservableCollection<TypeStat> Stats { get; } = new ObservableCollection<TypeStat>();

        [ObservableProperty] private Paper _selectedPaper;
        [ObservableProperty] private PaperItemViewModel _selectedItem;
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _description = "";
        [ObservableProperty] private bool _isDirty;
        [ObservableProperty] private bool _hasPaper;
        [ObservableProperty] private string _totalText = "";
        [ObservableProperty] private int _missingCount;

        partial void OnNameChanged(string value) => MarkDirty();
        partial void OnDescriptionChanged(string value) => MarkDirty();

        partial void OnSelectedPaperChanging(Paper oldValue, Paper newValue)
        {
            if (!_loading && IsDirty && oldValue != null && newValue != oldValue && Ui.Confirm($"试卷“{Name}”有未保存的修改，要保存吗？", "保存修改"))
                Persist(oldValue);
        }

        partial void OnSelectedPaperChanged(Paper value)
        {
            if (!_loading) Load(value);
        }

        public override void OnActivated()
        {
            if (IsDirty) return; // 保留未保存的编辑
            var keep = SelectedPaper?.Id;
            ReloadList();
            var target = Papers.FirstOrDefault(p => p.Id == keep) ?? Papers.FirstOrDefault();
            SetSelectedSilently(target);
            Load(target);
        }

        private void ReloadList()
        {
            _loading = true;
            try
            {
                Papers.Clear();
                foreach (var p in S.Papers.List()) Papers.Add(p);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>只更新选中项，不重新加载编辑区。</summary>
        private void SetSelectedSilently(Paper paper)
        {
            _loading = true;
            try
            {
                SelectedPaper = paper;
            }
            finally
            {
                _loading = false;
            }
        }

        private void ShowPaper(long id)
        {
            ReloadList();
            var target = Papers.FirstOrDefault(p => p.Id == id);
            SetSelectedSilently(target);
            Load(target);
        }

        private void MarkDirty()
        {
            if (_loading) return;
            IsDirty = true;
        }

        private void Load(Paper paper)
        {
            _loading = true;
            try
            {
                Items.Clear();
                HasPaper = paper != null;
                Name = paper?.Name ?? "";
                Description = paper?.Description ?? "";
                MissingCount = 0;
                if (paper != null)
                {
                    var questions = S.Questions.GetMany(paper.Items.Select(i => i.QuestionId));
                    foreach (var item in paper.Items)
                    {
                        if (questions.TryGetValue(item.QuestionId, out var q)) Items.Add(new PaperItemViewModel(q, item.Score, ItemChanged));
                        else MissingCount++;
                    }
                }
                Renumber();
                IsDirty = false;
            }
            finally
            {
                _loading = false;
            }
        }

        private void ItemChanged()
        {
            UpdateStats();
            MarkDirty();
        }

        private void Renumber()
        {
            var n = 0;
            foreach (var i in Items) i.Number = ++n;
            UpdateStats();
        }

        private void UpdateStats()
        {
            Stats.Clear();
            foreach (var g in Items.GroupBy(i => i.Type).OrderBy(g => g.Key))
                Stats.Add(new TypeStat { Type = g.Key, Count = g.Count(), Score = g.Sum(i => i.Score) });
            TotalText = $"共 {Items.Count} 题 · 满分 {ScoreConverter.Format(Items.Sum(i => i.Score))}";
        }

        private HashSet<long> CurrentIds() => new HashSet<long>(Items.Select(i => i.QuestionId));

        [RelayCommand]
        private void New()
        {
            if (IsDirty && SelectedPaper != null && Ui.Confirm($"试卷“{Name}”有未保存的修改，要保存吗？", "保存修改")) Persist(SelectedPaper);
            var name = TextInputWindow.Ask("新建试卷", "试卷名称", $"新试卷 {DateTime.Now:MMdd}");
            if (string.IsNullOrWhiteSpace(name)) return;
            var paper = new Paper { Name = name.Trim() };
            S.Papers.Save(paper);
            IsDirty = false;
            ShowPaper(paper.Id);
        }

        [RelayCommand]
        private void Duplicate()
        {
            if (SelectedPaper == null) return;
            SaveCurrent();
            var source = SelectedPaper;
            var copy = new Paper
            {
                Name = source.Name + "（副本）",
                Description = source.Description,
                Items = source.Items.Select(i => new PaperItem { QuestionId = i.QuestionId, Score = i.Score }).ToList(),
            };
            S.Papers.Save(copy);
            ShowPaper(copy.Id);
            Ui.Toast("已复制试卷");
        }

        [RelayCommand]
        private void Delete()
        {
            if (SelectedPaper == null) return;
            if (!Ui.ConfirmDanger($"确定删除试卷“{SelectedPaper.Name}”吗？\n已用这份试卷创建的考试不受影响。", "删除试卷")) return;
            var index = Papers.IndexOf(SelectedPaper);
            S.Papers.Delete(SelectedPaper.Id);
            IsDirty = false;
            ReloadList();
            var target = Papers.Count == 0 ? null : Papers[Math.Max(0, Math.Min(index, Papers.Count - 1))];
            SetSelectedSilently(target);
            Load(target);
        }

        [RelayCommand]
        private void Save()
        {
            if (SelectedPaper == null) return;
            SaveCurrent();
            Ui.Toast("试卷已保存");
        }

        /// <summary>保存当前编辑的试卷并刷新左侧列表（编辑区保持不变）。</summary>
        private void SaveCurrent()
        {
            var paper = SelectedPaper;
            if (paper == null) return;
            Persist(paper);
            ReloadList();
            SetSelectedSilently(Papers.FirstOrDefault(p => p.Id == paper.Id));
        }

        /// <summary>把编辑区内容写入数据库。</summary>
        private void Persist(Paper paper)
        {
            paper.Name = string.IsNullOrWhiteSpace(Name) ? "未命名试卷" : Name.Trim();
            paper.Description = Description ?? "";
            paper.Items = Items.Select(i => new PaperItem { QuestionId = i.QuestionId, Score = i.Score }).ToList();
            S.Papers.Save(paper);
            IsDirty = false;
        }

        [RelayCommand]
        private void AddManual()
        {
            if (SelectedPaper == null) return;
            var picker = new QuestionPickerWindow(CurrentIds());
            if (Ui.ShowDialog(picker) != true) return;
            foreach (var q in picker.Picked) Items.Add(new PaperItemViewModel(q, picker.ScoreFor(q.Type), ItemChanged));
            Renumber();
            MarkDirty();
            Ui.Toast($"已添加 {picker.Picked.Count} 道题");
        }

        [RelayCommand]
        private void AddRandom()
        {
            if (SelectedPaper == null) return;
            var dialog = new RandomDrawWindow(CurrentIds());
            if (Ui.ShowDialog(dialog) != true) return;
            var questions = S.Questions.GetMany(dialog.Result.Items.Select(i => i.QuestionId));
            foreach (var item in dialog.Result.Items)
            {
                if (questions.TryGetValue(item.QuestionId, out var q)) Items.Add(new PaperItemViewModel(q, item.Score, ItemChanged));
            }
            Renumber();
            MarkDirty();
            if (dialog.Result.Warnings.Count > 0) Ui.Info(string.Join("\n", dialog.Result.Warnings), "部分题型题目不足");
            else Ui.Toast($"已随机抽取 {dialog.Result.Items.Count} 道题");
        }

        [RelayCommand]
        private void MoveUp(PaperItemViewModel item)
        {
            var i = Items.IndexOf(item);
            if (i <= 0) return;
            Items.Move(i, i - 1);
            Renumber();
            MarkDirty();
        }

        [RelayCommand]
        private void MoveDown(PaperItemViewModel item)
        {
            var i = Items.IndexOf(item);
            if (i < 0 || i >= Items.Count - 1) return;
            Items.Move(i, i + 1);
            Renumber();
            MarkDirty();
        }

        [RelayCommand]
        private void Remove(PaperItemViewModel item)
        {
            if (item == null) return;
            Items.Remove(item);
            Renumber();
            MarkDirty();
        }

        [RelayCommand]
        private void SortByType()
        {
            var sorted = Items.OrderBy(i => i.Type).ToList();
            Items.Clear();
            foreach (var i in sorted) Items.Add(i);
            Renumber();
            MarkDirty();
        }

        [RelayCommand]
        private void SetScore(TypeStat stat)
        {
            if (stat == null) return;
            var text = TextInputWindow.Ask("统一设置分值", $"{QuestionTypes.DisplayName(stat.Type)}每题分值",
                ScoreConverter.Format(Items.Where(i => i.Type == stat.Type).Select(i => i.Score).FirstOrDefault()));
            if (text == null) return;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score) || score < 0 || score > 1000)
            {
                Ui.Error("请输入有效的分值");
                return;
            }
            _loading = true;
            foreach (var i in Items.Where(i => i.Type == stat.Type)) i.Score = score;
            _loading = false;
            UpdateStats();
            MarkDirty();
        }

        [RelayCommand]
        private void Preview()
        {
            if (Items.Count == 0) return;
            var window = new PaperPreviewWindow(Name, Items.Select(i => (i.Question, i.Score)).ToList());
            Ui.ShowDialog(window);
        }

        [RelayCommand]
        private void CreateExam()
        {
            if (SelectedPaper == null) return;
            if (IsDirty) SaveCurrent();
            if (Items.Count == 0)
            {
                Ui.Info("试卷中还没有题目");
                return;
            }
            var paperId = SelectedPaper.Id;
            Shell.Navigate("exams");
            var dialog = new ExamEditorWindow(paperId);
            if (Ui.ShowDialog(dialog) != true) return;
            Shell.Exams.Refresh();
            if (dialog.OpenNow) Shell.Exams.OpenCommand.Execute(Shell.Exams.Exams.FirstOrDefault(e => e.Id == dialog.CreatedExamId));
        }
    }
}
