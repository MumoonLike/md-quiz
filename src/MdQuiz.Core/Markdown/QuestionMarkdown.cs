using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MdQuiz.Core.Models;

namespace MdQuiz.Core.Markdown
{
    public class ParsedQuestion
    {
        public Question Question { get; set; }
        /// <summary>文件中写明的分值（如 [单选 2分]），未写时为 null。</summary>
        public double? Score { get; set; }
        /// <summary>题目标题所在行号（从 1 开始）。</summary>
        public int Line { get; set; }
        public string Error { get; set; }
        public bool IsValid => Error == null;
    }

    public class ParseResult
    {
        /// <summary>文件开头的一级标题（如有）。</summary>
        public string Title { get; set; }
        public List<ParsedQuestion> Questions { get; } = new List<ParsedQuestion>();
        public List<string> Warnings { get; } = new List<string>();
        public int ValidCount => Questions.Count(q => q.IsValid);
    }

    /// <summary>
    /// Markdown 题库格式的解析与生成。格式示例见 samples/示例题库.md：
    /// <code>
    /// ## [单选 2分] 下列哪个是质数？
    /// - [ ] 4
    /// - [x] 7
    /// 解析: 7 只能被 1 和它本身整除。
    /// 标签: 数学, 质数
    /// </code>
    /// </summary>
    public static class QuestionMarkdown
    {
        private static readonly Regex QuestionHeading = new Regex(
            @"^\s{0,3}#{1,6}\s*(?:\d+\s*[.、．)）]\s*)?[\[【](?<meta>[^\]】]*)[\]】]\s*(?<rest>.*)$", RegexOptions.Compiled);
        private static readonly Regex AnyHeading = new Regex(@"^\s{0,3}(?<level>#{1,6})\s+(?<text>.*)$", RegexOptions.Compiled);
        private static readonly Regex Fence = new Regex(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled);
        private static readonly Regex TaskOption = new Regex(@"^\s{0,3}[-*+]\s+\[(?<mark>[ xX√✓✔])\]\s?(?<text>.*)$", RegexOptions.Compiled);
        private static readonly Regex LetterOption = new Regex(@"^\s{0,3}[(（]?(?<key>[A-J])\s*[)）.．、]\s*(?<text>.*\S.*)$", RegexOptions.Compiled);
        private static readonly Regex Meta = new Regex(
            @"^\s{0,3}(?<key>正确答案|参考答案|答案|答案解析|解析|标签|知识点|分值|分数|answer|analysis|explanation|tags?|score)\s*[:：]\s?(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ScoreToken = new Regex(@"^(?<n>\d+(?:\.\d+)?)\s*分?$", RegexOptions.Compiled);
        private static readonly char[] MetaSeparators = { ' ', '\t', '|', '｜', ',', '，', '、', '/', ';', '；' };
        private static readonly char[] TagSeparators = { ',', '，', '、', ';', '；', ' ', '\t' };

        private enum Field { Stem, Option, Meta, Ignored }

        private sealed class MetaEntry
        {
            public string Key;
            public readonly List<string> Lines = new List<string>();
        }

        private sealed class OptionEntry
        {
            public string Key;
            public bool Marked;
            public bool IsTask;
            public readonly StringBuilder Text = new StringBuilder();
        }

        private sealed class Block
        {
            public int Line;
            public string MetaText;
            public readonly List<string> Stem = new List<string>();
            public readonly List<OptionEntry> Options = new List<OptionEntry>();
            public readonly List<MetaEntry> Metas = new List<MetaEntry>();
            public QuestionType? Type;
            public double? Score;
        }

        public static ParseResult Parse(string text)
        {
            var result = new ParseResult();
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Block block = null;
            var field = Field.Ignored;
            var inFence = false;
            var prevBlank = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (i == 0) line = line.TrimStart('﻿');
                var lineNo = i + 1;

                if (!inFence)
                {
                    var qh = QuestionHeading.Match(line);
                    if (qh.Success && IsHeadingLine(line))
                    {
                        if (block != null) result.Questions.Add(Build(block, result));
                        block = new Block { Line = lineNo, MetaText = qh.Groups["meta"].Value };
                        ParseHeadingMeta(block);
                        var rest = qh.Groups["rest"].Value.TrimEnd();
                        if (rest.Length > 0) block.Stem.Add(rest);
                        field = Field.Stem;
                        prevBlank = false;
                        continue;
                    }

                    var h = AnyHeading.Match(line);
                    if (h.Success)
                    {
                        if (block != null) result.Questions.Add(Build(block, result));
                        block = null;
                        field = Field.Ignored;
                        if (result.Title == null && result.Questions.Count == 0 && h.Groups["level"].Value.Length == 1)
                            result.Title = h.Groups["text"].Value.Trim();
                        continue;
                    }
                }

                if (block == null) continue;

                var isFenceLine = Fence.IsMatch(line);
                if (inFence || isFenceLine)
                {
                    if (isFenceLine) inFence = !inFence;
                    AppendToField(block, ref field, line);
                    prevBlank = false;
                    continue;
                }

                var meta = Meta.Match(line);
                if (meta.Success)
                {
                    var entry = new MetaEntry { Key = NormalizeMetaKey(meta.Groups["key"].Value) };
                    var value = meta.Groups["value"].Value.TrimEnd();
                    if (value.Length > 0) entry.Lines.Add(value);
                    block.Metas.Add(entry);
                    field = Field.Meta;
                    prevBlank = false;
                    continue;
                }

                if (field == Field.Stem || field == Field.Option)
                {
                    var allowOptions = block.Type == null || QuestionTypes.HasOptions(block.Type.Value) || block.Type == QuestionType.TrueFalse;
                    var task = allowOptions ? TaskOption.Match(line) : Match.Empty;
                    if (task.Success)
                    {
                        var opt = new OptionEntry { IsTask = true, Marked = task.Groups["mark"].Value.Trim().Length > 0 };
                        opt.Text.Append(task.Groups["text"].Value.Trim());
                        block.Options.Add(opt);
                        field = Field.Option;
                        prevBlank = false;
                        continue;
                    }

                    var letter = allowOptions && block.Type != QuestionType.TrueFalse && !block.Options.Any(o => o.IsTask)
                        ? LetterOption.Match(line) : Match.Empty;
                    if (letter.Success && (block.Options.Count > 0 || letter.Groups["key"].Value == "A"))
                    {
                        AddLetterOptions(block, letter.Groups["key"].Value, letter.Groups["text"].Value);
                        field = Field.Option;
                        prevBlank = false;
                        continue;
                    }
                }

                var blank = string.IsNullOrWhiteSpace(line);
                if (field == Field.Option)
                {
                    if (blank) { prevBlank = true; continue; }
                    if (!prevBlank || line.StartsWith("  ") || line.StartsWith("\t"))
                    {
                        var last = block.Options[block.Options.Count - 1];
                        last.Text.Append('\n').Append(StripIndent(line));
                        continue;
                    }
                    result.Warnings.Add($"第 {lineNo} 行：选项之后的内容无法识别，已忽略（解析、答案请写成“答案: …”“解析: …”）");
                    field = Field.Ignored;
                    continue;
                }

                AppendToField(block, ref field, line);
                prevBlank = blank;
            }

            if (block != null) result.Questions.Add(Build(block, result));
            return result;
        }

        private static bool IsHeadingLine(string line) => line.TrimStart().StartsWith("#");

        private static string StripIndent(string line)
        {
            if (line.StartsWith("\t")) return line.Substring(1);
            var n = 0;
            while (n < line.Length && n < 2 && line[n] == ' ') n++;
            return line.Substring(n);
        }

        private static void AppendToField(Block block, ref Field field, string line)
        {
            switch (field)
            {
                case Field.Stem:
                    block.Stem.Add(line);
                    break;
                case Field.Option:
                    block.Options[block.Options.Count - 1].Text.Append('\n').Append(StripIndent(line));
                    break;
                case Field.Meta:
                    block.Metas[block.Metas.Count - 1].Lines.Add(line);
                    break;
            }
        }

        private static void AddLetterOptions(Block block, string key, string text)
        {
            // 支持一行写多个选项：A. 4   B. 7   C. 9
            var current = key[0];
            var remaining = text;
            while (true)
            {
                var next = (char)(current + 1);
                var m = Regex.Match(remaining, @"\s+[(（]?" + next + @"\s*[)）.．、]\s*");
                var opt = new OptionEntry { Key = current.ToString() };
                if (m.Success && next <= 'J' && m.Index > 0)
                {
                    opt.Text.Append(remaining.Substring(0, m.Index).Trim());
                    block.Options.Add(opt);
                    remaining = remaining.Substring(m.Index + m.Length);
                    current = next;
                    continue;
                }
                opt.Text.Append(remaining.Trim());
                block.Options.Add(opt);
                break;
            }
        }

        private static string NormalizeMetaKey(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "正确答案": case "参考答案": case "答案": case "answer": return "answer";
                case "答案解析": case "解析": case "analysis": case "explanation": return "analysis";
                case "标签": case "知识点": case "tag": case "tags": return "tags";
                case "分值": case "分数": case "score": return "score";
                default: return key;
            }
        }

        private static void ParseHeadingMeta(Block block)
        {
            foreach (var token in block.MetaText.Split(MetaSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var sm = ScoreToken.Match(token);
                if (sm.Success)
                {
                    block.Score = double.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (QuestionTypes.TryParse(token, out var t)) block.Type = t;
                else
                {
                    // “单选2分”这类连写
                    var m = Regex.Match(token, @"^(?<t>\D+?)(?<n>\d+(?:\.\d+)?)分?$");
                    if (m.Success && QuestionTypes.TryParse(m.Groups["t"].Value, out t))
                    {
                        block.Type = t;
                        block.Score = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        private static string JoinTrim(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            while (list.Count > 0 && string.IsNullOrWhiteSpace(list[0])) list.RemoveAt(0);
            while (list.Count > 0 && string.IsNullOrWhiteSpace(list[list.Count - 1])) list.RemoveAt(list.Count - 1);
            return string.Join("\n", list.Select(l => l.TrimEnd()));
        }

        private static ParsedQuestion Build(Block block, ParseResult result)
        {
            var q = new Question { Stem = JoinTrim(block.Stem) };
            var pq = new ParsedQuestion { Question = q, Line = block.Line, Score = block.Score };
            if (block.Type == null)
            {
                pq.Error = $"无法识别题型“{block.MetaText}”，可用：单选、多选、判断、填空、简答";
                return pq;
            }
            q.Type = block.Type.Value;

            List<string> MetaLines(string key) => block.Metas.Where(m => m.Key == key).SelectMany(m => m.Lines).ToList();
            var answerLines = MetaLines("answer");
            q.Analysis = JoinTrim(MetaLines("analysis"));
            q.Tags = MetaLines("tags")
                .SelectMany(l => l.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            var scoreText = MetaLines("score").FirstOrDefault();
            if (scoreText != null)
            {
                var sm = ScoreToken.Match(scoreText.Trim());
                if (sm.Success) pq.Score = double.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture);
                else result.Warnings.Add($"第 {block.Line} 行的题目：分值“{scoreText}”无法识别");
            }

            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                {
                    q.Options = block.Options.Select(o => o.Text.ToString().Trim()).ToList();
                    var marked = block.Options.Select((o, idx) => new { o, idx }).Where(x => x.o.Marked).Select(x => Question.OptionKey(x.idx)).ToList();
                    if (marked.Count > 0) q.Answer = Question.NormalizeKeys(marked);
                    else
                    {
                        // 只取“答案:”首行开头的字母部分，如 “AC”“A、C”“A,C（注：…）”
                        var prefix = Regex.Match(answerLines.FirstOrDefault() ?? "", @"^[\sA-Ja-j,，、/;；]+").Value;
                        q.Answer = Question.NormalizeKeys(prefix.ToUpperInvariant().Where(c => c >= 'A' && c <= 'J').Select(c => c.ToString()));
                    }
                    break;
                }
                case QuestionType.TrueFalse:
                {
                    bool value;
                    var markedOpt = block.Options.FirstOrDefault(o => o.Marked);
                    if (markedOpt != null && Question.TryParseTrueFalse(markedOpt.Text.ToString(), out value))
                        q.Answer = value ? "T" : "F";
                    else if (answerLines.Count > 0 && Question.TryParseTrueFalse(answerLines[0], out value))
                        q.Answer = value ? "T" : "F";
                    break;
                }
                case QuestionType.FillBlank:
                {
                    var blanks = answerLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                    var count = q.BlankCountInStem();
                    if (blanks.Count == 1 && count > 1 && blanks[0].IndexOfAny(new[] { ';', '；' }) >= 0)
                        blanks = blanks[0].Split(new[] { ';', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    q.Blanks = blanks.Select(b => string.Join("|", Question.BlankAlternatives(b.Replace('｜', '|')))).ToList();
                    break;
                }
                case QuestionType.ShortAnswer:
                    q.Answer = JoinTrim(answerLines);
                    break;
            }

            pq.Error = q.Validate();
            return pq;
        }

        /// <summary>把题目写成 Markdown 题库格式（与 <see cref="Parse"/> 互逆）。</summary>
        public static string Write(IEnumerable<KeyValuePair<Question, double?>> items, string title = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append("# ").Append(title.Trim()).Append("\n\n");
            var number = 0;
            foreach (var pair in items)
            {
                var q = pair.Key;
                number++;
                sb.Append("## ").Append(number).Append(". [").Append(QuestionTypes.ShortName(q.Type));
                if (pair.Value.HasValue) sb.Append(' ').Append(pair.Value.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append('分');
                sb.Append(']');

                var stemLines = (q.Stem ?? "").Replace("\r\n", "\n").Split('\n');
                var first = stemLines[0];
                var inlineFirst = first.Trim().Length > 0 && !Regex.IsMatch(first, @"^\s*(```|~~~|!\[|[-*+]\s|#|\||>|\d+\.\s)");
                if (inlineFirst) sb.Append(' ').Append(first.Trim());
                sb.Append('\n');
                foreach (var l in inlineFirst ? stemLines.Skip(1) : stemLines) sb.Append(l).Append('\n');
                sb.Append('\n');

                switch (q.Type)
                {
                    case QuestionType.Single:
                    case QuestionType.Multiple:
                        var keys = q.CorrectKeys();
                        for (var i = 0; i < q.Options.Count; i++)
                        {
                            var optLines = (q.Options[i] ?? "").Replace("\r\n", "\n").Split('\n');
                            sb.Append(keys.Contains(Question.OptionKey(i)) ? "- [x] " : "- [ ] ").Append(optLines[0]).Append('\n');
                            foreach (var l in optLines.Skip(1)) sb.Append("  ").Append(l).Append('\n');
                        }
                        sb.Append('\n');
                        break;
                    case QuestionType.TrueFalse:
                        sb.Append("答案: ").Append(q.Answer == "T" ? "对" : "错").Append('\n');
                        break;
                    case QuestionType.FillBlank:
                        foreach (var b in q.Blanks) sb.Append("答案: ").Append(b.Replace("|", " | ")).Append('\n');
                        break;
                    case QuestionType.ShortAnswer:
                        if (!string.IsNullOrWhiteSpace(q.Answer)) AppendMultiline(sb, "答案", q.Answer);
                        break;
                }
                if (!string.IsNullOrWhiteSpace(q.Analysis)) AppendMultiline(sb, "解析", q.Analysis);
                if (q.Tags.Count > 0) sb.Append("标签: ").Append(string.Join(", ", q.Tags)).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        private static void AppendMultiline(StringBuilder sb, string key, string value)
        {
            var lines = value.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 1) sb.Append(key).Append(": ").Append(lines[0]).Append('\n');
            else
            {
                sb.Append(key).Append(":\n");
                foreach (var l in lines) sb.Append(l).Append('\n');
            }
        }
    }
}
