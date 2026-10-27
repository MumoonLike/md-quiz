using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MdQuiz.Core.Models;
using MdQuiz.Wpf.Controls;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.Views
{
    /// <summary>题目预览（含答案与解析），由代码生成内容。</summary>
    public class QuestionPreview : ContentControl
    {
        public static readonly DependencyProperty QuestionProperty = DependencyProperty.Register(
            nameof(Question), typeof(Question), typeof(QuestionPreview), new PropertyMetadata(null, (d, e) => ((QuestionPreview)d).Build()));

        public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
            nameof(Score), typeof(double?), typeof(QuestionPreview), new PropertyMetadata(null, (d, e) => ((QuestionPreview)d).Build()));

        public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(
            nameof(Number), typeof(int), typeof(QuestionPreview), new PropertyMetadata(0, (d, e) => ((QuestionPreview)d).Build()));

        public static readonly DependencyProperty ShowAnswerProperty = DependencyProperty.Register(
            nameof(ShowAnswer), typeof(bool), typeof(QuestionPreview), new PropertyMetadata(true, (d, e) => ((QuestionPreview)d).Build()));

        public QuestionPreview()
        {
            Focusable = false;
            IsTabStop = false;
        }

        public Question Question
        {
            get => (Question)GetValue(QuestionProperty);
            set => SetValue(QuestionProperty, value);
        }

        public double? Score
        {
            get => (double?)GetValue(ScoreProperty);
            set => SetValue(ScoreProperty, value);
        }

        public int Number
        {
            get => (int)GetValue(NumberProperty);
            set => SetValue(NumberProperty, value);
        }

        public bool ShowAnswer
        {
            get => (bool)GetValue(ShowAnswerProperty);
            set => SetValue(ShowAnswerProperty, value);
        }

        private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

        private void Build()
        {
            var q = Question;
            if (q == null)
            {
                Content = null;
                return;
            }
            var panel = new StackPanel();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            if (Number > 0)
                header.Children.Add(new TextBlock { Text = Number + ".", FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new Chip { Text = QuestionTypes.DisplayName(q.Type), Color = QuestionTypeColorConverter.ColorOf(q.Type) });
            if (Score.HasValue)
                header.Children.Add(new TextBlock { Text = ScoreConverter.Format(Score) + " 分", Foreground = Res("SecondaryTextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);

            panel.Children.Add(new MarkdownView { Markdown = q.Stem, NumberBlanks = true, FontSize = 15 });

            var correct = q.CorrectKeys();
            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                {
                    var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                    for (var i = 0; i < q.Options.Count; i++)
                    {
                        var key = Question.OptionKey(i);
                        var isCorrect = ShowAnswer && correct.Contains(key);
                        options.Children.Add(OptionRow(key, q.Options[i], isCorrect));
                    }
                    panel.Children.Add(options);
                    break;
                }
                case QuestionType.TrueFalse:
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                    row.Children.Add(Pill("对", ShowAnswer && q.Answer == "T"));
                    row.Children.Add(Pill("错", ShowAnswer && q.Answer == "F"));
                    panel.Children.Add(row);
                    break;
                }
                case QuestionType.FillBlank when ShowAnswer:
                {
                    var box = Section("答案");
                    for (var i = 0; i < q.Blanks.Count; i++)
                    {
                        var alts = string.Join(" / ", Question.BlankAlternatives(q.Blanks[i]));
                        box.Children.Add(new TextBlock { Text = $"第 {i + 1} 空：{alts}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = Res("DarkSuccessBrush") });
                    }
                    panel.Children.Add(box);
                    break;
                }
                case QuestionType.ShortAnswer when ShowAnswer:
                {
                    var box = Section("参考答案（人工批改）");
                    box.Children.Add(string.IsNullOrWhiteSpace(q.Answer)
                        ? (UIElement)new TextBlock { Text = "未填写参考答案", Foreground = Res("ThirdlyTextBrush") }
                        : new MarkdownView { Markdown = q.Answer });
                    panel.Children.Add(box);
                    break;
                }
            }

            if (ShowAnswer && !string.IsNullOrWhiteSpace(q.Analysis))
            {
                var box = Section("解析");
                box.Children.Add(new MarkdownView { Markdown = q.Analysis, Foreground = Res("SecondaryTextBrush") });
                panel.Children.Add(box);
            }

            if (q.Tags.Count > 0)
            {
                var tags = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
                foreach (var t in q.Tags) tags.Children.Add(new Chip { Text = "#" + t, Color = Palette.Gray, Margin = new Thickness(0, 0, 6, 6) });
                panel.Children.Add(tags);
            }
            Content = panel;
        }

        private static StackPanel Section(string title)
        {
            var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            box.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Res("SecondaryTextBrush"), Margin = new Thickness(0, 0, 0, 6), FontSize = 12 });
            return box;
        }

        private static UIElement OptionRow(string key, string text, bool correct)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = correct ? Res("SuccessBrush") : Res("RegionBrush"),
                BorderBrush = correct ? Res("SuccessBrush") : Res("BorderBrush"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = key,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = correct ? Brushes.White : Res("SecondaryTextBrush"),
                },
            };
            var content = new MarkdownView { Markdown = text, Margin = new Thickness(12, 2, 0, 0), BlockSpacing = 4 };
            Grid.SetColumn(content, 1);
            grid.Children.Add(badge);
            grid.Children.Add(content);
            return new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                Background = correct ? Res("LightSuccessBrush") : Res("SecondaryRegionBrush"),
                Child = grid,
            };
        }

        private static UIElement Pill(string text, bool correct)
        {
            return new Border
            {
                Padding = new Thickness(22, 6, 22, 6),
                Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(16),
                Background = correct ? Res("SuccessBrush") : Res("SecondaryRegionBrush"),
                Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = correct ? Brushes.White : Res("SecondaryTextBrush") },
            };
        }
    }
}
