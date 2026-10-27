using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Server.Services;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;
using Microsoft.Win32;

namespace MdQuiz.Teacher.ViewModels
{
    /// <summary>题型筛选项（Type 为 null 表示全部）。</summary>
    public class TypeOption
    {
        public QuestionType? Type { get; set; }
        public string Name { get; set; }

        public static List<TypeOption> WithAll() =>
            new[] { new TypeOption { Name = "全部题型" } }
                .Concat(QuestionTypes.All.Select(t => new TypeOption { Type = t, Name = QuestionTypes.DisplayName(t) })).ToList();

        public static List<TypeOption> Only() =>
            QuestionTypes.All.Select(t => new TypeOption { Type = t, Name = QuestionTypes.DisplayName(t) }).ToList();
    }

    public class QuestionItem
    {
        public QuestionItem(Question q)
        {
            Question = q;
        }

        public Question Question { get; }
        public long Id => Question.Id;
        public QuestionType Type => Question.Type;
        public string Summary => Question.Summary(120);
        public string TagsText => string.Join("  ", Question.Tags.Select(t => "#" + t));
        public DateTime UpdatedAt => Question.UpdatedAt;
    }

    public partial class QuestionBankViewModel : PageViewModel
    {
        public QuestionBankViewModel()
        {
            TypeOptions = TypeOption.WithAll();
            _selectedType = TypeOptions[0];
        }

        public List<TypeOption> TypeOptions { get; }
        public ObservableCollection<QuestionItem> Questions { get; } = new ObservableCollection<QuestionItem>();
        public ObservableCollection<string> Tags { get; } = new ObservableCollection<string>();

        [ObservableProperty] private string _keyword = "";
        [ObservableProperty] private TypeOption _selectedType;
        [ObservableProperty] private string _selectedTag;
        [ObservableProperty] private QuestionItem _selected;
        [ObservableProperty] private string _countText = "";
        [ObservableProperty] private bool _isEmpty;
        [ObservableProperty] private int _totalCount;

        private bool _suppressRefresh;

        partial void OnKeywordChanged(string value) => Refresh();
        partial void OnSelectedTypeChanged(TypeOption value) => Refresh();
        partial void OnSelectedTagChanged(string value) => Refresh();

        public override void OnActivated()
        {
            RefreshTags();
            Refresh();
        }

        private void RefreshTags()
        {
            var keep = SelectedTag;
            _suppressRefresh = true;
            try
            {
                Tags.Clear();
                Tags.Add("全部标签");
                foreach (var t in S.Questions.AllTags()) Tags.Add(t);
                SelectedTag = Tags.Contains(keep) ? keep : "全部标签";
            }
            finally
            {
                _suppressRefresh = false;
            }
        }

        public void Refresh()
        {
            if (_suppressRefresh) return;
            var keepId = Selected?.Id;
            var list = S.Questions.Query(new QuestionQuery
            {
                Keyword = Keyword,
                Type = SelectedType?.Type,
                Tag = SelectedTag == "全部标签" ? null : SelectedTag,
            });
            Questions.Clear();
            foreach (var q in list) Questions.Add(new QuestionItem(q));
            TotalCount = S.Questions.Count();
            CountText = list.Count == TotalCount ? $"共 {TotalCount} 题" : $"筛选出 {list.Count} 题（题库共 {TotalCount} 题）";
            IsEmpty = TotalCount == 0;
            Selected = Questions.FirstOrDefault(q => q.Id == keepId) ?? Questions.FirstOrDefault();
        }

        [RelayCommand]
        private void New()
        {
            var editor = new QuestionEditorWindow(null);
            if (Ui.ShowDialog(editor) != true) return;
            RefreshTags();
            Refresh();
            Selected = Questions.FirstOrDefault(q => q.Id == editor.Result.Id);
            Ui.Toast("题目已添加");
        }

        [RelayCommand]
        private void Edit(QuestionItem item)
        {
            item = item ?? Selected;
            if (item == null) return;
            var editor = new QuestionEditorWindow(S.Questions.Get(item.Id));
            if (Ui.ShowDialog(editor) != true) return;
            RefreshTags();
            Refresh();
            Ui.Toast("题目已保存");
        }

        [RelayCommand]
        private void Duplicate(QuestionItem item)
        {
            item = item ?? Selected;
            if (item == null) return;
            var copy = item.Question.Clone();
            copy.Id = 0;
            S.Questions.Insert(copy);
            Refresh();
            Selected = Questions.FirstOrDefault(q => q.Id == copy.Id);
            Ui.Toast("已复制题目");
        }

        [RelayCommand]
        private void Delete(IList selection)
        {
            var items = selection?.OfType<QuestionItem>().ToList() ?? new List<QuestionItem>();
            if (items.Count == 0 && Selected != null) items.Add(Selected);
            if (items.Count == 0) return;
            if (!Ui.ConfirmDanger(items.Count == 1 ? "确定删除这道题吗？" : $"确定删除选中的 {items.Count} 道题吗？",
                    "删除题目")) return;
            S.Questions.Delete(items.Select(i => i.Id));
            RefreshTags();
            Refresh();
            Ui.Toast($"已删除 {items.Count} 道题（已创建的考试不受影响）");
        }

        [RelayCommand]
        private void ImportMarkdown()
        {
            var dlg = new OpenFileDialog { Filter = "Markdown 题库|*.md;*.markdown;*.txt|所有文件|*.*", Title = "导入 Markdown 题库" };
            if (dlg.ShowDialog() != true) return;
            ParseResult result;
            try
            {
                result = S.Importer.ImportMarkdownFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                Ui.Error("读取文件失败：" + ex.Message);
                return;
            }
            RunImport(result, dlg.FileName);
        }

        [RelayCommand]
        private void ImportExcel()
        {
            var dlg = new OpenFileDialog { Filter = "Excel 题目表|*.xlsx;*.csv", Title = "导入 Excel 题目" };
            if (dlg.ShowDialog() != true) return;
            ParseResult result;
            try
            {
                result = ExcelService.ReadQuestions(dlg.FileName);
            }
            catch (Exception ex)
            {
                Ui.Error("读取 Excel 失败：" + ex.Message + "\n\n请确认文件没有被 Excel 占用，并使用“下载 Excel 模板”中的格式。");
                return;
            }
            RunImport(result, dlg.FileName);
        }

        private void RunImport(ParseResult result, string fileName)
        {
            if (result.Questions.Count == 0)
            {
                Ui.Info("文件中没有识别到题目。\n\nMarkdown 题库中每道题以“## [单选] 题干”开头，可参考 samples 目录中的示例题库。");
                return;
            }
            var preview = new ImportPreviewWindow(result, Path.GetFileNameWithoutExtension(fileName));
            if (Ui.ShowDialog(preview) != true) return;
            RefreshTags();
            Refresh();
        }

        [RelayCommand]
        private void ExportMarkdown()
        {
            var list = Questions.Select(q => q.Question).ToList();
            if (list.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = "Markdown 文件|*.md", FileName = "题库导出.md" };
            if (dlg.ShowDialog() != true) return;
            var md = QuestionMarkdown.Write(list.Select(q => new KeyValuePair<Question, double?>(q, null)), "题库导出");
            File.WriteAllText(dlg.FileName, md, new System.Text.UTF8Encoding(true));
            Ui.Toast($"已导出 {list.Count} 道题");
        }

        [RelayCommand]
        private void DownloadTemplate()
        {
            var dlg = new SaveFileDialog { Filter = "Excel 文件|*.xlsx", FileName = "题目导入模板.xlsx" };
            if (dlg.ShowDialog() != true) return;
            ExcelService.WriteQuestionTemplate(dlg.FileName);
            Ui.Toast("模板已保存");
        }

        [RelayCommand]
        private void ClearFilters()
        {
            _suppressRefresh = true;
            try
            {
                Keyword = "";
                SelectedTag = "全部标签";
                SelectedType = TypeOptions[0];
            }
            finally
            {
                _suppressRefresh = false;
            }
            Refresh();
        }
    }
}
