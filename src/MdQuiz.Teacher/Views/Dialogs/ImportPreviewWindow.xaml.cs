using System.Linq;
using System.Windows;
using System.Windows.Media;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class ImportPreviewWindow
    {
        private readonly ParseResult _result;

        public class Row
        {
            public string LineText { get; set; }
            public string TypeText { get; set; }
            public Color TypeColor { get; set; }
            public string Summary { get; set; }
            public string Error { get; set; }
            public string ScoreText { get; set; }
        }

        public ImportPreviewWindow(ParseResult result, string defaultName)
        {
            InitializeComponent();
            _result = result;
            var valid = result.ValidCount;
            var invalid = result.Questions.Count - valid;
            SummaryText.Text = invalid == 0 ? $"识别到 {valid} 道题" : $"识别到 {result.Questions.Count} 道题，其中 {invalid} 道有问题";
            SummaryHint.Text = invalid == 0 ? "确认无误后点击导入。" : "有问题的题目不会被导入，可以修改文件后重新导入，或导入后在题库中逐题修正。";
            TagBox.Text = "";
            PaperNameBox.Text = string.IsNullOrWhiteSpace(result.Title) ? defaultName : result.Title;
            ImportButton.Content = $"导入 {valid} 道题";
            ImportButton.IsEnabled = valid > 0;
            if (result.Warnings.Count > 0)
            {
                WarningsPanel.Visibility = Visibility.Visible;
                WarningsText.Text = string.Join("\n", result.Warnings.Take(20)) + (result.Warnings.Count > 20 ? "\n……" : "");
            }
            ItemsList.ItemsSource = result.Questions.Select(q => new Row
            {
                LineText = $"第 {q.Line} 行",
                TypeText = q.Question.Type == 0 ? "未知" : QuestionTypes.ShortName(q.Question.Type),
                TypeColor = q.Question.Type == 0 ? Palette.Gray : QuestionTypeColorConverter.ColorOf(q.Question.Type),
                Summary = string.IsNullOrWhiteSpace(q.Question.Stem) ? "（题干为空）" : q.Question.Summary(90),
                Error = q.Error,
                ScoreText = q.Score.HasValue ? ScoreConverter.Format(q.Score) + " 分" : "",
            }).ToList();
        }

        private void OnCreatePaperChanged(object sender, RoutedEventArgs e)
        {
            PaperNameBox.IsEnabled = CreatePaperBox.IsChecked == true;
        }

        private void OnImport(object sender, RoutedEventArgs e)
        {
            var s = AppServices.Current;
            var valid = _result.Questions.Where(q => q.IsValid).ToList();
            var extraTags = (TagBox.Text ?? "").Split(new[] { ',', '，', '、' }, System.StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            foreach (var q in valid)
            {
                foreach (var t in extraTags)
                    if (!q.Question.Tags.Contains(t)) q.Question.Tags.Add(t);
            }
            s.Questions.InsertMany(valid.Select(q => q.Question));

            if (CreatePaperBox.IsChecked == true)
            {
                var paper = new Paper
                {
                    Name = string.IsNullOrWhiteSpace(PaperNameBox.Text) ? "导入的试卷" : PaperNameBox.Text.Trim(),
                    Items = valid.Select(q => new PaperItem { QuestionId = q.Question.Id, Score = q.Score ?? DefaultScore(q.Question.Type) }).ToList(),
                };
                s.Papers.Save(paper);
                Ui.Toast($"已导入 {valid.Count} 道题，并创建试卷“{paper.Name}”");
            }
            else
            {
                Ui.Toast($"已导入 {valid.Count} 道题");
            }
            DialogResult = true;
        }

        /// <summary>文件中没写分值时的默认分值。</summary>
        public static double DefaultScore(QuestionType type)
        {
            switch (type)
            {
                case QuestionType.Multiple: return 4;
                case QuestionType.TrueFalse: return 1;
                case QuestionType.FillBlank: return 2;
                case QuestionType.ShortAnswer: return 10;
                default: return 2;
            }
        }
    }
}
