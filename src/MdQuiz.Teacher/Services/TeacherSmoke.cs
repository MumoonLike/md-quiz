using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MdQuiz.Core.Models;
using MdQuiz.Server.Services;
using MdQuiz.Teacher.ViewModels;
using MdQuiz.Teacher.Views;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.Services
{
    /// <summary>
    /// 教师端界面冒烟测试（MdQuiz.Teacher.exe --smoke 目录）：
    /// 使用独立的临时数据目录，导入示例数据，逐页、逐个对话框截图，
    /// 并与学生端冒烟测试配合在本机完成一场真实考试。
    /// </summary>
    public static class TeacherSmoke
    {
        public static string PrepareDataDir()
        {
            var dir = Path.Combine(SmokeKit.OutDir, "teacher-data");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            return dir;
        }

        public static async Task RunAsync(AppServices s)
        {
            var code = 1;
            try
            {
                await RunCore(s);
                code = SmokeKit.ExitCode();
            }
            catch (Exception ex)
            {
                SmokeKit.Error("teacher smoke failed", ex);
                SmokeKit.ExitCode();
            }
            finally
            {
                Application.Current.Shutdown(code);
            }
        }

        private static async Task Show(Window owner, Window dialog, string name)
        {
            SmokeKit.Step("open " + name);
            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            dialog.Show();
            await SmokeKit.Settle(900);
            SmokeKit.Capture(dialog, name);
            dialog.Close();
        }

        private static async Task Page(MainWindow main, string key, string name)
        {
            SmokeKit.Step("navigate " + key);
            MainViewModel.Instance.Navigate(key);
            await SmokeKit.Settle(900);
            SmokeKit.Capture(main, name);
        }

        private static async Task RunCore(AppServices s)
        {
            // 登录窗口：首次使用和正常两种状态
            await Show(null, new LoginWindow(s), "teacher-login-first-run");
            s.Settings.SetPassword("smoke-test");
            await Show(null, new LoginWindow(s), "teacher-login");

            var main = new MainWindow(s)
            {
                WindowState = WindowState.Normal,
                Width = 1366,
                Height = 820,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            Application.Current.MainWindow = main;
            main.Show();
            await SmokeKit.Settle(1500);
            var shell = MainViewModel.Instance;
            if (!shell.ServerRunning) SmokeKit.Error("exam server is not running");
            SmokeKit.Capture(main, "teacher-bank-empty");

            // ---------- 准备数据 ----------
            var samples = Path.Combine(AppPaths.ExeDir, "samples");
            var parsed = s.Importer.ImportMarkdownFile(Path.Combine(samples, "sample-questions.md"));
            await Show(main, new ImportPreviewWindow(parsed, "sample-questions"), "teacher-import-preview");
            var valid = parsed.Questions.Where(q => q.IsValid).ToList();
            if (valid.Count != parsed.Questions.Count || valid.Count == 0) SmokeKit.Error($"sample import: {valid.Count}/{parsed.Questions.Count} valid");
            s.Questions.InsertMany(valid.Select(q => q.Question));
            var report = s.Roster.Import(ExcelService.ReadRoster(Path.Combine(samples, "roster-sample.csv")), null);
            SmokeKit.Step($"roster import: added {report.Added}, errors {report.Errors.Count}");
            var paper = new Paper
            {
                Name = "冒烟测试试卷",
                GroupId = s.Papers.AddGroup("冒烟测试分组"),
                Items = valid.Select(q => new PaperItem { QuestionId = q.Question.Id, Score = q.Score ?? 2 }).ToList(),
            };
            s.Papers.Save(paper);
            var classIds = s.Roster.ListClasses().Select(c => c.Id).ToList();
            var examId = s.PaperService.CreateExam(paper, "冒烟测试考试", classIds, new ExamSettings
            {
                DurationMinutes = 30,
                AntiCheat = true,
                ShuffleOptions = true,
                ShowScoreAfterSubmit = true,
            }, out _);

            // ---------- 各页面 ----------
            await Page(main, "bank", "teacher-bank");
            await Page(main, "papers", "teacher-papers");
            await Page(main, "students", "teacher-students");
            await Page(main, "exams", "teacher-exams");
            await Page(main, "results", "teacher-results-empty");
            await Page(main, "settings", "teacher-settings");
            await Page(main, "monitor", "teacher-monitor-empty");

            // ---------- 对话框 ----------
            var questions = s.Questions.Query();
            await Show(main, new QuestionEditorWindow(questions.First(q => q.Type == QuestionType.Single)), "teacher-editor-single");
            await Show(main, new QuestionEditorWindow(questions.First(q => q.Type == QuestionType.FillBlank)), "teacher-editor-fill");
            await Show(main, new QuestionEditorWindow(null), "teacher-editor-new");
            await Show(main, new ExamEditorWindow(paper.Id), "teacher-exam-editor");
            await Show(main, new QuestionPickerWindow(new HashSet<long>()), "teacher-question-picker");
            await Show(main, new RandomDrawWindow(new HashSet<long>()), "teacher-random-draw");
            var byId = questions.ToDictionary(q => q.Id);
            await Show(main, new PaperPreviewWindow(paper.Name, paper.Items.Select(i => (byId[i.QuestionId], i.Score)).ToList()), "teacher-paper-preview");

            // ---------- 与学生端一起考试 ----------
            s.Host.OpenExam(examId);
            await Page(main, "monitor", "teacher-monitor-open");
            SmokeKit.Step("waiting for a student to log in");
            await SmokeKit.WaitUntil(() => s.Host.GetParticipants().Any(p => p.Online), 240000, "student login");
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "teacher-monitor-waiting");

            s.Host.StartExam();
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "teacher-monitor-started");
            await SmokeKit.WaitUntil(() => s.Host.GetParticipants().Any(p => p.Answered >= 3), 180000, "student answers");
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "teacher-monitor-running");
            await SmokeKit.WaitUntil(() => s.Host.GetParticipants().Any(p => p.Status == ParticipantStatus.Submitted), 240000, "student submit");
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "teacher-monitor-submitted");

            if (s.Host.CurrentExam?.Status == ExamStatus.Running) s.Host.EndExam();
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "teacher-monitor-ended");

            shell.Navigate("results");
            shell.Results.Select(examId);
            await SmokeKit.Settle(900);
            SmokeKit.Capture(main, "teacher-results");
            var exam = s.Exams.Get(examId);
            await Show(main, new GradingWindow(exam), "teacher-grading");
            ExcelService.ExportResults(Path.Combine(SmokeKit.OutDir, "results.xlsx"), s.Results.BuildTable(exam));

            // 最小窗口尺寸下的布局
            main.Width = main.MinWidth;
            main.Height = main.MinHeight;
            await Page(main, "monitor", "teacher-monitor-min-size");
            await Page(main, "papers", "teacher-papers-min-size");
            await Page(main, "bank", "teacher-bank-min-size");
        }
    }
}
