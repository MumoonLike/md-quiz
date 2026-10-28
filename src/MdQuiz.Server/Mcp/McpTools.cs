using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MdQuiz.Core;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
using MdQuiz.Server.Services;
using Newtonsoft.Json.Linq;

namespace MdQuiz.Server.Mcp
{
    /// <summary>工具返回的数据（序列化为 JSON 文本）和写入操作的简短说明（显示在教师端通知中）。</summary>
    public sealed class McpToolOutput
    {
        public object Data { get; set; }
        public string Summary { get; set; }
    }

    public sealed class McpTool
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public McpPermissions Level { get; set; }
        public JObject InputSchema { get; set; }
        public bool Destructive { get; set; }
        public Func<McpArgs, McpToolOutput> Run { get; set; }

        public JObject Describe()
        {
            return new JObject
            {
                ["name"] = Name,
                ["title"] = Title,
                ["description"] = Description,
                ["inputSchema"] = InputSchema,
                ["annotations"] = new JObject
                {
                    ["title"] = Title,
                    ["readOnlyHint"] = Level == McpPermissions.Read,
                    ["destructiveHint"] = Destructive,
                    ["openWorldHint"] = false,
                },
            };
        }
    }

    /// <summary>工具参数读取（类型不符时抛出 ArgumentException，作为工具错误返回给 AI）。</summary>
    public sealed class McpArgs
    {
        private readonly JObject _o;

        public McpArgs(JObject o)
        {
            _o = o ?? new JObject();
        }

        public JObject Raw => _o;

        public bool Has(string name) => _o.TryGetValue(name, out _);

        public bool IsNull(string name) => !_o.TryGetValue(name, out var t) || t.Type == JTokenType.Null;

        private JToken Get(string name) => _o.TryGetValue(name, out var t) && t.Type != JTokenType.Null ? t : null;

        public string Str(string name, bool required = false)
        {
            var t = Get(name);
            if (t == null)
            {
                if (required) throw new ArgumentException($"缺少参数 {name}");
                return null;
            }
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        }

        public long Long(string name)
        {
            var v = LongOrNull(name);
            if (v == null) throw new ArgumentException($"缺少参数 {name}");
            return v.Value;
        }

        public long? LongOrNull(string name)
        {
            var t = Get(name);
            if (t == null) return null;
            try
            {
                return t.Value<long>();
            }
            catch (Exception)
            {
                throw new ArgumentException($"参数 {name} 应为整数");
            }
        }

        public int Int(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            var v = LongOrNull(name);
            if (v == null) return fallback;
            if (v < min || v > max) throw new ArgumentException($"参数 {name} 应在 {min} ~ {max} 之间");
            return (int)v.Value;
        }

        public double? Double(string name)
        {
            var t = Get(name);
            if (t == null) return null;
            try
            {
                return t.Value<double>();
            }
            catch (Exception)
            {
                throw new ArgumentException($"参数 {name} 应为数字");
            }
        }

        public bool Bool(string name, bool fallback)
        {
            var t = Get(name);
            if (t == null) return fallback;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (bool.TryParse(t.ToString(), out var b)) return b;
            throw new ArgumentException($"参数 {name} 应为 true 或 false");
        }

        public List<string> StrList(string name)
        {
            var t = Get(name);
            if (t == null) return null;
            if (t is JArray a) return a.Where(x => x.Type != JTokenType.Null).Select(x => x.Type == JTokenType.String ? (string)x : x.ToString()).ToList();
            if (t.Type == JTokenType.String) return ((string)t).Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            throw new ArgumentException($"参数 {name} 应为字符串数组");
        }

        public List<long> LongList(string name, bool required = false)
        {
            var t = Get(name);
            if (t == null)
            {
                if (required) throw new ArgumentException($"缺少参数 {name}");
                return null;
            }
            try
            {
                if (t is JArray a) return a.Select(x => x.Value<long>()).ToList();
                return new List<long> { t.Value<long>() };
            }
            catch (Exception)
            {
                throw new ArgumentException($"参数 {name} 应为整数数组");
            }
        }

        public List<McpArgs> ObjList(string name, bool required = false)
        {
            var t = Get(name);
            if (t == null)
            {
                if (required) throw new ArgumentException($"缺少参数 {name}");
                return null;
            }
            if (!(t is JArray a) || a.Any(x => !(x is JObject))) throw new ArgumentException($"参数 {name} 应为对象数组");
            return a.Select(x => new McpArgs((JObject)x)).ToList();
        }
    }

    /// <summary>JSON Schema 构造。</summary>
    internal sealed class Schema
    {
        private readonly JObject _props = new JObject();
        private readonly JArray _required = new JArray();

        public static Schema Object() => new Schema();

        private Schema Add(string name, JObject prop, string description, bool required)
        {
            prop["description"] = description;
            _props[name] = prop;
            if (required) _required.Add(name);
            return this;
        }

        public Schema Str(string name, string description, bool required = false, params string[] values)
        {
            var prop = new JObject { ["type"] = "string" };
            if (values != null && values.Length > 0) prop["enum"] = new JArray(values.Cast<object>().ToArray());
            return Add(name, prop, description, required);
        }

        public Schema Int(string name, string description, bool required = false, int? min = null, int? max = null)
        {
            var prop = new JObject { ["type"] = "integer" };
            if (min.HasValue) prop["minimum"] = min.Value;
            if (max.HasValue) prop["maximum"] = max.Value;
            return Add(name, prop, description, required);
        }

        public Schema Num(string name, string description, bool required = false, bool nullable = false)
        {
            var prop = nullable ? new JObject { ["type"] = new JArray("number", "null") } : new JObject { ["type"] = "number" };
            return Add(name, prop, description, required);
        }

        public Schema NullableInt(string name, string description, bool required = false)
        {
            return Add(name, new JObject { ["type"] = new JArray("integer", "null") }, description, required);
        }

        public Schema Bool(string name, string description, bool required = false)
        {
            return Add(name, new JObject { ["type"] = "boolean" }, description, required);
        }

        public Schema StrArray(string name, string description, bool required = false)
        {
            return Add(name, new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, description, required);
        }

        public Schema IntArray(string name, string description, bool required = false)
        {
            return Add(name, new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, description, required);
        }

        public Schema ObjArray(string name, string description, Schema item, bool required = false, int? maxItems = null)
        {
            var prop = new JObject { ["type"] = "array", ["items"] = item.Build() };
            if (maxItems.HasValue) prop["maxItems"] = maxItems.Value;
            return Add(name, prop, description, required);
        }

        public JObject Build()
        {
            var o = new JObject { ["type"] = "object", ["properties"] = _props };
            if (_required.Count > 0) o["required"] = _required;
            return o;
        }
    }

    /// <summary>MCP 工具集合：查询（只读）、编辑（题库 / 试卷 / 考试）、考试控制与改分三档。</summary>
    public sealed class McpToolset
    {
        private static readonly string[] TypeNames = { "单选", "多选", "判断", "填空", "简答" };
        private static readonly string[] ModeNames = { "全对才得分", "少选得一半", "按比例给分" };

        private readonly QuestionRepository _questions;
        private readonly PaperRepository _papers;
        private readonly RosterRepository _roster;
        private readonly ExamRepository _exams;
        private readonly ResultService _results;
        private readonly PaperService _paperService;
        private readonly ExamHost _host;
        private readonly List<McpTool> _tools = new List<McpTool>();

        public McpToolset(QuestionRepository questions, PaperRepository papers, RosterRepository roster, ExamRepository exams,
            ResultService results, PaperService paperService, ExamHost host)
        {
            _questions = questions;
            _papers = papers;
            _roster = roster;
            _exams = exams;
            _results = results;
            _paperService = paperService;
            _host = host;
            RegisterReadTools();
            RegisterEditTools();
            RegisterControlTools();
        }

        public IReadOnlyList<McpTool> All => _tools;

        public McpTool Find(string name) => _tools.FirstOrDefault(t => t.Name == name);

        private void Add(McpPermissions level, string name, string title, string description, Schema schema, Func<McpArgs, McpToolOutput> run, bool destructive = false)
        {
            _tools.Add(new McpTool
            {
                Level = level,
                Name = name,
                Title = title,
                Description = description,
                InputSchema = schema.Build(),
                Run = run,
                Destructive = destructive,
            });
        }

        private static McpToolOutput Out(object data, string summary = null) => new McpToolOutput { Data = data, Summary = summary };

        // ================= 格式化 =================

        private static string TypeName(QuestionType t) => QuestionTypes.ShortName(t);

        private static QuestionType ParseType(string text)
        {
            if (QuestionTypes.TryParse(text, out var t)) return t;
            throw new ArgumentException($"未知题型“{text}”，可用：{string.Join("、", TypeNames)}");
        }

        private static object QuestionSummary(Question q) => new
        {
            id = q.Id,
            type = TypeName(q.Type),
            summary = q.Summary(120),
            tags = q.Tags,
        };

        private static object QuestionDetail(Question q) => new
        {
            id = q.Id,
            type = TypeName(q.Type),
            stem = q.Stem,
            options = QuestionTypes.HasOptions(q.Type) ? q.Options.Select((o, i) => new { key = Question.OptionKey(i), text = o }).ToList() : null,
            answer = q.Type == QuestionType.TrueFalse ? (q.Answer == "T" ? "对" : "错") : q.Type == QuestionType.FillBlank ? null : q.Answer,
            blanks = q.Type == QuestionType.FillBlank ? q.Blanks : null,
            analysis = string.IsNullOrEmpty(q.Analysis) ? null : q.Analysis,
            tags = q.Tags,
            updatedAt = q.UpdatedAt,
        };

        private static object ExamBrief(ExamSummary e, Exam current) => new
        {
            id = e.Id,
            title = e.Title,
            paper = e.PaperName,
            status = ExamTexts.StatusName(e.Status),
            questionCount = e.QuestionCount,
            totalScore = e.TotalScore,
            durationMinutes = e.TotalMinutes,
            extraMinutes = e.ExtraMinutes,
            createdAt = e.CreatedAt,
            startedAt = e.StartedAt,
            endedAt = e.EndedAt,
            plannedEndAt = e.Status == ExamStatus.Running ? e.EndAt : null,
            participants = e.ParticipantCount,
            submitted = e.SubmittedCount,
            pendingManual = e.PendingManualCount,
            inMonitor = current != null && current.Id == e.Id,
        };

        private Exam RequireExam(long id) => _exams.Get(id) ?? throw new ArgumentException($"考试 {id} 不存在");

        private Paper RequirePaper(long id) => _papers.Get(id) ?? throw new ArgumentException($"试卷 {id} 不存在");

        private long? GroupArg(McpArgs a, string name)
        {
            var id = a.LongOrNull(name);
            if (id.HasValue && _papers.ListGroups().All(g => g.Id != id.Value)) throw new ArgumentException($"试卷分组 {id} 不存在");
            return id;
        }

        // ================= 只读 =================

        private void RegisterReadTools()
        {
            const McpPermissions L = McpPermissions.Read;

            Add(L, "get_overview", "系统概况",
                "获取题库、试卷、学生、考试的数量统计，以及当前监考中的考试状态。适合在开始时了解整体情况。",
                Schema.Object(), a =>
                {
                    var questions = _questions.Query();
                    var exams = _exams.List();
                    var classes = _roster.ListClasses();
                    var current = _host.CurrentExam;
                    return Out(new
                    {
                        product = Brand.ProductName,
                        questionCount = questions.Count,
                        questionsByType = questions.GroupBy(q => q.Type).OrderBy(g => g.Key).ToDictionary(g => TypeName(g.Key), g => g.Count()),
                        tagCount = _questions.AllTags().Count,
                        paperCount = _papers.List().Count,
                        paperGroupCount = _papers.ListGroups().Count,
                        classCount = classes.Count,
                        studentCount = classes.Sum(c => c.StudentCount),
                        examCount = exams.Count,
                        examsByStatus = exams.GroupBy(e => e.Status).OrderBy(g => g.Key).ToDictionary(g => ExamTexts.StatusName(g.Key), g => g.Count()),
                        currentExam = current == null ? null : new { id = current.Id, title = current.Title, status = ExamTexts.StatusName(current.Status) },
                    });
                });

            Add(L, "search_questions", "搜索题目",
                "按关键词、题型、标签搜索题库，返回题目 id、题型、题干摘要和标签（按新到旧）。需要完整内容和答案时再调用 get_questions。",
                Schema.Object()
                    .Str("keyword", "关键词（匹配题干、选项和标签）")
                    .Str("type", "题型", false, TypeNames)
                    .Str("tag", "标签（完全匹配）")
                    .Int("limit", "最多返回条数，默认 30", false, 1, 200)
                    .Int("offset", "跳过前若干条，用于翻页", false, 0), a =>
                {
                    var type = a.Str("type");
                    var list = _questions.Query(new QuestionQuery
                    {
                        Keyword = a.Str("keyword"),
                        Tag = a.Str("tag"),
                        Type = string.IsNullOrWhiteSpace(type) ? (QuestionType?)null : ParseType(type),
                    });
                    var limit = a.Int("limit", 30, 1, 200);
                    var offset = a.Int("offset", 0, 0);
                    return Out(new { total = list.Count, offset, items = list.Skip(offset).Take(limit).Select(QuestionSummary).ToList() });
                });

            Add(L, "get_questions", "查看题目详情",
                "按 id 获取题目的完整内容：题干（Markdown）、选项、答案、填空答案、解析、标签。一次最多 50 道。",
                Schema.Object().IntArray("ids", "题目 id 列表", true), a =>
                {
                    var ids = a.LongList("ids", true);
                    if (ids.Count > 50) throw new ArgumentException("一次最多查询 50 道题");
                    var found = _questions.GetMany(ids);
                    return Out(new
                    {
                        items = ids.Where(found.ContainsKey).Select(id => QuestionDetail(found[id])).ToList(),
                        notFound = ids.Where(id => !found.ContainsKey(id)).ToList(),
                    });
                });

            Add(L, "list_tags", "列出标签", "列出题库中用到的全部标签。", Schema.Object(), a => Out(new { tags = _questions.AllTags() }));

            Add(L, "list_papers", "列出试卷",
                "列出试卷分组和试卷（id、名称、所属分组、题数、满分、更新时间）。可按分组筛选。",
                Schema.Object().NullableInt("group_id", "只看某个分组的试卷；不传表示全部"), a =>
                {
                    var groups = _papers.ListGroups();
                    var names = groups.ToDictionary(g => g.Id, g => g.Name);
                    var papers = _papers.List();
                    if (a.Has("group_id"))
                    {
                        var gid = a.LongOrNull("group_id");
                        papers = papers.Where(p => p.GroupId == gid).ToList();
                    }
                    return Out(new
                    {
                        groups = groups.Select(g => new { id = g.Id, name = g.Name, paperCount = g.PaperCount }).ToList(),
                        papers = papers.Select(p => new
                        {
                            id = p.Id,
                            name = p.Name,
                            groupId = p.GroupId,
                            group = p.GroupId.HasValue && names.TryGetValue(p.GroupId.Value, out var n) ? n : "未分组",
                            questionCount = p.Items.Count,
                            totalScore = p.TotalScore,
                            updatedAt = p.UpdatedAt,
                        }).ToList(),
                    });
                });

            Add(L, "get_paper", "查看试卷",
                "获取试卷的题目列表（顺序、题目 id、题型、分值、题干摘要）和按题型的统计。",
                Schema.Object().Int("paper_id", "试卷 id", true), a =>
                {
                    var paper = RequirePaper(a.Long("paper_id"));
                    var questions = _questions.GetMany(paper.Items.Select(i => i.QuestionId));
                    var group = paper.GroupId.HasValue ? _papers.ListGroups().FirstOrDefault(g => g.Id == paper.GroupId)?.Name : null;
                    var items = paper.Items.Select((it, i) => new
                    {
                        order = i + 1,
                        questionId = it.QuestionId,
                        score = it.Score,
                        type = questions.TryGetValue(it.QuestionId, out var q) ? TypeName(q.Type) : null,
                        summary = q?.Summary(100) ?? "（已从题库删除）",
                    }).ToList();
                    return Out(new
                    {
                        id = paper.Id,
                        name = paper.Name,
                        description = paper.Description,
                        group = group ?? "未分组",
                        totalScore = paper.TotalScore,
                        byType = paper.Items.Where(i => questions.ContainsKey(i.QuestionId)).GroupBy(i => questions[i.QuestionId].Type).OrderBy(g => g.Key)
                            .Select(g => new { type = TypeName(g.Key), count = g.Count(), score = g.Sum(i => i.Score) }).ToList(),
                        items,
                    });
                });

            Add(L, "list_classes", "列出班级", "列出班级及人数。", Schema.Object(),
                a => Out(new { classes = _roster.ListClasses().Select(c => new { id = c.Id, name = c.Name, studentCount = c.StudentCount }).ToList() }));

            Add(L, "list_students", "列出学生",
                "列出学生（学号、姓名、班级），可按班级或关键词筛选。",
                Schema.Object().Int("class_id", "班级 id").Str("keyword", "按学号或姓名筛选").Int("limit", "最多返回条数，默认 200", false, 1, 2000), a =>
                {
                    var classId = a.LongOrNull("class_id");
                    var keyword = (a.Str("keyword") ?? "").Trim();
                    var list = _roster.ListStudents(classId).Where(s => keyword.Length == 0 || s.StudentNo.Contains(keyword) || s.Name.Contains(keyword)).ToList();
                    return Out(new
                    {
                        total = list.Count,
                        items = list.Take(a.Int("limit", 200, 1, 2000)).Select(s => new { studentNo = s.StudentNo, name = s.Name, className = s.ClassName, classId = s.ClassId }).ToList(),
                    });
                });

            Add(L, "list_exams", "列出考试",
                "列出考试：状态、试卷、题数、满分、时长、开始 / 结束时间、交卷人数等。",
                Schema.Object().Str("status", "按状态筛选", false, "未开始", "候考中", "考试中", "已结束"), a =>
                {
                    var status = a.Str("status");
                    var current = _host.CurrentExam;
                    var list = _exams.List().Where(e => string.IsNullOrEmpty(status) || ExamTexts.StatusName(e.Status) == status);
                    return Out(new { exams = list.Select(e => ExamBrief(e, current)).ToList() });
                });

            Add(L, "get_monitor_status", "监考状态",
                "当前监考中的考试：剩余时间、结束时间，以及每名考生的登录、在线、答题进度、切屏次数。",
                Schema.Object(), a =>
                {
                    var exam = _host.CurrentExam;
                    if (exam == null) return Out(new { exam = (object)null, message = "当前没有开放的考试" });
                    var participants = _host.GetParticipants();
                    var remaining = _host.Remaining;
                    return Out(new
                    {
                        exam = new
                        {
                            id = exam.Id,
                            title = exam.Title,
                            status = ExamTexts.StatusName(exam.Status),
                            durationMinutes = exam.TotalMinutes,
                            startedAt = exam.StartedAt,
                            endAt = exam.EndedAt ?? exam.DeadlineUtc,
                            remainingSeconds = remaining.HasValue ? (long?)remaining.Value.TotalSeconds : null,
                        },
                        counts = new
                        {
                            total = participants.Count,
                            loggedIn = participants.Count(p => p.Status != ParticipantStatus.Absent),
                            answering = participants.Count(p => p.Status == ParticipantStatus.Answering && p.Online),
                            submitted = participants.Count(p => p.Status == ParticipantStatus.Submitted),
                            offline = participants.Count(p => !p.Online && (p.Status == ParticipantStatus.Waiting || p.Status == ParticipantStatus.Answering)),
                            focusLostAlerts = participants.Count(p => p.FocusLost > 0),
                        },
                        participants = participants.Select(p => new
                        {
                            studentNo = p.StudentNo,
                            name = p.Name,
                            className = p.ClassName,
                            status = ExamTexts.ParticipantStatusName(p.Status),
                            online = p.Online,
                            answered = p.Answered,
                            questionCount = p.QuestionCount,
                            focusLost = p.FocusLost,
                            machine = p.Machine,
                            ip = p.Ip,
                            objectiveScore = p.ObjectiveScore,
                        }).ToList(),
                    });
                });

            Add(L, "get_exam_results", "考试成绩",
                "获取一场考试的成绩统计（平均分、最高分、最低分、分数段）和每名考生的成绩（按总分降序）。",
                Schema.Object().Int("exam_id", "考试 id", true), a =>
                {
                    var exam = RequireExam(a.Long("exam_id"));
                    var ps = _exams.ListParticipants(exam.Id);
                    var scored = ps.Where(p => p.Status == ParticipantStatus.Submitted && p.TotalScore.HasValue).Select(p => p.TotalScore.Value).ToList();
                    var full = exam.TotalScore <= 0 ? 100 : exam.TotalScore;
                    return Out(new
                    {
                        exam = new { id = exam.Id, title = exam.Title, status = ExamTexts.StatusName(exam.Status), totalScore = exam.TotalScore, questionCount = exam.Questions.Count },
                        stats = new
                        {
                            expected = ps.Count,
                            attended = ps.Count(p => p.Status != ParticipantStatus.Absent),
                            submitted = ps.Count(p => p.Status == ParticipantStatus.Submitted),
                            average = scored.Count == 0 ? (double?)null : Math.Round(scored.Average(), 2),
                            highest = scored.Count == 0 ? (double?)null : scored.Max(),
                            lowest = scored.Count == 0 ? (double?)null : scored.Min(),
                            pendingManualStudents = ps.Count(p => p.PendingManual > 0),
                            distribution = new
                            {
                                below60 = scored.Count(s => s / full < 0.6),
                                from60to70 = scored.Count(s => s / full >= 0.6 && s / full < 0.7),
                                from70to80 = scored.Count(s => s / full >= 0.7 && s / full < 0.8),
                                from80to90 = scored.Count(s => s / full >= 0.8 && s / full < 0.9),
                                atLeast90 = scored.Count(s => s / full >= 0.9),
                            },
                        },
                        students = ps.OrderByDescending(p => p.TotalScore ?? -1).ThenBy(p => p.StudentNo).Select(p => new
                        {
                            studentNo = p.StudentNo,
                            name = p.Name,
                            className = p.ClassName,
                            status = p.Status == ParticipantStatus.Absent ? "缺考" : ExamTexts.ParticipantStatusName(p.Status),
                            forcedSubmit = p.Forced,
                            objectiveScore = p.ObjectiveScore,
                            subjectiveScore = p.SubjectiveScore,
                            totalScore = p.TotalScore,
                            pendingManual = p.PendingManual,
                            focusLost = p.FocusLost,
                            submittedAt = p.SubmittedAt,
                        }).ToList(),
                    });
                });

            Add(L, "get_student_answers", "考生答卷",
                "获取某名考生在一场考试中的逐题作答：学生答案、正确答案、得分和判定（正确 / 部分正确 / 错误 / 未作答 / 待批改）。考试中查看时客观题得分为按当前答案预判。",
                Schema.Object().Int("exam_id", "考试 id", true).Str("student_no", "学号", true), a =>
                {
                    var exam = RequireExam(a.Long("exam_id"));
                    var no = a.Str("student_no", true).Trim();
                    var p = _exams.GetParticipant(exam.Id, no) ?? throw new ArgumentException($"学号 {no} 不在这场考试的名单中");
                    var sheet = _results.BuildAnswerSheet(exam, no);
                    return Out(new
                    {
                        student = new
                        {
                            studentNo = p.StudentNo,
                            name = p.Name,
                            className = p.ClassName,
                            status = ExamTexts.ParticipantStatusName(p.Status),
                            totalScore = p.TotalScore,
                            pendingManual = p.PendingManual,
                            focusLost = p.FocusLost,
                        },
                        items = sheet.Select(s => new
                        {
                            number = s.Number,
                            questionId = s.Question.Id,
                            type = TypeName(s.Question.Type),
                            stem = s.Question.Stem,
                            options = QuestionTypes.HasOptions(s.Question.Type) ? s.Question.Options.Select((o, i) => $"{Question.OptionKey(i)}. {o}").ToList() : null,
                            studentAnswer = s.Answered ? AnswerFormatter.Format(s.Question, s.Values) : null,
                            correctAnswer = AnswerFormatter.FormatCorrect(s.Question),
                            score = s.Score,
                            maxScore = s.Question.Score,
                            verdict = VerdictName(s.Verdict),
                            provisional = s.Provisional,
                            manual = s.Manual,
                        }).ToList(),
                    });
                });

            Add(L, "get_question_analysis", "逐题分析",
                "统计一场考试每道题的作答情况：平均得分、得分率、满分率、未作答人数；选择题给出各选项被选人数。只统计已交卷的考生。",
                Schema.Object().Int("exam_id", "考试 id", true), a =>
                {
                    var exam = RequireExam(a.Long("exam_id"));
                    var submitted = new HashSet<string>(_exams.ListParticipants(exam.Id).Where(p => p.Status == ParticipantStatus.Submitted).Select(p => p.StudentNo), StringComparer.OrdinalIgnoreCase);
                    var answers = _exams.ListAllAnswers(exam.Id).Where(x => submitted.Contains(x.StudentNo)).GroupBy(x => x.QuestionId).ToDictionary(g => g.Key, g => g.ToList());
                    var n = submitted.Count;
                    return Out(new
                    {
                        exam = new { id = exam.Id, title = exam.Title, submitted = n },
                        questions = exam.Questions.Select((q, i) =>
                        {
                            answers.TryGetValue(q.Id, out var list);
                            list = list ?? new List<AnswerRecord>();
                            var scores = list.Where(x => x.Score.HasValue).Select(x => x.Score.Value).ToList();
                            var avg = n == 0 ? (double?)null : Math.Round(scores.Sum() / n, 2);
                            return new
                            {
                                number = i + 1,
                                questionId = q.Id,
                                type = TypeName(q.Type),
                                summary = q.Summary(80),
                                correctAnswer = AnswerFormatter.FormatCorrect(q),
                                maxScore = q.Score,
                                averageScore = avg,
                                scoreRate = avg.HasValue && q.Score > 0 ? Math.Round(avg.Value / q.Score, 3) : (double?)null,
                                fullScoreCount = scores.Count(s => s >= q.Score - 1e-9),
                                unanswered = n - list.Count(x => !AnswerValues.IsEmpty(x.Values)),
                                pendingManual = list.Count(x => !x.Score.HasValue),
                                optionCounts = QuestionTypes.HasOptions(q.Type) || q.Type == QuestionType.TrueFalse
                                    ? list.SelectMany(x => x.Values ?? new List<string>()).GroupBy(v => (v ?? "").Trim().ToUpperInvariant()).Where(g => g.Key.Length > 0)
                                        .OrderBy(g => g.Key).ToDictionary(g => q.Type == QuestionType.TrueFalse ? (g.Key == "T" ? "对" : "错") : g.Key, g => g.Count())
                                    : null,
                            };
                        }).ToList(),
                    });
                });
        }

        private static string VerdictName(AnswerVerdict v)
        {
            switch (v)
            {
                case AnswerVerdict.Correct: return "正确";
                case AnswerVerdict.Partial: return "部分正确";
                case AnswerVerdict.Wrong: return "错误";
                case AnswerVerdict.Pending: return "待批改";
                case AnswerVerdict.Graded: return "已批改";
                default: return "未作答";
            }
        }

        // ================= 编辑 =================

        private static Schema QuestionSchema(bool forUpdate)
        {
            var s = Schema.Object();
            if (forUpdate) s.Int("id", "要修改的题目 id", true);
            return s.Str("type", "题型", !forUpdate, TypeNames)
                .Str("stem", "题干，支持 Markdown（加粗、代码、表格、公式 $x^2$）。填空题用 ___（三个及以上下划线）标出每个空", !forUpdate)
                .StrArray("options", "选择题选项（2~10 个，按 A、B、C… 顺序，不要带字母前缀）")
                .Str("answer", "答案：单选如 \"B\"，多选如 \"ACD\"，判断题“对”或“错”，简答题写参考答案。填空题不用此字段")
                .StrArray("blanks", "填空题每个空的答案，按顺序一项一个；同一空有多个可接受答案用 | 分隔，如 \"北京|北京市\"")
                .Str("analysis", "解析（可选）")
                .StrArray("tags", "标签（可选），用于筛选和随机抽题");
        }

        /// <summary>按参数填充题目（修改时只覆盖传入的字段），返回错误描述或 null。</summary>
        private static string FillQuestion(Question q, McpArgs a)
        {
            if (a.Has("type")) q.Type = ParseType(a.Str("type"));
            if (a.Has("stem")) q.Stem = a.Str("stem") ?? "";
            if (a.Has("options")) q.Options = (a.StrList("options") ?? new List<string>()).Select(o => o.Trim()).ToList();
            if (a.Has("blanks")) q.Blanks = (a.StrList("blanks") ?? new List<string>()).Select(o => o.Trim()).ToList();
            if (a.Has("analysis")) q.Analysis = a.Str("analysis") ?? "";
            if (a.Has("tags")) q.Tags = (a.StrList("tags") ?? new List<string>()).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            if (a.Has("answer"))
            {
                var answer = (a.Str("answer") ?? "").Trim();
                switch (q.Type)
                {
                    case QuestionType.Single:
                    case QuestionType.Multiple:
                        q.Answer = Question.NormalizeKeys(answer.Where(char.IsLetter).Select(c => c.ToString()));
                        break;
                    case QuestionType.TrueFalse:
                        if (!Question.TryParseTrueFalse(answer, out var tf)) return "判断题答案应为“对”或“错”";
                        q.Answer = tf ? "T" : "F";
                        break;
                    case QuestionType.FillBlank:
                        if (!a.Has("blanks") && answer.Length > 0) q.Blanks = answer.Split(new[] { ';', '；' }).Select(x => x.Trim()).ToList();
                        break;
                    default:
                        q.Answer = answer;
                        break;
                }
            }
            if (!QuestionTypes.HasOptions(q.Type)) q.Options = new List<string>();
            if (q.Type != QuestionType.FillBlank) q.Blanks = new List<string>();
            return q.Validate();
        }

        private void RegisterEditTools()
        {
            const McpPermissions L = McpPermissions.Edit;

            Add(L, "create_questions", "新建题目",
                "向题库添加题目（一次最多 100 道）。每道题单独校验，有问题的题目不会添加并会返回原因。",
                Schema.Object().ObjArray("questions", "要添加的题目", QuestionSchema(false), true, 100), a =>
                {
                    var items = a.ObjList("questions", true);
                    if (items.Count > 100) throw new ArgumentException("一次最多添加 100 道题");
                    var valid = new List<Question>();
                    var errors = new List<object>();
                    for (var i = 0; i < items.Count; i++)
                    {
                        var q = new Question();
                        string error;
                        try
                        {
                            error = FillQuestion(q, items[i]);
                        }
                        catch (ArgumentException ex)
                        {
                            error = ex.Message;
                        }
                        if (error == null) valid.Add(q);
                        else errors.Add(new { index = i, error });
                    }
                    if (valid.Count > 0) _questions.InsertMany(valid);
                    return Out(new { created = valid.Select(q => new { id = q.Id, type = TypeName(q.Type), summary = q.Summary(60) }).ToList(), errors },
                        $"新建 {valid.Count} 道题目" + (errors.Count > 0 ? $"，{errors.Count} 道有误未添加" : ""));
                });

            Add(L, "update_question", "修改题目",
                "修改一道题目，只需传入要修改的字段。修改不影响已经创建的考试（考试中的题目在创建时已固定）。",
                QuestionSchema(true), a =>
                {
                    var id = a.Long("id");
                    var q = _questions.Get(id) ?? throw new ArgumentException($"题目 {id} 不存在");
                    var error = FillQuestion(q, a);
                    if (error != null) throw new ArgumentException(error);
                    _questions.Update(q);
                    return Out(QuestionDetail(q), $"修改题目 #{id}");
                });

            Add(L, "delete_questions", "删除题目",
                "从题库删除题目。已创建的考试不受影响；引用这些题目的试卷会提示题目已删除。",
                Schema.Object().IntArray("ids", "要删除的题目 id", true), a =>
                {
                    var ids = a.LongList("ids", true);
                    var n = _questions.Delete(ids);
                    return Out(new { deleted = n }, $"删除 {n} 道题目");
                }, destructive: true);

            Add(L, "import_questions_markdown", "导入 Markdown 题目",
                "按 Markdown 题库格式批量导入题目，可同时生成一份试卷。格式：每题以 `## [题型 分值] 题干` 开头；选择题用 `- [ ]` / `- [x]` 列出选项（x 为正确）或 `A. 选项` 加 `答案: B`；"
                + "判断题 `答案: 对`；填空题题干用 ___ 标空，每空一行 `答案: 北京 | 北京市`；简答题 `答案:` 后写参考答案；可选 `解析:`、`标签: a, b`。不支持图片。",
                Schema.Object()
                    .Str("markdown", "Markdown 题库内容", true)
                    .StrArray("extra_tags", "给导入的每道题额外加上的标签")
                    .Str("paper_name", "同时用导入的题目创建试卷时的试卷名称（不传则不创建试卷）")
                    .NullableInt("paper_group_id", "新试卷所属分组 id")
                    .Num("default_score", "题目没有写分值时使用的分值，默认 2"), a =>
                {
                    var parsed = QuestionMarkdown.Parse(a.Str("markdown", true));
                    var extra = a.StrList("extra_tags") ?? new List<string>();
                    var valid = parsed.Questions.Where(p => p.IsValid).ToList();
                    foreach (var p in valid)
                        foreach (var t in extra.Select(t => t.Trim()).Where(t => t.Length > 0))
                            if (!p.Question.Tags.Contains(t)) p.Question.Tags.Add(t);
                    var groupId = GroupArg(a, "paper_group_id");
                    var paperName = a.Str("paper_name");
                    if (valid.Count > 0) _questions.InsertMany(valid.Select(p => p.Question));
                    Paper paper = null;
                    if (!string.IsNullOrWhiteSpace(paperName) && valid.Count > 0)
                    {
                        var score = a.Double("default_score") ?? 2;
                        paper = new Paper
                        {
                            Name = paperName.Trim(),
                            GroupId = groupId,
                            Items = valid.Select(p => new PaperItem { QuestionId = p.Question.Id, Score = p.Score ?? score }).ToList(),
                        };
                        _papers.Save(paper);
                    }
                    return Out(new
                    {
                        imported = valid.Count,
                        ids = valid.Select(p => p.Question.Id).ToList(),
                        errors = parsed.Questions.Where(p => !p.IsValid).Select(p => new { line = p.Line, error = p.Error }).ToList(),
                        warnings = parsed.Warnings,
                        paper = paper == null ? null : new { id = paper.Id, name = paper.Name, totalScore = paper.TotalScore },
                    }, $"导入 {valid.Count} 道题目" + (paper != null ? $"，创建试卷“{paper.Name}”" : ""));
                });

            var itemSchema = Schema.Object().Int("question_id", "题目 id", true).Num("score", "该题分值", true);

            Add(L, "create_paper", "创建试卷",
                "用题库中的题目创建试卷，题目顺序即列表顺序。",
                Schema.Object()
                    .Str("name", "试卷名称", true)
                    .Str("description", "说明（仅教师可见）")
                    .NullableInt("group_id", "所属分组 id（不传为未分组）")
                    .ObjArray("items", "题目及分值", itemSchema, true), a =>
                {
                    var paper = new Paper
                    {
                        Name = a.Str("name", true).Trim(),
                        Description = a.Str("description") ?? "",
                        GroupId = GroupArg(a, "group_id"),
                        Items = ReadItems(a, "items"),
                    };
                    _papers.Save(paper);
                    return Out(new { id = paper.Id, name = paper.Name, questionCount = paper.Items.Count, totalScore = paper.TotalScore },
                        $"创建试卷“{paper.Name}”（{paper.Items.Count} 题）");
                });

            Add(L, "update_paper", "修改试卷",
                "修改试卷名称、说明、分组或题目。items 会整体替换题目列表，append_items 在末尾追加；group_id 传 null 表示移出分组。",
                Schema.Object()
                    .Int("paper_id", "试卷 id", true)
                    .Str("name", "新名称")
                    .Str("description", "新说明")
                    .NullableInt("group_id", "所属分组 id，null 表示未分组")
                    .ObjArray("items", "替换后的题目及分值", itemSchema)
                    .ObjArray("append_items", "追加的题目及分值", itemSchema), a =>
                {
                    var paper = RequirePaper(a.Long("paper_id"));
                    if (a.Has("name") && !string.IsNullOrWhiteSpace(a.Str("name"))) paper.Name = a.Str("name").Trim();
                    if (a.Has("description")) paper.Description = a.Str("description") ?? "";
                    if (a.Has("group_id")) paper.GroupId = GroupArg(a, "group_id");
                    if (a.Has("items")) paper.Items = ReadItems(a, "items");
                    if (a.Has("append_items"))
                    {
                        var existing = new HashSet<long>(paper.Items.Select(i => i.QuestionId));
                        paper.Items.AddRange(ReadItems(a, "append_items").Where(i => !existing.Contains(i.QuestionId)));
                    }
                    _papers.Save(paper);
                    return Out(new { id = paper.Id, name = paper.Name, questionCount = paper.Items.Count, totalScore = paper.TotalScore }, $"修改试卷“{paper.Name}”");
                });

            Add(L, "random_paper", "随机组卷",
                "按题型、标签、数量随机从题库抽题生成新试卷。题目不足时尽量多抽并返回提示。",
                Schema.Object()
                    .Str("name", "试卷名称", true)
                    .NullableInt("group_id", "所属分组 id")
                    .ObjArray("rules", "抽题规则", Schema.Object()
                        .Str("type", "题型", true, TypeNames)
                        .Str("tag", "限定标签（可选）")
                        .Int("count", "抽取数量", true, 1, 200)
                        .Num("score_each", "每题分值", true), true), a =>
                {
                    var rules = a.ObjList("rules", true).Select(r => new RandomDrawRule
                    {
                        Type = ParseType(r.Str("type", true)),
                        Tag = r.Str("tag") ?? "",
                        Count = r.Int("count", 0, 1, 200),
                        ScoreEach = r.Double("score_each") ?? throw new ArgumentException("缺少参数 score_each"),
                    }).ToList();
                    var groupId = GroupArg(a, "group_id");
                    var draw = _paperService.Draw(rules, null);
                    if (draw.Items.Count == 0) throw new InvalidOperationException("没有抽到任何题目：" + string.Join("；", draw.Warnings));
                    var paper = new Paper { Name = a.Str("name", true).Trim(), GroupId = groupId, Items = draw.Items };
                    _papers.Save(paper);
                    return Out(new { id = paper.Id, name = paper.Name, questionCount = paper.Items.Count, totalScore = paper.TotalScore, warnings = draw.Warnings },
                        $"随机组卷“{paper.Name}”（{paper.Items.Count} 题）");
                });

            Add(L, "create_paper_group", "新建试卷分组", "新建试卷分组（如“高一数学”“期中考试”）。",
                Schema.Object().Str("name", "分组名称", true), a =>
                {
                    var name = a.Str("name", true);
                    var id = _papers.AddGroup(name);
                    return Out(new { id, name = name.Trim() }, $"新建试卷分组“{name.Trim()}”");
                });

            Add(L, "move_papers_to_group", "移动试卷到分组", "把试卷移动到某个分组；group_id 为 null 表示移出分组。",
                Schema.Object().IntArray("paper_ids", "试卷 id", true).NullableInt("group_id", "目标分组 id，null 表示未分组", true), a =>
                {
                    var ids = a.LongList("paper_ids", true);
                    var n = _papers.MoveToGroup(ids, GroupArg(a, "group_id"));
                    return Out(new { moved = n }, $"移动 {n} 份试卷");
                });

            Add(L, "create_exam", "创建考试",
                "用试卷创建考试（创建后为“未开始”，需要老师在监考页面或用 open_exam 开放登录）。题目内容在创建时固定。",
                Schema.Object()
                    .Int("paper_id", "试卷 id", true)
                    .Str("title", "考试名称（默认用试卷名称）")
                    .IntArray("class_ids", "参加的班级 id；不传表示全部班级")
                    .Int("duration_minutes", "考试时长（分钟），默认 60", false, 1, 1440)
                    .Str("multiple_scoring", "多选题计分规则，默认“少选得一半”", false, ModeNames)
                    .Bool("shuffle_questions", "题目乱序，默认 false")
                    .Bool("shuffle_options", "选项乱序，默认 false")
                    .Bool("show_score_after_submit", "交卷后向学生显示客观题得分，默认 true")
                    .Bool("anti_cheat", "防作弊（全屏置顶、切屏上报），默认 true"), a =>
                {
                    var paper = RequirePaper(a.Long("paper_id"));
                    var classIds = a.LongList("class_ids") ?? _roster.ListClasses().Select(c => c.Id).ToList();
                    var known = new HashSet<long>(_roster.ListClasses().Select(c => c.Id));
                    var unknown = classIds.Where(id => !known.Contains(id)).ToList();
                    if (unknown.Count > 0) throw new ArgumentException("班级不存在：" + string.Join(", ", unknown));
                    var settings = new ExamSettings
                    {
                        DurationMinutes = a.Int("duration_minutes", 60, 1, 1440),
                        MultipleMode = ParseMode(a.Str("multiple_scoring")),
                        ShuffleQuestions = a.Bool("shuffle_questions", false),
                        ShuffleOptions = a.Bool("shuffle_options", false),
                        ShowScoreAfterSubmit = a.Bool("show_score_after_submit", true),
                        AntiCheat = a.Bool("anti_cheat", true),
                    };
                    var id = _paperService.CreateExam(paper, a.Str("title"), classIds, settings, out var missing);
                    var exam = _exams.Get(id);
                    return Out(new { id, title = exam.Title, questionCount = exam.Questions.Count, totalScore = exam.TotalScore, missingQuestions = missing, status = ExamTexts.StatusName(exam.Status) },
                        $"创建考试“{exam.Title}”");
                });
        }

        private static MultipleScoringMode ParseMode(string text)
        {
            switch ((text ?? "").Trim())
            {
                case "":
                case "少选得一半":
                    return MultipleScoringMode.HalfForPartial;
                case "全对才得分":
                    return MultipleScoringMode.AllOrNothing;
                case "按比例给分":
                case "按比例":
                    return MultipleScoringMode.Proportional;
                default:
                    throw new ArgumentException($"未知的多选题计分规则“{text}”，可用：{string.Join("、", ModeNames)}");
            }
        }

        private List<PaperItem> ReadItems(McpArgs a, string name)
        {
            var items = (a.ObjList(name) ?? new List<McpArgs>()).Select(x => new PaperItem
            {
                QuestionId = x.Long("question_id"),
                Score = x.Double("score") ?? throw new ArgumentException("缺少参数 score"),
            }).ToList();
            if (items.Any(i => i.Score < 0 || i.Score > 1000)) throw new ArgumentException("分值应在 0 ~ 1000 之间");
            var dup = items.GroupBy(i => i.QuestionId).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ArgumentException($"题目 {dup.Key} 重复");
            var found = _questions.GetMany(items.Select(i => i.QuestionId));
            var missing = items.Where(i => !found.ContainsKey(i.QuestionId)).Select(i => i.QuestionId).ToList();
            if (missing.Count > 0) throw new ArgumentException("题目不存在：" + string.Join(", ", missing));
            return items;
        }

        // ================= 考试控制与改分 =================

        private void RegisterControlTools()
        {
            const McpPermissions L = McpPermissions.Control;

            Add(L, "open_exam", "开放登录",
                "开放一场未开始的考试，学生端可以登录候考（同一时间只能主持一场考试）。",
                Schema.Object().Int("exam_id", "考试 id", true), a =>
                {
                    var id = a.Long("exam_id");
                    _host.OpenExam(id);
                    var exam = _host.CurrentExam;
                    return Out(new { id, title = exam?.Title, status = ExamTexts.StatusName(exam?.Status ?? ExamStatus.Open), participants = _host.GetParticipants().Count },
                        $"开放考试“{exam?.Title}”");
                });

            Add(L, "start_exam", "开始考试", "开始当前候考中的考试：下发试卷并开始倒计时。", Schema.Object(), a =>
            {
                _host.StartExam();
                var exam = _host.CurrentExam;
                return Out(new { id = exam.Id, title = exam.Title, endAt = exam.DeadlineUtc }, $"开始考试“{exam.Title}”");
            });

            Add(L, "adjust_exam_time", "加减考试时间",
                "考试中延长（正数）或缩短（负数）考试时间，学生端倒计时立即同步。缩短后剩余时间不能少于 1 分钟。",
                Schema.Object().Int("minutes", "调整的分钟数，正数延长、负数缩短", true, -600, 600), a =>
                {
                    var minutes = a.Int("minutes", 0, -600, 600);
                    var deadline = _host.AdjustTime(minutes);
                    return Out(new { minutes, endAt = deadline, totalMinutes = _host.CurrentExam?.TotalMinutes },
                        $"考试时间{(minutes > 0 ? "延长" : "缩短")} {Math.Abs(minutes)} 分钟");
                });

            Add(L, "end_exam", "结束考试", "立即结束当前考试，未交卷的考生按已保存的答案自动收卷。", Schema.Object(), a =>
            {
                var exam = _host.CurrentExam;
                _host.EndExam();
                return Out(new { id = exam?.Id, title = exam?.Title, status = "已结束" }, $"结束考试“{exam?.Title}”");
            }, destructive: true);

            Add(L, "close_exam", "关闭考试",
                "关闭监考页面中的考试：候考中的考试取消登录（回到未开始），已结束的考试从监考页移除。考试进行中不能关闭。",
                Schema.Object(), a =>
                {
                    var exam = _host.CurrentExam;
                    _host.CloseExam();
                    return Out(new { closed = exam != null, id = exam?.Id }, exam == null ? "没有需要关闭的考试" : $"关闭考试“{exam.Title}”");
                });

            Add(L, "set_manual_score", "人工改分",
                "给某名考生的某道题打分（任何题型都可以改分）；score 传 null 表示清除人工分、恢复自动判分。",
                Schema.Object()
                    .Int("exam_id", "考试 id", true)
                    .Str("student_no", "学号", true)
                    .Int("question_id", "题目 id（考试中的题目 id，可用 get_student_answers 查看）", true)
                    .Num("score", "得分（0 ~ 该题满分），null 表示恢复自动判分", true, nullable: true), a =>
                {
                    var exam = RequireExam(a.Long("exam_id"));
                    var no = a.Str("student_no", true).Trim();
                    var p = _exams.GetParticipant(exam.Id, no) ?? throw new ArgumentException($"学号 {no} 不在这场考试的名单中");
                    if (p.Status != ParticipantStatus.Submitted) throw new InvalidOperationException($"{p.Name} 还没有交卷，交卷后才能改分");
                    var qid = a.Long("question_id");
                    var q = exam.FindQuestion(qid) ?? throw new ArgumentException($"这场考试中没有题目 {qid}");
                    var score = a.Double("score");
                    _results.SetManualScore(exam, no, qid, score);
                    var after = _exams.GetParticipant(exam.Id, no);
                    var number = exam.Questions.IndexOf(q) + 1;
                    return Out(new { studentNo = no, name = p.Name, questionNumber = number, score, totalScore = after.TotalScore, pendingManual = after.PendingManual },
                        score.HasValue ? $"改分：{p.Name} 第 {number} 题 {score.Value.ToString("0.##", CultureInfo.InvariantCulture)} 分" : $"恢复自动判分：{p.Name} 第 {number} 题");
                });

            Add(L, "allow_student_relogin", "允许换电脑登录",
                "允许考生换到其他电脑重新登录：当前电脑退出，已保存的答案保留。",
                Schema.Object().Str("student_no", "学号", true), a =>
                {
                    var no = a.Str("student_no", true).Trim();
                    if (_host.CurrentExam == null) throw new InvalidOperationException("当前没有开放的考试");
                    if (_host.GetParticipants().All(p => !string.Equals(p.StudentNo, no, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"学号 {no} 不在当前考试的名单中");
                    _host.ReleaseStudent(no);
                    return Out(new { studentNo = no, released = true }, $"允许 {no} 换电脑登录");
                });
        }
    }
}
