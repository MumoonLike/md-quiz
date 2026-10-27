using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Server.Services;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Converters;
using Microsoft.Win32;

namespace MdQuiz.Teacher.ViewModels
{
    public class ResultRowItem
    {
        public Participant P { get; set; }
        public string StudentNo => P.StudentNo;
        public string Name => P.Name;
        public string ClassName => P.ClassName;
        public string StatusText
        {
            get
            {
                switch (P.Status)
                {
                    case ParticipantStatus.Submitted: return P.PendingManual > 0 ? $"待批改 {P.PendingManual} 题" : (P.Forced ? "已收卷" : "已交卷");
                    case ParticipantStatus.Absent: return "缺考";
                    default: return "未交卷";
                }
            }
        }
        public System.Windows.Media.Color StatusColor =>
            P.Status == ParticipantStatus.Absent ? Palette.Gray
            : P.Status != ParticipantStatus.Submitted ? Palette.Amber
            : P.PendingManual > 0 ? Palette.Violet : Palette.Emerald;
        public string Objective => ScoreConverter.Format(P.ObjectiveScore);
        public string Subjective => ScoreConverter.Format(P.SubjectiveScore);
        public string Total => ScoreConverter.Format(P.TotalScore);
        public int FocusLost => P.FocusLost;
        public DateTime? SubmittedAt => P.SubmittedAt;
    }

    public class Bucket
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public double Percent { get; set; }
    }

    public partial class ResultsViewModel : PageViewModel
    {
        public ObservableCollection<ExamSummary> Exams { get; } = new ObservableCollection<ExamSummary>();
        public ObservableCollection<ResultRowItem> Rows { get; } = new ObservableCollection<ResultRowItem>();
        public ObservableCollection<Bucket> Buckets { get; } = new ObservableCollection<Bucket>();

        [ObservableProperty] private ExamSummary _selectedExam;
        [ObservableProperty] private bool _hasExam;
        [ObservableProperty] private string _expected = "0";
        [ObservableProperty] private string _attended = "0";
        [ObservableProperty] private string _average = "—";
        [ObservableProperty] private string _highest = "—";
        [ObservableProperty] private string _lowest = "—";
        [ObservableProperty] private int _pending;
        [ObservableProperty] private string _fullScore = "";

        partial void OnSelectedExamChanged(ExamSummary value) => LoadRows();

        public override void OnActivated()
        {
            var keep = SelectedExam?.Id;
            Exams.Clear();
            foreach (var e in S.Exams.List().Where(e => e.Status == ExamStatus.Ended || e.Status == ExamStatus.Running)) Exams.Add(e);
            SelectedExam = Exams.FirstOrDefault(e => e.Id == keep) ?? Exams.FirstOrDefault();
            if (SelectedExam == null) LoadRows();
        }

        public void Select(long examId)
        {
            OnActivated();
            var target = Exams.FirstOrDefault(e => e.Id == examId);
            if (target != null) SelectedExam = target;
        }

        private void LoadRows()
        {
            Rows.Clear();
            Buckets.Clear();
            HasExam = SelectedExam != null;
            if (SelectedExam == null) return;
            var participants = S.Exams.ListParticipants(SelectedExam.Id);
            foreach (var p in participants.OrderByDescending(p => p.TotalScore ?? -1).ThenBy(p => p.StudentNo)) Rows.Add(new ResultRowItem { P = p });

            var scored = participants.Where(p => p.Status == ParticipantStatus.Submitted && p.TotalScore.HasValue).Select(p => p.TotalScore.Value).ToList();
            Expected = participants.Count.ToString();
            Attended = participants.Count(p => p.Status != ParticipantStatus.Absent).ToString();
            Average = scored.Count == 0 ? "—" : ScoreConverter.Format(Math.Round(scored.Average(), 1));
            Highest = scored.Count == 0 ? "—" : ScoreConverter.Format(scored.Max());
            Lowest = scored.Count == 0 ? "—" : ScoreConverter.Format(scored.Min());
            Pending = participants.Count(p => p.PendingManual > 0);
            FullScore = "满分 " + ScoreConverter.Format(SelectedExam.TotalScore);

            // 分数段分布（按满分的百分比）
            var full = SelectedExam.TotalScore <= 0 ? 100 : SelectedExam.TotalScore;
            var labels = new[] { "< 60%", "60–70%", "70–80%", "80–90%", "≥ 90%" };
            var counts = new int[5];
            foreach (var s in scored)
            {
                var r = s / full;
                var i = r < 0.6 ? 0 : r < 0.7 ? 1 : r < 0.8 ? 2 : r < 0.9 ? 3 : 4;
                counts[i]++;
            }
            var max = Math.Max(1, counts.Max());
            for (var i = 0; i < 5; i++) Buckets.Add(new Bucket { Label = labels[i], Count = counts[i], Percent = 100.0 * counts[i] / max });
        }

        [RelayCommand]
        private void Refresh() => LoadRows();

        [RelayCommand]
        private void Grade()
        {
            if (SelectedExam == null) return;
            var exam = S.Exams.Get(SelectedExam.Id);
            var window = new GradingWindow(exam);
            Ui.ShowDialog(window);
            var keep = SelectedExam.Id;
            OnActivated();
            SelectedExam = Exams.FirstOrDefault(e => e.Id == keep);
        }

        [RelayCommand]
        private void Export()
        {
            if (SelectedExam == null) return;
            var dlg = new SaveFileDialog { Filter = "Excel 文件|*.xlsx", FileName = $"{SafeName(SelectedExam.Title)}_成绩.xlsx" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var exam = S.Exams.Get(SelectedExam.Id);
                ExcelService.ExportResults(dlg.FileName, S.Results.BuildTable(exam));
                Ui.Toast("成绩已导出");
            }
            catch (Exception ex)
            {
                Ui.Error("导出失败：" + ex.Message + "\n\n如果同名文件已在 Excel 中打开，请先关闭。");
            }
        }

        private static string SafeName(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
