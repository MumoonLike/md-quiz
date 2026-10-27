using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MdQuiz.Core.Client;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
using MdQuiz.Server.Services;
using Xunit;

namespace MdQuiz.Tests
{
    internal sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Dir = Path.Combine(Path.GetTempPath(), "mdquiz-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Db = new Database(Path.Combine(Dir, "test.db"));
            Db.Initialize();
            Questions = new QuestionRepository(Db);
            Resources = new ResourceRepository(Db);
            Papers = new PaperRepository(Db);
            Roster = new RosterRepository(Db);
            Exams = new ExamRepository(Db);
            Settings = new SettingsRepository(Db);
            PaperService = new PaperService(Questions, Exams);
            Results = new ResultService(Exams);
        }

        public string Dir { get; }
        public Database Db { get; }
        public QuestionRepository Questions { get; }
        public ResourceRepository Resources { get; }
        public PaperRepository Papers { get; }
        public RosterRepository Roster { get; }
        public ExamRepository Exams { get; }
        public SettingsRepository Settings { get; }
        public PaperService PaperService { get; }
        public ResultService Results { get; }

        public void Dispose()
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(Dir, true); } catch (Exception) { }
        }

        public static int FreeUdpPort()
        {
            using (var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) return ((IPEndPoint)u.Client.LocalEndPoint).Port;
        }
    }

    internal static class TaskExt
    {
        public static async Task<T> Within<T>(this Task<T> task, int ms = 5000)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new TimeoutException("timed out");
            return await task;
        }

        public static async Task Until(Func<bool> condition, int ms = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
                await Task.Delay(20);
            }
        }
    }

    public class RepositoryTests
    {
        [Fact]
        public void QuestionsCrudAndQuery()
        {
            using var env = new TestEnv();
            var parsed = QuestionMarkdown.Parse(File.ReadAllText(MarkdownParserTests.SamplePath));
            Assert.Equal(10, env.Questions.InsertMany(parsed.Questions.Select(q => q.Question)));
            Assert.Equal(10, env.Questions.Count());
            Assert.Equal(5, env.Questions.Query(new QuestionQuery { Tag = "信息技术" }).Count);
            Assert.Equal(2, env.Questions.Query(new QuestionQuery { Tag = "网络" }).Count);
            Assert.Equal(3, env.Questions.Query(new QuestionQuery { Type = QuestionType.Single }).Count);
            Assert.Single(env.Questions.Query(new QuestionQuery { Keyword = "三次握手" }));
            Assert.Contains("Python", env.Questions.AllTags());
            Assert.Equal(2, env.Questions.FindIds(QuestionType.Single, "数学").Count);

            var q = env.Questions.Query(new QuestionQuery { Keyword = "质数" }).Single();
            q.Tags.Add("新标签");
            q.Options[0] = "四";
            env.Questions.Update(q);
            var again = env.Questions.Get(q.Id);
            Assert.Equal("四", again.Options[0]);
            Assert.Contains("新标签", again.Tags);
            env.Questions.Delete(new[] { q.Id });
            Assert.Null(env.Questions.Get(q.Id));
        }

        [Fact]
        public void RosterImportUpsertsAndReportsErrors()
        {
            using var env = new TestEnv();
            var classId = env.Roster.AddClass("一班");
            env.Roster.AddStudent(classId, "001", "张三");
            Assert.Throws<InvalidOperationException>(() => env.Roster.AddStudent(classId, "001", "重复"));
            var report = env.Roster.Import(new[]
            {
                new RosterRow { StudentNo = "001", Name = "张三丰", Line = 2 },
                new RosterRow { StudentNo = "002", Name = "李四", ClassName = "二班", Line = 3 },
                new RosterRow { StudentNo = "002", Name = "李四重复", ClassName = "二班", Line = 4 },
                new RosterRow { StudentNo = "", Name = "无学号", Line = 5 },
            }, classId);
            Assert.Equal(1, report.Added);
            Assert.Equal(1, report.Updated);
            Assert.Equal(2, report.Errors.Count);
            var classes = env.Roster.ListClasses();
            Assert.Equal(new[] { "一班", "二班" }, classes.Select(c => c.Name).OrderBy(n => n));
            Assert.Equal("张三丰", env.Roster.ListStudents(classId).Single().Name);
        }

        [Fact]
        public void ExcelRoundTrips()
        {
            using var env = new TestEnv();
            var roster = Path.Combine(env.Dir, "roster.xlsx");
            ExcelService.WriteRosterTemplate(roster);
            var rows = ExcelService.ReadRoster(roster);
            Assert.Equal(3, rows.Count);
            Assert.Equal("20240101", rows[0].StudentNo);
            Assert.Equal("高一(2)班", rows[2].ClassName);

            var questions = Path.Combine(env.Dir, "q.xlsx");
            ExcelService.WriteQuestionTemplate(questions);
            var parsed = ExcelService.ReadQuestions(questions);
            Assert.Equal(5, parsed.Questions.Count);
            Assert.All(parsed.Questions, q => Assert.True(q.IsValid, $"row {q.Line}: {q.Error}"));
            Assert.Equal(new[] { "北京|北京市", "上海|上海市" }, parsed.Questions[3].Question.Blanks);
            Assert.Equal("AB", parsed.Questions[1].Question.Answer);

            var csv = Path.Combine(env.Dir, "roster.csv");
            File.WriteAllText(csv, "学号,姓名,班级\n1001,赵六,三班\n");
            Assert.Equal("赵六", ExcelService.ReadRoster(csv).Single().Name);
        }

        [Fact]
        public void PasswordHashMatchesPbkdf2Sha256()
        {
            var stored = PasswordHash.Create("秘密123");
            Assert.True(PasswordHash.Verify("秘密123", stored));
            Assert.False(PasswordHash.Verify("秘密124", stored));
            var parts = stored.Split('$');
            var expected = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes("秘密123"), Convert.FromBase64String(parts[2]), int.Parse(parts[1]), HashAlgorithmName.SHA256, 32);
            Assert.Equal(Convert.ToBase64String(expected), parts[3]);

            using var env = new TestEnv();
            Assert.False(env.Settings.HasPassword);
            env.Settings.SetPassword("abc");
            Assert.True(env.Settings.VerifyPassword("abc"));
            Assert.False(env.Settings.VerifyPassword("abd"));
        }

        [Fact]
        public void RandomDrawRespectsRulesAndExclusions()
        {
            using var env = new TestEnv();
            env.Questions.InsertMany(QuestionMarkdown.Parse(File.ReadAllText(MarkdownParserTests.SamplePath)).Questions.Select(q => q.Question));
            var singles = env.Questions.FindIds(QuestionType.Single, null);
            var result = env.PaperService.Draw(new[]
            {
                new RandomDrawRule { Type = QuestionType.Single, Count = 2, ScoreEach = 3 },
                new RandomDrawRule { Type = QuestionType.Multiple, Tag = "地理", Count = 5, ScoreEach = 4 },
            }, new HashSet<long> { singles[0] });
            Assert.Equal(3, result.Items.Count);
            Assert.DoesNotContain(singles[0], result.Items.Select(i => i.QuestionId));
            Assert.Single(result.Warnings);
            Assert.Equal(new double[] { 3, 3, 4 }, result.Items.Select(i => i.Score));
        }
    }

    public class ExamFlowTests
    {
        private static (TestEnv env, long examId, long classId, Exam exam) Setup(ExamSettings settings)
        {
            var env = new TestEnv();
            var classId = env.Roster.AddClass("一班");
            env.Roster.AddStudent(classId, "001", "张三");
            env.Roster.AddStudent(classId, "002", "李四");
            env.Roster.AddStudent(classId, "003", "王五");
            var parsed = QuestionMarkdown.Parse(File.ReadAllText(MarkdownParserTests.SamplePath)).Questions;
            env.Questions.InsertMany(parsed.Select(p => p.Question));
            var paper = new Paper { Name = "测试卷", Items = parsed.Select(p => new PaperItem { QuestionId = p.Question.Id, Score = p.Score ?? 2 }).ToList() };
            env.Papers.Save(paper);
            var examId = env.PaperService.CreateExam(paper, "期中测试", new[] { classId }, settings, out var missing);
            Assert.Equal(0, missing);
            return (env, examId, classId, env.Exams.Get(examId));
        }

        private static List<string> CorrectValues(ExamQuestion q)
        {
            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                    return q.Answer.Select(c => c.ToString()).ToList();
                case QuestionType.TrueFalse:
                    return new List<string> { q.Answer };
                case QuestionType.FillBlank:
                    return q.Blanks.Select(b => Question.BlankAlternatives(b).Last()).ToList();
                default:
                    return new List<string> { "三次握手：SYN、SYN+ACK、ACK" };
            }
        }

        [Fact]
        public async Task FullExamFlowWithReconnectAndForcedSubmit()
        {
            var (env, examId, _, exam) = Setup(new ExamSettings { DurationMinutes = 30, ShuffleOptions = true, ShuffleQuestions = true, MultipleMode = MultipleScoringMode.HalfForPartial });
            using (env)
            using (var host = new ExamHost(env.Db) { ServerName = "教师机" })
            {
                var udp = TestEnv.FreeUdpPort();
                host.Start(0, udp);
                host.OpenExam(examId);
                var port = host.Port;

                // 局域网发现（本机回环）
                var found = await Discovery.SearchAsync(udp, 800);
                Assert.Contains(found, f => f.Port == port && f.ServerName == "教师机" && f.ExamTitle == "期中测试");

                using var c1 = new ExamClient(Path.Combine(env.Dir, "c1"), "PC-01");
                var welcome = await c1.ConnectAsync("127.0.0.1", port).Within();
                Assert.Equal(ExamStatus.Open, welcome.Exam.Status);
                Assert.False((await c1.IdentifyAsync("999")).Ok);
                var who = await c1.IdentifyAsync("001");
                Assert.Equal("张三", who.Name);
                var login = await c1.LoginAsync("001");
                Assert.True(login.Ok, login.Error);
                Assert.Equal(ParticipantStatus.Waiting, login.Status);

                // 同一学号不能在第二台电脑同时登录
                using (var dup = new ExamClient(Path.Combine(env.Dir, "dup"), "PC-99"))
                {
                    await dup.ConnectAsync("127.0.0.1", port);
                    var dupLogin = await dup.LoginAsync("001");
                    Assert.False(dupLogin.Ok);
                    Assert.Contains("PC-01", dupLogin.Error);
                }

                using var c2 = new ExamClient(Path.Combine(env.Dir, "c2"), "PC-02");
                await c2.ConnectAsync("127.0.0.1", port);
                Assert.True((await c2.LoginAsync("002")).Ok);

                var paper1 = new TaskCompletionSource<StudentPaper>();
                c1.PaperReceived += p => paper1.TrySetResult(p);
                host.StartExam();
                var sp = await paper1.Task.Within();
                Assert.Equal(exam.Questions.Count, sp.Questions.Count);
                Assert.True(c1.DeadlineUtc > DateTime.UtcNow.AddMinutes(29));
                await TaskExt.Until(() => c2.Paper != null);

                // 作答：除最后一道题外全部答对
                var byId = exam.Questions.ToDictionary(q => q.Id);
                var last = sp.Questions.Last();
                foreach (var q in sp.Questions.Where(q => q != last)) c1.SetAnswer(q.Id, CorrectValues(byId[q.Id]));
                await TaskExt.Until(() => c1.PendingSyncCount == 0);
                Assert.Equal(sp.Questions.Count - 1, host.GetParticipants().Single(p => p.StudentNo == "001").Answered);

                // 教师机断网重启：客户端自动重连，离线期间的作答在重连后同步
                var lostStates = new List<ConnectionState>();
                c1.StateChanged += s => lostStates.Add(s);
                host.Stop();
                await TaskExt.Until(() => c1.State == ConnectionState.Reconnecting);
                c1.SetAnswer(last.Id, CorrectValues(byId[last.Id]));
                Assert.Equal(1, c1.PendingSyncCount);
                host.Start(port, udp);
                await TaskExt.Until(() => c1.State == ConnectionState.Connected && c1.PendingSyncCount == 0, 10000);
                Assert.Equal(sp.Questions.Count, host.GetParticipants().Single(p => p.StudentNo == "001").Answered);

                // 切屏上报
                c1.ReportFocusLost();
                c1.ReportFocusLost();
                await TaskExt.Until(() => host.GetParticipants().Single(p => p.StudentNo == "001").FocusLost == 2);

                // 交卷：客观题全对
                var result = await c1.SubmitAsync().Within();
                Assert.True(result.Ok, result.Error);
                Assert.True(result.ShowScore);
                Assert.Equal(result.ObjectiveTotal, result.ObjectiveScore);
                Assert.True(result.HasPendingManual);
                Assert.True(c1.IsSubmitted);

                // 时间到前老师结束考试：李四被强制收卷
                var forced = new TaskCompletionSource<MdQuiz.Core.Protocol.SubmitResult>();
                c2.Submitted += r => forced.TrySetResult(r);
                c2.SetAnswer(sp.Questions[0].Id, new List<string> { "Z" });
                await TaskExt.Until(() => c2.PendingSyncCount == 0);
                host.EndExam();
                var r2 = await forced.Task.Within();
                Assert.True(r2.Forced);

                var participants = env.Exams.ListParticipants(examId).ToDictionary(p => p.StudentNo);
                Assert.Equal(ParticipantStatus.Submitted, participants["001"].Status);
                Assert.Equal(2, participants["001"].FocusLost);
                Assert.Equal(1, participants["001"].PendingManual);
                Assert.True(participants["002"].Forced);
                Assert.Equal(0, participants["002"].ObjectiveScore);
                Assert.Equal(ParticipantStatus.Absent, participants["003"].Status);

                // 人工批改简答题
                var shortQ = exam.Questions.Single(q => q.Type == QuestionType.ShortAnswer);
                env.Results.SetManualScore(exam, "001", shortQ.Id, 8);
                var p1 = env.Exams.GetParticipant(examId, "001");
                Assert.Equal(0, p1.PendingManual);
                Assert.Equal(p1.ObjectiveScore + 8, p1.TotalScore);
                Assert.Throws<ArgumentException>(() => env.Results.SetManualScore(exam, "001", shortQ.Id, 11));

                // 导出成绩
                var xlsx = Path.Combine(env.Dir, "成绩.xlsx");
                ExcelService.ExportResults(xlsx, env.Results.BuildTable(exam));
                Assert.True(new FileInfo(xlsx).Length > 0);

                Assert.Equal(ExamStatus.Ended, env.Exams.Get(examId).Status);
                host.CloseExam();
                Assert.Null(host.CurrentExam);
            }
        }

        [Fact]
        public async Task TeacherCanReleaseStudentToAnotherMachine()
        {
            var (env, examId, _, _) = Setup(new ExamSettings());
            using (env)
            using (var host = new ExamHost(env.Db))
            {
                host.Start(0, TestEnv.FreeUdpPort());
                host.OpenExam(examId);
                using var a = new ExamClient(Path.Combine(env.Dir, "a"), "PC-A");
                await a.ConnectAsync("127.0.0.1", host.Port);
                Assert.True((await a.LoginAsync("003")).Ok);
                var lost = new TaskCompletionSource<string>();
                a.SessionLost += r => lost.TrySetResult(r);

                using var b = new ExamClient(Path.Combine(env.Dir, "b"), "PC-B");
                await b.ConnectAsync("127.0.0.1", host.Port);
                Assert.False((await b.LoginAsync("003")).Ok);
                host.ReleaseStudent("003");
                Assert.Contains("其他电脑", await lost.Task.Within());
                Assert.True((await b.LoginAsync("003")).Ok);
            }
        }

        [Fact]
        public async Task ExamAutoEndsAtDeadlineAndResumesAfterTeacherRestart()
        {
            var (env, examId, _, _) = Setup(new ExamSettings { DurationMinutes = 1 });
            using (env)
            {
                var host = new ExamHost(env.Db);
                host.Start(0, TestEnv.FreeUdpPort());
                host.OpenExam(examId);
                host.StartExam();
                host.Stop();

                // 教师端重启后恢复进行中的考试
                var active = env.Exams.FindActive();
                Assert.Equal(examId, active.Id);
                using var host2 = new ExamHost(env.Db);
                host2.Start(0, TestEnv.FreeUdpPort());
                host2.OpenExam(active.Id);
                Assert.Equal(ExamStatus.Running, host2.CurrentExam.Status);

                using var c = new ExamClient(Path.Combine(env.Dir, "c"), "PC");
                await c.ConnectAsync("127.0.0.1", host2.Port);
                var login = await c.LoginAsync("001");
                Assert.True(login.Ok);
                Assert.Equal(ParticipantStatus.Answering, login.Status);
                await TaskExt.Until(() => c.Paper != null);

                // 把开考时间往前拨，模拟时间已到
                var exam = host2.CurrentExam;
                exam.StartedAt = DateTime.UtcNow.AddMinutes(-2);
                var submitted = new TaskCompletionSource<MdQuiz.Core.Protocol.SubmitResult>();
                c.Submitted += r => submitted.TrySetResult(r);
                Assert.True((await submitted.Task.Within(5000)).Forced);
                Assert.Equal(ExamStatus.Ended, env.Exams.Get(examId).Status);
            }
        }
    }
}
