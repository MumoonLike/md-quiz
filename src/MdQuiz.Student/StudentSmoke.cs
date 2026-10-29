using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Student.ViewModels;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Controls;

namespace MdQuiz.Student
{
    /// <summary>
    /// 学生端界面冒烟测试（MdQuiz.Student.exe --smoke 目录 [--server 地址:端口] [--student 学号]）：
    /// 连接本机教师端，登录、作答各种题型、交卷，每个界面截图。
    /// </summary>
    public static class StudentSmoke
    {
        /// <summary>冒烟测试时上报给教师端的电脑名（截图也用于用户手册）。</summary>
        public const string MachineName = "LAB302-PC03";

        public static async Task RunAsync(Window main, ShellViewModel vm, string[] args)
        {
            var code = 1;
            try
            {
                await RunCore(main, vm, args);
                code = SmokeKit.ExitCode();
            }
            catch (Exception ex)
            {
                SmokeKit.Error("student smoke failed", ex);
                SmokeKit.ExitCode();
            }
            finally
            {
                Application.Current.Shutdown(code);
            }
        }

        private static async Task RunCore(Window main, ShellViewModel vm, string[] args)
        {
            var server = SmokeKit.Arg(args, "--server") ?? "127.0.0.1:" + Discovery.DefaultTcpPort;
            var studentNo = SmokeKit.Arg(args, "--student") ?? "20240101";
            var host = server.Split(':')[0];
            var port = server.Contains(":") ? int.Parse(server.Split(':')[1]) : Discovery.DefaultTcpPort;

            await SmokeKit.Settle(2500);
            SmokeKit.Capture(main, "student-connect");

            // 连接教师机（自动发现可能已经连上）
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (vm.Screen == Screen.Connect && DateTime.UtcNow < deadline)
            {
                if (!await vm.ConnectToAsync(host, port)) await Task.Delay(2000);
            }
            if (vm.Screen == Screen.Connect)
            {
                SmokeKit.Error("could not connect to teacher");
                return;
            }
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "student-login-no-exam");
            await SmokeKit.WaitUntil(() => vm.LoginVm.HasExam, 240000, "exam opened by teacher");
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "student-login");

            vm.LoginVm.StudentNo = studentNo;
            await vm.LoginVm.NextCommand.ExecuteAsync(null);
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "student-confirm");
            if (!vm.LoginVm.Confirming)
            {
                SmokeKit.Error("identify failed: " + vm.LoginVm.Error);
                return;
            }
            await vm.LoginVm.LoginCommand.ExecuteAsync(null);
            await SmokeKit.WaitUntil(() => vm.Screen == Screen.Waiting || vm.Screen == Screen.Exam, 30000, "login");
            await SmokeKit.Settle(800);
            SmokeKit.Capture(main, "student-waiting");

            await SmokeKit.WaitUntil(() => vm.Screen == Screen.Exam, 240000, "exam started");
            await SmokeKit.Settle(1500);
            SmokeKit.Capture(main, "student-exam-start");

            // 依次作答，每种题型截一张图
            var exam = vm.ExamVm;
            var captured = new HashSet<QuestionType>();
            foreach (var q in exam.Questions.ToList())
            {
                exam.Current = q;
                await SmokeKit.Settle(300);
                switch (q.Type)
                {
                    case QuestionType.Single:
                    case QuestionType.TrueFalse:
                        // 通过界面上的选项按钮点击（与学生实际操作一致）
                        Click(main, q.Options[1]);
                        Click(main, q.Options[0]);
                        await SmokeKit.Settle(200);
                        if (q.GetValues().Count != 1 || q.GetValues()[0] != q.Options[0].Key)
                            SmokeKit.Error($"question {q.Number}: single choice selection is wrong: {string.Join(",", q.GetValues())}");
                        break;
                    case QuestionType.Multiple:
                        Click(main, q.Options[0]);
                        Click(main, q.Options[1]);
                        await SmokeKit.Settle(200);
                        if (q.GetValues().Count != 2)
                            SmokeKit.Error($"question {q.Number}: multiple choice kept {q.GetValues().Count} options, expected 2");
                        break;
                    case QuestionType.FillBlank:
                        foreach (var b in q.Blanks) b.Text = "北京";
                        break;
                    case QuestionType.ShortAnswer:
                        q.ShortText = "客户端先发送 SYN，服务器回复 SYN+ACK，客户端再回复 ACK，连接建立。\n这样双方都能确认对方的发送和接收能力正常。";
                        break;
                }
                if (q.Number == 2) q.IsFlagged = true;
                if (captured.Add(q.Type))
                {
                    await SmokeKit.Settle(700);
                    SmokeKit.Capture(main, "student-question-" + q.Type.ToString().ToLowerInvariant());
                }
            }
            exam.Current = exam.Questions.First();
            await SmokeKit.Settle(1200);
            await SmokeKit.WaitUntil(() => vm.Client.PendingSyncCount == 0, 20000, "answers synced");
            exam.UpdateSync();
            await SmokeKit.Settle(600);
            SmokeKit.Capture(main, "student-exam-answered");

            var result = await vm.Client.SubmitAsync();
            if (!result.Ok) SmokeKit.Error("submit failed: " + result.Error);
            await SmokeKit.WaitUntil(() => vm.Screen == Screen.Result, 20000, "result screen");
            await SmokeKit.Settle(1200);
            SmokeKit.Capture(main, "student-result");
        }

        /// <summary>找到界面上该选项对应的可见按钮，用自动化接口模拟点击。</summary>
        private static void Click(Window main, OptionVm option)
        {
            var button = FindVisual<ChoiceButton>(main, b => b.DataContext == option && b.IsVisible);
            if (button == null)
            {
                SmokeKit.Error("option button not found: " + option.Label);
                return;
            }
            if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        }

        private static T FindVisual<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && match(t)) return t;
                var found = FindVisual(child, match);
                if (found != null) return found;
            }
            return null;
        }
    }
}
