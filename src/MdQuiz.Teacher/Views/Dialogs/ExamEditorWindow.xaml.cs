using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MdQuiz.Core.Models;
using MdQuiz.Teacher.Services;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class ExamEditorWindow
    {
        public partial class ClassOption : ObservableObject
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public int Count { get; set; }

            [ObservableProperty] private bool _selected;
        }

        private readonly List<ClassOption> _classes;
        private string _autoTitle = "";

        public ExamEditorWindow(long? paperId = null)
        {
            InitializeComponent();
            var s = AppServices.Current;
            var papers = s.Papers.List().Where(p => p.Items.Count > 0).ToList();
            PaperBox.ItemsSource = papers;
            PaperBox.SelectedItem = papers.FirstOrDefault(p => p.Id == paperId) ?? papers.FirstOrDefault();
            _classes = s.Roster.ListClasses().Where(c => c.StudentCount > 0)
                .Select(c => new ClassOption { Id = c.Id, Name = c.Name, Count = c.StudentCount }).ToList();
            if (_classes.Count == 1) _classes[0].Selected = true;
            ClassList.ItemsSource = _classes;
        }

        public long CreatedExamId { get; private set; }

        public bool OpenNow { get; private set; }

        private void OnPaperChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(PaperBox.SelectedItem is Paper paper)) return;
            // 未手动修改过名称时，跟随试卷名
            if (string.IsNullOrWhiteSpace(TitleBox.Text) || TitleBox.Text == _autoTitle)
            {
                _autoTitle = paper.Name;
                TitleBox.Text = paper.Name;
            }
        }

        private void OnSelectAllClasses(object sender, RoutedEventArgs e)
        {
            var all = _classes.All(c => c.Selected);
            foreach (var c in _classes) c.Selected = !all;
        }

        private void OnCreate(object sender, RoutedEventArgs e) => Create(false);

        private void OnCreateAndOpen(object sender, RoutedEventArgs e) => Create(true);

        private void Create(bool open)
        {
            ErrorText.Text = "";
            if (!(PaperBox.SelectedItem is Paper paper))
            {
                ErrorText.Text = "请选择试卷";
                return;
            }
            var classIds = _classes.Where(c => c.Selected).Select(c => c.Id).ToList();
            if (classIds.Count == 0)
            {
                ErrorText.Text = "请至少选择一个班级";
                return;
            }
            var settings = new ExamSettings
            {
                DurationMinutes = (int)Math.Max(1, Math.Round(DurationBox.Value)),
                MultipleMode = ModeAll.IsChecked == true ? MultipleScoringMode.AllOrNothing
                    : ModeProportional.IsChecked == true ? MultipleScoringMode.Proportional : MultipleScoringMode.HalfForPartial,
                AntiCheat = AntiCheatBox.IsChecked == true,
                ShuffleQuestions = ShuffleQuestionsBox.IsChecked == true,
                ShuffleOptions = ShuffleOptionsBox.IsChecked == true,
                ShowScoreAfterSubmit = ShowScoreBox.IsChecked == true,
            };
            try
            {
                CreatedExamId = AppServices.Current.PaperService.CreateExam(paper, TitleBox.Text, classIds, settings, out var missing);
                if (missing > 0) Ui.Info($"试卷中有 {missing} 道题已从题库删除，未包含在考试中。");
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
                return;
            }
            OpenNow = open;
            DialogResult = true;
        }
    }
}
