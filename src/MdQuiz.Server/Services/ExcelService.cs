using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
using MiniExcelLibs;
using MiniExcelLibs.Csv;

namespace MdQuiz.Server.Services
{
    /// <summary>Excel / CSV 的导入导出（花名册、题目、成绩）。</summary>
    public static class ExcelService
    {
        private static readonly string[] NoHeaders = { "学号", "考号", "编号", "学籍号", "studentno", "no", "id" };
        private static readonly string[] NameHeaders = { "姓名", "名字", "学生姓名", "name" };
        private static readonly string[] ClassHeaders = { "班级", "班级名称", "行政班", "class" };

        // ---------------- 读取 ----------------

        private static List<Dictionary<string, string>> ReadRows(string path)
        {
            var isCsv = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
            IEnumerable<object> rows;
            if (isCsv)
            {
                var text = TextFiles.ReadAllText(path);
                var config = new CsvConfiguration();
                var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
                rows = MiniExcel.Query(stream, useHeaderRow: true, excelType: ExcelType.CSV, configuration: config);
            }
            else
            {
                rows = MiniExcel.Query(path, useHeaderRow: true);
            }
            var result = new List<Dictionary<string, string>>();
            foreach (var obj in rows)
            {
                if (!(obj is IDictionary<string, object> row)) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in row)
                {
                    var key = (kv.Key ?? "").Trim();
                    if (key.Length == 0) continue;
                    dict[key] = CellText(kv.Value);
                }
                result.Add(dict);
            }
            return result;
        }

        private static string CellText(object value)
        {
            switch (value)
            {
                case null: return "";
                case double d: return d.ToString("0.##########", CultureInfo.InvariantCulture);
                case float f: return f.ToString("0.######", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString("0.##########", CultureInfo.InvariantCulture);
                case DateTime dt: return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                default: return value.ToString().Trim();
            }
        }

        private static string Pick(Dictionary<string, string> row, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                if (row.TryGetValue(n, out var v)) return v;
            }
            return null;
        }

        // ---------------- 花名册 ----------------

        public static List<RosterRow> ReadRoster(string path)
        {
            var rows = ReadRows(path);
            if (rows.Count > 0 && Pick(rows[0], NoHeaders) == null && !rows[0].Keys.Any(k => NoHeaders.Contains(k, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("没有找到“学号”列。请使用模板：第一行为表头（学号、姓名、班级）");
            var list = new List<RosterRow>();
            var line = 1;
            foreach (var row in rows)
            {
                line++;
                list.Add(new RosterRow { StudentNo = Pick(row, NoHeaders), Name = Pick(row, NameHeaders), ClassName = Pick(row, ClassHeaders), Line = line });
            }
            return list;
        }

        public static void WriteRosterTemplate(string path)
        {
            var rows = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["学号"] = "20240101", ["姓名"] = "张三", ["班级"] = "高一(1)班" },
                new Dictionary<string, object> { ["学号"] = "20240102", ["姓名"] = "李四", ["班级"] = "高一(1)班" },
                new Dictionary<string, object> { ["学号"] = "20240201", ["姓名"] = "王五", ["班级"] = "高一(2)班" },
            };
            MiniExcel.SaveAs(path, rows, sheetName: "花名册", overwriteFile: true);
        }

        // ---------------- 题目 ----------------

        private static readonly string[] OptionLetters = Enumerable.Range(0, Question.MaxOptions).Select(Question.OptionKey).ToArray();

        /// <summary>从 Excel 读取题目。每行一题；第一行为表头。</summary>
        public static ParseResult ReadQuestions(string path)
        {
            var result = new ParseResult();
            var line = 1;
            foreach (var row in ReadRows(path))
            {
                line++;
                var typeText = Pick(row, new[] { "题型", "类型", "type" }) ?? "";
                var stem = (Pick(row, new[] { "题干", "题目", "stem" }) ?? "").Replace("\r\n", "\n");
                if (typeText.Length == 0 && stem.Length == 0) continue;
                var q = new Question { Stem = stem.Trim() };
                var pq = new ParsedQuestion { Question = q, Line = line };
                result.Questions.Add(pq);
                if (!QuestionTypes.TryParse(typeText, out var type))
                {
                    pq.Error = $"无法识别题型“{typeText}”";
                    continue;
                }
                q.Type = type;
                var answer = (Pick(row, new[] { "答案", "正确答案", "参考答案", "answer" }) ?? "").Trim();
                q.Analysis = (Pick(row, new[] { "解析", "答案解析", "analysis" }) ?? "").Trim();
                q.Tags = (Pick(row, new[] { "标签", "知识点", "tags" }) ?? "")
                    .Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Distinct().ToList();
                var scoreText = Pick(row, new[] { "分值", "分数", "score" });
                if (!string.IsNullOrWhiteSpace(scoreText))
                {
                    if (double.TryParse(scoreText.Trim().TrimEnd('分'), NumberStyles.Float, CultureInfo.InvariantCulture, out var score)) pq.Score = score;
                    else result.Warnings.Add($"第 {line} 行：分值“{scoreText}”无法识别");
                }

                switch (type)
                {
                    case QuestionType.Single:
                    case QuestionType.Multiple:
                        foreach (var letter in OptionLetters)
                        {
                            var opt = Pick(row, new[] { "选项" + letter, letter, "选项 " + letter });
                            if (!string.IsNullOrWhiteSpace(opt)) q.Options.Add(opt.Trim());
                        }
                        q.Answer = Question.NormalizeKeys(answer.ToUpperInvariant().Where(c => c >= 'A' && c <= 'J').Select(c => c.ToString()));
                        break;
                    case QuestionType.TrueFalse:
                        if (Question.TryParseTrueFalse(answer, out var tf)) q.Answer = tf ? "T" : "F";
                        break;
                    case QuestionType.FillBlank:
                        q.Blanks = answer.Replace('｜', '|').Split(new[] { ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(b => string.Join("|", Question.BlankAlternatives(b))).Where(b => b.Length > 0).ToList();
                        break;
                    case QuestionType.ShortAnswer:
                        q.Answer = answer;
                        break;
                }
                pq.Error = q.Validate();
            }
            return result;
        }

        public static void WriteQuestionTemplate(string path)
        {
            Dictionary<string, object> Row(string type, string stem, string[] options, string answer, string analysis, string tags, double score)
            {
                var d = new Dictionary<string, object> { ["题型"] = type, ["题干"] = stem };
                for (var i = 0; i < 6; i++) d["选项" + OptionLetters[i]] = options != null && i < options.Length ? options[i] : "";
                d["答案"] = answer;
                d["解析"] = analysis;
                d["标签"] = tags;
                d["分值"] = score;
                return d;
            }

            var rows = new List<Dictionary<string, object>>
            {
                Row("单选", "下列哪个数是质数？", new[] { "4", "7", "9", "15" }, "B", "7 只能被 1 和它本身整除。", "数学,质数", 2),
                Row("多选", "下列属于传输层协议的是？", new[] { "TCP", "UDP", "IP", "HTTP" }, "AB", "", "网络", 4),
                Row("判断", "地球绕太阳公转一周约为一年。", null, "对", "", "地理", 1),
                Row("填空", "中国的首都是 ___，最大的城市是 ___。", null, "北京|北京市；上海|上海市", "多个空用“；”分隔，同一个空的多个可接受答案用“|”分隔", "地理", 4),
                Row("简答", "简述 TCP 三次握手的过程。", null, "客户端发送 SYN，服务器回复 SYN+ACK，客户端再回复 ACK。", "", "网络", 10),
            };
            MiniExcel.SaveAs(path, rows, sheetName: "题目", overwriteFile: true);
        }

        // ---------------- 成绩 ----------------

        public static void ExportResults(string path, ResultTable table)
        {
            var exam = table.Exam;
            var summary = new List<Dictionary<string, object>>();
            var detail = new List<Dictionary<string, object>>();
            var order = 0;
            foreach (var row in table.Rows)
            {
                var p = row.Participant;
                var d = new Dictionary<string, object>
                {
                    ["序号"] = ++order,
                    ["学号"] = p.StudentNo,
                    ["姓名"] = p.Name,
                    ["班级"] = p.ClassName,
                    ["状态"] = p.Status == ParticipantStatus.Submitted ? (p.Forced ? "自动收卷" : "已交卷") : p.Status == ParticipantStatus.Absent ? "缺考" : ExamTexts.ParticipantStatusName(p.Status),
                    ["客观题得分"] = p.ObjectiveScore,
                    ["主观题得分"] = p.SubjectiveScore,
                    ["总分"] = p.TotalScore,
                    ["待批改"] = p.PendingManual > 0 ? $"{p.PendingManual} 题" : "",
                    ["切屏次数"] = p.FocusLost,
                    ["交卷时间"] = p.SubmittedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                };
                var number = 0;
                foreach (var q in exam.Questions)
                {
                    number++;
                    row.Answers.TryGetValue(q.Id, out var a);
                    d[$"第{number}题({QuestionTypes.ShortName(q.Type)} {q.Score:0.##}分)"] = a?.Score;
                }
                summary.Add(d);

                number = 0;
                foreach (var q in exam.Questions)
                {
                    number++;
                    row.Answers.TryGetValue(q.Id, out var a);
                    detail.Add(new Dictionary<string, object>
                    {
                        ["学号"] = p.StudentNo,
                        ["姓名"] = p.Name,
                        ["题号"] = number,
                        ["题型"] = QuestionTypes.ShortName(q.Type),
                        ["题目"] = q.Summary(60),
                        ["学生答案"] = a == null ? "" : AnswerFormatter.Format(q, a.Values),
                        ["正确答案"] = AnswerFormatter.FormatCorrect(q),
                        ["得分"] = a?.Score,
                        ["满分"] = q.Score,
                    });
                }
            }
            var sheets = new Dictionary<string, object> { ["成绩"] = summary, ["答题明细"] = detail };
            MiniExcel.SaveAs(path, sheets, overwriteFile: true);
        }
    }
}
