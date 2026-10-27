using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MdQuiz.Core.Models;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class PaperPreviewWindow
    {
        public class Item
        {
            public Question Question { get; set; }
            public double? Score { get; set; }
            public int Number { get; set; }
            public bool ShowAnswer { get; set; }
        }

        private readonly IList<(Question question, double score)> _questions;

        public PaperPreviewWindow(string name, IList<(Question question, double score)> questions)
        {
            InitializeComponent();
            _questions = questions;
            TitleText.Text = string.IsNullOrWhiteSpace(name) ? "试卷预览" : name;
            SubText.Text = $"共 {questions.Count} 题 · 满分 {ScoreConverter.Format(questions.Sum(q => q.score))}";
            Fill();
        }

        private void Fill()
        {
            var show = ShowAnswerBox.IsChecked == true;
            QuestionList.ItemsSource = _questions.Select((q, i) => new Item { Question = q.question, Score = q.score, Number = i + 1, ShowAnswer = show }).ToList();
        }

        private void OnShowAnswerChanged(object sender, RoutedEventArgs e)
        {
            if (_questions != null && QuestionList != null) Fill();
        }
    }
}
