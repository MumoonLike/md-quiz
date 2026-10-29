using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using MdQuiz.Core.Models;
using MdQuiz.Server.Hosting;
using MdQuiz.Server.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MdQuiz.Tests
{
    public class McpTests
    {
        private static (TestEnv env, ExamHost host, McpServer mcp) Create(McpPermissions permissions = McpPermissions.Read)
        {
            var env = new TestEnv();
            var host = new ExamHost(env.Db);
            var tools = new McpToolset(env.Questions, env.Papers, env.Roster, env.Exams, env.Results, env.PaperService, host);
            var mcp = new McpServer(tools);
            mcp.Start(new McpOptions { Port = 0, Permissions = permissions });
            return (env, host, mcp);
        }

        private static JObject Request(string method, object @params = null, int id = 1)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (@params != null) o["params"] = JObject.FromObject(@params);
            return o;
        }

        /// <summary>调用工具，返回解析后的 JSON 结果；工具出错时返回 (null, 错误文本)。</summary>
        private static (JToken data, string error) Call(McpServer mcp, string tool, object args = null)
        {
            var response = (JObject)mcp.HandleMessage(Request("tools/call", new { name = tool, arguments = args ?? new { } }));
            Assert.Null(response["error"]);
            var result = response["result"];
            var text = (string)result["content"][0]["text"];
            return (bool)result["isError"] ? (null, text) : (JToken.Parse(text), null);
        }

        [Fact]
        public void InitializeListAndPermissions()
        {
            var (env, host, mcp) = Create();
            using (env)
            using (host)
            using (mcp)
            {
                var init = (JObject)mcp.HandleMessage(Request("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }));
                Assert.Equal("2025-03-26", (string)init["result"]["protocolVersion"]);
                Assert.Equal("mdquiz", (string)init["result"]["serverInfo"]["name"]);
                Assert.NotNull(init["result"]["capabilities"]["tools"]);
                var unknownVersion = (JObject)mcp.HandleMessage(Request("initialize", new { protocolVersion = "1999-01-01" }));
                Assert.Equal(McpServer.SupportedVersions[0], (string)unknownVersion["result"]["protocolVersion"]);

                Assert.Null(mcp.HandleMessage(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
                Assert.NotNull(((JObject)mcp.HandleMessage(Request("ping")))["result"]);
                Assert.Equal(-32601, (int)mcp.HandleMessage(Request("no/such"))["error"]["code"]);

                // 只读权限：看不到编辑和控制工具，调用时返回说明
                var names = mcp.HandleMessage(Request("tools/list"))["result"]["tools"].Select(t => (string)t["name"]).ToList();
                Assert.Contains("search_questions", names);
                Assert.Contains("get_student_answers", names);
                Assert.DoesNotContain("create_questions", names);
                Assert.DoesNotContain("start_exam", names);
                Assert.All(mcp.HandleMessage(Request("tools/list"))["result"]["tools"], t => Assert.Equal("object", (string)t["inputSchema"]["type"]));
                var (_, error) = Call(mcp, "create_questions", new { questions = new[] { new { type = "判断", stem = "1+1=2", answer = "对" } } });
                Assert.Contains("没有开放", error);
                Assert.Equal(0, env.Questions.Count());
                Assert.Equal(-32602, (int)mcp.HandleMessage(Request("tools/call", new { name = "nope" }))["error"]["code"]);

                // 批量请求
                var batch = (JArray)mcp.HandleMessage(new JArray(Request("ping", null, 1), Request("ping", null, 2)));
                Assert.Equal(2, batch.Count);

                mcp.UpdatePermissions(McpPermissions.Read | McpPermissions.Edit);
                names = mcp.HandleMessage(Request("tools/list"))["result"]["tools"].Select(t => (string)t["name"]).ToList();
                Assert.Contains("create_questions", names);
                Assert.DoesNotContain("end_exam", names);
            }
        }

        [Fact]
        public void EditToolsValidateAndCreate()
        {
            var (env, host, mcp) = Create(McpPermissions.Read | McpPermissions.Edit);
            using (env)
            using (host)
            using (mcp)
            {
                var (created, _) = Call(mcp, "create_questions", new
                {
                    questions = new object[]
                    {
                        new { type = "单选", stem = "2 是质数吗？哪个是质数", options = new[] { "4", "7", "9" }, answer = "B", tags = new[] { "数学" } },
                        new { type = "多选", stem = "传输层协议", options = new[] { "TCP", "UDP", "IP" }, answer = "A, B" },
                        new { type = "判断", stem = "地球是圆的", answer = "对" },
                        new { type = "填空", stem = "中国的首都是 ___", blanks = new[] { "北京|北京市" } },
                        new { type = "简答", stem = "简述 TCP 三次握手", answer = "SYN、SYN+ACK、ACK" },
                        new { type = "单选", stem = "缺答案", options = new[] { "a", "b" } },
                        new { type = "未知", stem = "x" },
                    },
                });
                Assert.Equal(5, created["created"].Count());
                Assert.Equal(new[] { 5, 6 }, created["errors"].Select(e => (int)e["index"]));
                var multi = env.Questions.Query(new MdQuiz.Server.Data.QuestionQuery { Type = QuestionType.Multiple }).Single();
                Assert.Equal("AB", multi.Answer);

                var (detail, _) = Call(mcp, "get_questions", new { ids = new[] { multi.Id, 9999 } });
                Assert.Equal("多选", (string)detail["items"][0]["type"]);
                Assert.Equal(9999, (long)detail["notFound"][0]);

                var (updated, err) = Call(mcp, "update_question", new { id = multi.Id, answer = "ABC", tags = new[] { "网络" } });
                Assert.Null(err);
                Assert.Equal("ABC", env.Questions.Get(multi.Id).Answer);
                Assert.Contains("超出", Call(mcp, "update_question", new { id = multi.Id, answer = "D" }).error);

                var md = "## [单选 3分] 1+1=?\n- [ ] 1\n- [x] 2\n\n## [判断] 太阳从西边升起\n答案: 错\n\n## [单选] 坏题\n- [ ] a\n";
                var (imported, _) = Call(mcp, "import_questions_markdown", new { markdown = md, extra_tags = new[] { "导入" }, paper_name = "导入卷", default_score = 5 });
                Assert.Equal(2, (int)imported["imported"]);
                Assert.Single(imported["errors"]);
                Assert.Equal(8, (double)imported["paper"]["totalScore"]); // 3 + 默认 5

                var (group, _) = Call(mcp, "create_paper_group", new { name = "测试分组" });
                var ids = env.Questions.Query().Select(q => q.Id).ToList();
                var (paper, _) = Call(mcp, "create_paper", new { name = "AI 组卷", group_id = (long)group["id"], items = ids.Select(id => new { question_id = id, score = 2 }) });
                Assert.Equal(ids.Count * 2, (double)paper["totalScore"]);
                Assert.Contains("不存在", Call(mcp, "create_paper", new { name = "x", items = new[] { new { question_id = 12345, score = 1 } } }).error);
                Assert.Contains("分组", Call(mcp, "create_paper", new { name = "x", group_id = 777, items = new object[0] }).error);

                var (papers, _) = Call(mcp, "list_papers", new { group_id = (long)group["id"] });
                Assert.Equal("AI 组卷", (string)papers["papers"].Single()["name"]);
                Assert.Equal("测试分组", (string)papers["papers"].Single()["group"]);

                var (random, _) = Call(mcp, "random_paper", new { name = "随机卷", rules = new[] { new { type = "单选", count = 5, score_each = 2 } } });
                Assert.Equal(2, (int)random["questionCount"]);
                Assert.Single(random["warnings"]);

                Assert.Contains("班级", Call(mcp, "create_exam", new { paper_id = (long)paper["id"], class_ids = new[] { 1 } }).error);
            }
        }

        [Fact]
        public async Task ControlToolsRunAnExam()
        {
            var (env, host, mcp) = Create(McpPermissions.Read | McpPermissions.Edit | McpPermissions.Control);
            using (env)
            using (host)
            using (mcp)
            {
                host.Start(0, TestEnv.FreeUdpPort());
                var classId = env.Roster.AddClass("一班");
                env.Roster.AddStudent(classId, "001", "张三");
                env.Roster.AddStudent(classId, "002", "李四");
                var md = File.ReadAllText(MarkdownParserTests.SamplePath);
                var (imported, _) = Call(mcp, "import_questions_markdown", new { markdown = md, paper_name = "样卷" });
                var (exam, err) = Call(mcp, "create_exam", new { paper_id = (long)imported["paper"]["id"], title = "MCP 考试", duration_minutes = 30 });
                Assert.Null(err);
                var examId = (long)exam["id"];
                Assert.Equal("未开始", (string)exam["status"]);

                Assert.Contains("只有候考中", Call(mcp, "start_exam").error);
                Call(mcp, "open_exam", new { exam_id = examId });
                Assert.Equal(ExamStatus.Open, host.CurrentExam.Status);

                using var c = new MdQuiz.Core.Client.ExamClient(Path.Combine(env.Dir, "c"), "PC");
                await c.ConnectAsync("127.0.0.1", host.Port);
                Assert.True((await c.LoginAsync("001")).Ok);

                Call(mcp, "start_exam");
                Assert.Equal(ExamStatus.Running, host.CurrentExam.Status);
                var (adjusted, _) = Call(mcp, "adjust_exam_time", new { minutes = -10 });
                Assert.Equal(20, (int)adjusted["totalMinutes"]);
                Assert.Contains("不足 1 分钟", Call(mcp, "adjust_exam_time", new { minutes = -25 }).error);

                await TaskExt.Until(() => c.Paper != null);
                var tf = env.Exams.Get(examId).Questions.First(q => q.Type == QuestionType.TrueFalse);
                c.SetAnswer(tf.Id, new System.Collections.Generic.List<string> { tf.Answer });
                await TaskExt.Until(() => c.PendingSyncCount == 0);

                var (monitor, _) = Call(mcp, "get_monitor_status");
                Assert.Equal("考试中", (string)monitor["exam"]["status"]);
                Assert.Equal(2, (int)monitor["counts"]["total"]);
                Assert.True((long)monitor["exam"]["remainingSeconds"] > 0);
                var live = monitor["participants"].Single(p => (string)p["studentNo"] == "001");
                Assert.True((bool)live["online"]);
                Assert.Equal(1, (int)live["answered"]);

                var (answers, _) = Call(mcp, "get_student_answers", new { exam_id = examId, student_no = "001" });
                var tfItem = answers["items"].Single(i => (long)i["questionId"] == tf.Id);
                Assert.Equal("正确", (string)tfItem["verdict"]);
                Assert.True((bool)tfItem["provisional"]);

                Call(mcp, "end_exam");
                Assert.Equal(ExamStatus.Ended, env.Exams.Get(examId).Status);

                var essay = env.Exams.Get(examId).Questions.First(q => q.Type == QuestionType.ShortAnswer);
                var (scored, scoreErr) = Call(mcp, "set_manual_score", new { exam_id = examId, student_no = "001", question_id = essay.Id, score = 5 });
                Assert.Null(scoreErr);
                Assert.Equal(5, (double)scored["score"]);

                var (results, _) = Call(mcp, "get_exam_results", new { exam_id = examId });
                Assert.Equal(2, (int)results["stats"]["expected"]);
                Assert.Equal("缺考", (string)results["students"].Single(s => (string)s["studentNo"] == "002")["status"]);
                var (analysis, _) = Call(mcp, "get_question_analysis", new { exam_id = examId });
                Assert.Equal(1, (int)analysis["exam"]["submitted"]);
                Assert.Equal(1, (int)analysis["questions"].Single(q => (long)q["questionId"] == tf.Id)["fullScoreCount"]);

                var (exams, _) = Call(mcp, "list_exams", new { status = "已结束" });
                Assert.Single(exams["exams"]);
                Call(mcp, "close_exam");
                Assert.Null(host.CurrentExam);
            }
        }

        [Fact]
        public async Task StreamableHttpTransport()
        {
            var (env, host, mcp) = Create();
            using (env)
            using (host)
            using (mcp)
            using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mcp.Port}") })
            {
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
                var init = await http.PostAsync("/mcp", Json(Request("initialize", new { protocolVersion = "2025-06-18" })));
                Assert.Equal(HttpStatusCode.OK, init.StatusCode);
                Assert.Equal("application/json", init.Content.Headers.ContentType.MediaType);
                Assert.Equal("2025-06-18", (string)JObject.Parse(await init.Content.ReadAsStringAsync())["result"]["protocolVersion"]);

                var note = await http.PostAsync("/mcp", Json(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
                Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);

                // 同一连接上连续请求（keep-alive）
                for (var i = 0; i < 3; i++)
                {
                    var list = await http.PostAsync("/mcp", Json(Request("tools/call", new { name = "get_overview", arguments = new { } }, 10 + i)));
                    var body = JObject.Parse(await list.Content.ReadAsStringAsync());
                    Assert.False((bool)body["result"]["isError"]);
                    Assert.Contains("明点智慧考评系统", (string)body["result"]["content"][0]["text"]);
                }

                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync("/mcp")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nope")).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/mcp", new StringContent("{bad", Encoding.UTF8, "application/json"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);

                // 浏览器来源检查（防 DNS 重绑定）
                var evil = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(Request("ping")) };
                evil.Headers.Add("Origin", "http://evil.example.com");
                Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(evil)).StatusCode);
                var local = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(Request("ping")) };
                local.Headers.Add("Origin", "http://localhost:6274");
                var localReply = await http.SendAsync(local);
                Assert.Equal(HttpStatusCode.OK, localReply.StatusCode);
                Assert.Equal("http://localhost:6274", localReply.Headers.GetValues("Access-Control-Allow-Origin").Single());

                // 分块传输的请求体
                var chunked = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(Request("ping", null, 99)) };
                chunked.Headers.TransferEncodingChunked = true;
                var chunkedReply = await http.SendAsync(chunked);
                Assert.Equal(99, (int)JObject.Parse(await chunkedReply.Content.ReadAsStringAsync())["id"]);
            }
        }

        [Fact]
        public async Task LanModeRequiresToken()
        {
            using var env = new TestEnv();
            using var host = new ExamHost(env.Db);
            using var mcp = new McpServer(new McpToolset(env.Questions, env.Papers, env.Roster, env.Exams, env.Results, env.PaperService, host));
            Assert.Throws<InvalidOperationException>(() => mcp.Start(new McpOptions { Port = 0, AllowLan = true }));
            var token = McpOptions.NewToken();
            mcp.Start(new McpOptions { Port = 0, AllowLan = true, Token = token });
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mcp.Port}") };

            var denied = await http.PostAsync("/mcp", Json(Request("ping")));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Contains("Bearer", denied.Headers.WwwAuthenticate.ToString());

            var wrong = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(Request("ping")) };
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token + "x");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

            var ok = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(Request("ping")) };
            ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/mcp?token=" + Uri.EscapeDataString(token), Json(Request("ping")))).StatusCode);

            // 重启为仅本机：不再需要令牌
            mcp.Start(new McpOptions { Port = 0 });
            using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mcp.Port}") };
            Assert.Equal(HttpStatusCode.OK, (await local.PostAsync("/mcp", Json(Request("ping")))).StatusCode);
        }

        [Fact]
        public async Task LegacySseTransport()
        {
            var (env, host, mcp) = Create();
            using (env)
            using (host)
            using (mcp)
            using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{mcp.Port}") })
            {
                var response = await http.GetAsync("/sse", HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

                async Task<(string evt, string data)> NextEvent()
                {
                    string evt = null, data = null, line;
                    while ((line = await reader.ReadLineAsync().Within(5000)) != null)
                    {
                        if (line.StartsWith("event: ")) evt = line.Substring(7);
                        else if (line.StartsWith("data: ")) data = line.Substring(6);
                        else if (line.Length == 0 && data != null) return (evt, data);
                    }
                    return (evt, data);
                }

                var (e1, endpoint) = await NextEvent();
                Assert.Equal("endpoint", e1);
                Assert.StartsWith("/messages?sessionId=", endpoint);

                var post = await http.PostAsync(endpoint, Json(Request("initialize", new { protocolVersion = "2024-11-05" }, 7)));
                Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
                var (e2, message) = await NextEvent();
                Assert.Equal("message", e2);
                var json = JObject.Parse(message);
                Assert.Equal(7, (int)json["id"]);
                Assert.Equal("2024-11-05", (string)json["result"]["protocolVersion"]);

                Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/messages?sessionId=nope", Json(Request("ping")))).StatusCode);
            }
        }

        private static StringContent Json(JToken body) => new StringContent(body.ToString(), Encoding.UTF8, "application/json");
    }
}
