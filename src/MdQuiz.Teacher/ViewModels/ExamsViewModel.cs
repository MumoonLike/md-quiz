using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.ViewModels
{
    public partial class ExamsViewModel : PageViewModel
    {
        public ObservableCollection<ExamSummary> Exams { get; } = new ObservableCollection<ExamSummary>();

        [ObservableProperty] private bool _isEmpty;

        public override void OnActivated() => Refresh();

        public void Refresh()
        {
            Exams.Clear();
            foreach (var e in S.Exams.List()) Exams.Add(e);
            IsEmpty = Exams.Count == 0;
        }

        [RelayCommand]
        private void Create()
        {
            if (!S.Papers.List().Any())
            {
                Ui.Info("还没有试卷。请先在“试卷”页面组卷，再创建考试。");
                Shell.Navigate("papers");
                return;
            }
            if (!S.Roster.ListClasses().Any(c => c.StudentCount > 0))
            {
                Ui.Info("还没有学生名单。请先在“学生”页面导入花名册，再创建考试。");
                Shell.Navigate("students");
                return;
            }
            var dialog = new ExamEditorWindow();
            if (Ui.ShowDialog(dialog) != true) return;
            Refresh();
            if (dialog.OpenNow)
            {
                Open(Exams.FirstOrDefault(e => e.Id == dialog.CreatedExamId));
            }
            else
            {
                Ui.Toast("考试已创建");
            }
        }

        [RelayCommand]
        private void Open(ExamSummary exam)
        {
            if (exam == null) return;
            var current = S.Host.CurrentExam;
            if (current != null && current.Id == exam.Id)
            {
                Shell.Navigate("monitor");
                return;
            }
            if (current != null && current.Status != ExamStatus.Ended)
            {
                Ui.Info($"考试“{current.Title}”还在进行中，请先在监考页面结束或取消它。");
                Shell.Navigate("monitor");
                return;
            }
            S.Host.OpenExam(exam.Id);
            Shell.Navigate("monitor");
            Ui.Toast($"“{exam.Title}”已开放登录，学生现在可以登录候考");
        }

        [RelayCommand]
        private void Delete(ExamSummary exam)
        {
            if (exam == null) return;
            var current = S.Host.CurrentExam;
            if (current != null && current.Id == exam.Id)
            {
                Ui.Info("这场考试正在监考页面中，请先关闭后再删除。");
                return;
            }
            var extra = exam.SubmittedCount > 0 ? $"\n\n已有 {exam.SubmittedCount} 名学生的答卷和成绩，删除后无法恢复！" : "";
            if (!Ui.ConfirmDanger($"确定删除考试“{exam.Title}”吗？{extra}", "删除考试")) return;
            S.Exams.Delete(exam.Id);
            Refresh();
        }

        [RelayCommand]
        private void Results(ExamSummary exam)
        {
            if (exam == null) return;
            Shell.Navigate("results");
            Shell.Results.Select(exam.Id);
        }
    }
}
