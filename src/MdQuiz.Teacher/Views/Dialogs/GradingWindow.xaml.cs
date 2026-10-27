using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MdQuiz.Core.Models;
using MdQuiz.Server.Hosting;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class GradingWindow
    {
        public partial class QuestionRow : ObservableObject
        {
            public ExamQuestion Question { get; set; }
            public int Number { get; set; }
            public string TypeText => QuestionTypes.ShortName(Question.Type);
            public Color TypeColor => QuestionTypeColorConverter.ColorOf(Question.Type);
            public string ScoreText => ScoreConverter.Format(Question.Score) + " 分";

            [ObservableProperty] private int _pending;
            [ObservableProperty] private string _pendingText = "";
            [ObservableProperty] private bool _hasPending;

            public void SetPending(int count, bool manualType)
            {
                Pending = count;
                HasPending = count > 0;
                PendingText = count > 0 ? $"待批改 {count}" : manualType ? "已批完" : "自动判分";
            }
        }

        public partial class AnswerRow : ObservableObject
        {
            private readonly Action<AnswerRow> _scoreChanged;
            private bool _silent;
            private double _applied;

            public AnswerRow(Action<AnswerRow> scoreChanged)
            {
                _scoreChanged = scoreChanged;
            }

            public string StudentNo { get; set; }
            public string Name { get; set; }
            public string AnswerText { get; set; }
            public string CorrectText { get; set; }
            public double MaxScore { get; set; }
            public string MaxText => ScoreConverter.Format(MaxScore);

            [ObservableProperty] private double _score;
            [ObservableProperty] private bool _manual;
            [ObservableProperty] private bool _pending;
            [ObservableProperty] private string _statusText = "";
            [ObservableProperty] private Color _statusColor;

            partial void OnScoreChanged(double value)
            {
                // 控件回写的取整误差不算改分，避免把自动判分误存为人工分
                if (_silent || Math.Abs(value - _applied) < 0.005) return;
                _scoreChanged(this);
            }

            public void Apply(double? score, bool manual)
            {
                _silent = true;
                Score = score ?? 0;
                _applied = Score;
                _silent = false;
                Manual = manual;
                Pending = !score.HasValue;
                if (Pending)
                {
                    StatusText = "待批改";
                    StatusColor = Palette.Violet;
                }
                else if (manual)
                {
                    StatusText = "人工给分";
                    StatusColor = Palette.Blue;
                }
                else
                {
                    StatusText = "自动判分";
                    StatusColor = Palette.Gray;
                }
            }
        }

        private readonly Exam _exam;
        private readonly List<QuestionRow> _questions;
        private readonly Dictionary<string, Participant> _participants;
        private List<AnswerRow> _rows = new List<AnswerRow>();

        public GradingWindow(Exam exam)
        {
            InitializeComponent();
            _exam = exam;
            ExamTitle.Text = exam.Title;
            _participants = AppServices.Current.Exams.ListParticipants(exam.Id)
                .Where(p => p.Status == ParticipantStatus.Submitted).ToDictionary(p => p.StudentNo);
            _questions = exam.Questions.Select((q, i) => new QuestionRow { Question = q, Number = i + 1 }).ToList();
            RefreshPendingCounts();
            QuestionList.ItemsSource = _questions;
            QuestionList.SelectedItem = _questions.FirstOrDefault(q => q.HasPending)
                                        ?? _questions.FirstOrDefault(q => q.Question.Type == QuestionType.ShortAnswer)
                                        ?? _questions.FirstOrDefault();
        }

        private void RefreshPendingCounts()
        {
            var all = AppServices.Current.Exams.ListAllAnswers(_exam.Id).Where(a => _participants.ContainsKey(a.StudentNo)).ToList();
            foreach (var row in _questions)
            {
                var pending = all.Count(a => a.QuestionId == row.Question.Id && !a.Score.HasValue);
                row.SetPending(pending, !QuestionTypes.IsAutoGraded(row.Question.Type));
            }
        }

        private QuestionRow Current => QuestionList.SelectedItem as QuestionRow;

        private void OnQuestionChanged(object sender, SelectionChangedEventArgs e) => LoadAnswers();

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (_exam != null) ShowRows();
        }

        private void LoadAnswers()
        {
            var row = Current;
            if (row == null) return;
            var q = row.Question;
            Preview.Score = q.Score;
            Preview.Number = row.Number;
            Preview.Question = q;
            var answers = AppServices.Current.Exams.ListAnswersForQuestion(_exam.Id, q.Id).ToDictionary(a => a.StudentNo);
            var correct = QuestionTypes.IsAutoGraded(q.Type) ? "正确答案：" + AnswerFormatter.FormatCorrect(q) : "";
            _rows = _participants.Values.OrderBy(p => p.ClassName).ThenBy(p => p.StudentNo).Select(p =>
            {
                answers.TryGetValue(p.StudentNo, out var a);
                var text = a == null ? "" : AnswerFormatter.Format(q, a.Values);
                var r = new AnswerRow(OnScoreChanged)
                {
                    StudentNo = p.StudentNo,
                    Name = p.Name,
                    AnswerText = string.IsNullOrWhiteSpace(text) ? "（未作答）" : text,
                    CorrectText = correct,
                    MaxScore = q.Score,
                };
                r.Apply(a?.Score ?? (QuestionTypes.IsAutoGraded(q.Type) ? 0 : (double?)null), a?.Manual ?? false);
                return r;
            }).ToList();
            ShowRows();
        }

        private void ShowRows()
        {
            var only = OnlyPendingBox.IsChecked == true;
            var list = only ? _rows.Where(r => r.Pending).ToList() : _rows;
            AnswerList.ItemsSource = list;
            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var pending = _rows.Count(r => r.Pending);
            AnswerSummary.Text = pending > 0 ? $"共 {_rows.Count} 份答卷，{pending} 份待批改" : $"共 {_rows.Count} 份答卷";
        }

        private void Save(AnswerRow row, double? score)
        {
            var q = Current?.Question;
            if (q == null) return;
            try
            {
                AppServices.Current.Results.SetManualScore(_exam, row.StudentNo, q.Id, score);
                var saved = AppServices.Current.Exams.ListAnswers(_exam.Id, row.StudentNo).FirstOrDefault(a => a.QuestionId == q.Id);
                row.Apply(saved?.Score, saved?.Manual ?? false);
                RefreshPendingCounts();
                var pending = _rows.Count(r => r.Pending);
                AnswerSummary.Text = pending > 0 ? $"共 {_rows.Count} 份答卷，{pending} 份待批改" : $"共 {_rows.Count} 份答卷";
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        private void OnScoreChanged(AnswerRow row) => Save(row, row.Score);

        private static AnswerRow RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AnswerRow;

        private void OnZero(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row != null) Save(row, 0);
        }

        private void OnFull(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row != null) Save(row, row.MaxScore);
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            var row = RowOf(sender);
            if (row != null) Save(row, null);
        }
    }
}
