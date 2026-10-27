using System;
using System.Collections.Generic;
using System.Linq;

namespace MdQuiz.Core.Models
{
    /// <summary>
    /// 题库中的一道题（含标准答案）。
    /// <para>答案约定：</para>
    /// <list type="bullet">
    /// <item>单选 / 多选：<see cref="Answer"/> 为选项字母，如 "B"、"ACD"（按字母排序）。</item>
    /// <item>判断：<see cref="Answer"/> 为 "T"（对）或 "F"（错）。</item>
    /// <item>填空：<see cref="Blanks"/> 每项对应一个空，多个可接受答案用 "|" 分隔。</item>
    /// <item>简答：<see cref="Answer"/> 为参考答案（Markdown），由教师人工批改。</item>
    /// </list>
    /// </summary>
    public class Question
    {
        public const int MaxOptions = 10;

        public long Id { get; set; }
        public QuestionType Type { get; set; }
        public string Stem { get; set; } = "";
        public List<string> Options { get; set; } = new List<string>();
        public string Answer { get; set; } = "";
        public List<string> Blanks { get; set; } = new List<string>();
        public string Analysis { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static string OptionKey(int index) => ((char)('A' + index)).ToString();

        public static int OptionIndex(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            var c = char.ToUpperInvariant(key[0]);
            return c >= 'A' && c < 'A' + MaxOptions ? c - 'A' : -1;
        }

        /// <summary>选择题的正确选项字母集合。</summary>
        public HashSet<string> CorrectKeys()
        {
            return new HashSet<string>((Answer ?? "")
                .Select(c => char.ToUpperInvariant(c).ToString())
                .Where(k => OptionIndex(k) >= 0));
        }

        /// <summary>把选项字母集合规范化为排序后的字符串，如 {C,A} → "AC"。</summary>
        public static string NormalizeKeys(IEnumerable<string> keys)
        {
            return string.Concat((keys ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => char.ToUpperInvariant(k.Trim()[0]))
                .Where(c => c >= 'A' && c < 'A' + MaxOptions)
                .Distinct()
                .OrderBy(c => c));
        }

        public static IEnumerable<string> BlankAlternatives(string blank)
        {
            return (blank ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0);
        }

        /// <summary>题干中空位（三个及以上下划线）的个数。</summary>
        public int BlankCountInStem() => BlankMarker.Count(Stem);

        public Question Clone()
        {
            var q = (Question)MemberwiseClone();
            q.Options = new List<string>(Options ?? new List<string>());
            q.Blanks = new List<string>(Blanks ?? new List<string>());
            q.Tags = new List<string>(Tags ?? new List<string>());
            return q;
        }

        /// <summary>检查题目是否完整，返回错误描述；完整时返回 null。</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Stem)) return "题干不能为空";
            switch (Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                {
                    if (Options == null || Options.Count < 2) return "选择题至少需要 2 个选项";
                    if (Options.Count > MaxOptions) return $"选项最多 {MaxOptions} 个";
                    if (Options.Any(string.IsNullOrWhiteSpace)) return "选项内容不能为空";
                    var keys = CorrectKeys();
                    if (keys.Count == 0) return "请设置正确答案";
                    if (keys.Any(k => OptionIndex(k) >= Options.Count)) return "正确答案超出选项范围";
                    if (Type == QuestionType.Single && keys.Count != 1) return "单选题只能有一个正确答案";
                    return null;
                }
                case QuestionType.TrueFalse:
                    return Answer == "T" || Answer == "F" ? null : "请设置判断题答案（对 / 错）";
                case QuestionType.FillBlank:
                {
                    var count = BlankCountInStem();
                    if (count == 0) return "填空题题干中需要用 ___（三个及以上下划线）标出空位";
                    if (Blanks == null || Blanks.Count != count) return $"题干中有 {count} 个空，但设置了 {Blanks?.Count ?? 0} 个答案";
                    if (Blanks.Any(b => !BlankAlternatives(b).Any())) return "每个空都需要设置答案";
                    return null;
                }
                case QuestionType.ShortAnswer:
                    return null;
                default:
                    return "未知题型";
            }
        }

        /// <summary>判断题答案文字解析：对/正确/√/T 等。</summary>
        public static bool TryParseTrueFalse(string text, out bool value)
        {
            value = false;
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "对": case "正确": case "是": case "√": case "✓": case "✔": case "t": case "true": case "yes": case "y": case "1":
                    value = true; return true;
                case "错": case "错误": case "否": case "×": case "✗": case "✘": case "x": case "f": case "false": case "no": case "n": case "0":
                    value = false; return true;
                default:
                    return false;
            }
        }

        /// <summary>题干首行纯文本摘要，用于列表展示。</summary>
        public string Summary(int maxLength = 80) => MarkdownText.ToPlainSummary(Stem, maxLength);
    }
}
