using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MdQuiz.Core.Models;

namespace MdQuiz.Core.Scoring
{
    /// <summary>自动判分。</summary>
    public static class Grader
    {
        /// <summary>
        /// 计算一道题的得分。简答题有作答时返回 null（待教师批改），未作答返回 0。
        /// </summary>
        public static double? Grade(ExamQuestion q, IList<string> values, MultipleScoringMode mode)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));
            var empty = AnswerValues.IsEmpty(values);
            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.TrueFalse:
                    if (empty) return 0;
                    var chosen = q.Type == QuestionType.Single
                        ? Question.NormalizeKeys(values)
                        : (values[0] ?? "").Trim().ToUpperInvariant();
                    return chosen == q.Answer ? q.Score : 0;

                case QuestionType.Multiple:
                    return empty ? 0 : Round(GradeMultiple(q.CorrectKeys(), new HashSet<string>(Question.NormalizeKeys(values).Select(c => c.ToString())), q.Score, mode));

                case QuestionType.FillBlank:
                {
                    if (empty || q.Blanks.Count == 0) return 0;
                    var each = q.Score / q.Blanks.Count;
                    double total = 0;
                    for (var i = 0; i < q.Blanks.Count; i++)
                    {
                        var given = i < values.Count ? NormalizeText(values[i]) : "";
                        if (given.Length == 0) continue;
                        if (Question.BlankAlternatives(q.Blanks[i]).Any(alt => NormalizeText(alt) == given)) total += each;
                    }
                    return Round(total);
                }

                case QuestionType.ShortAnswer:
                    return empty ? 0 : (double?)null;

                default:
                    return 0;
            }
        }

        public static double GradeMultiple(ISet<string> correct, ISet<string> chosen, double score, MultipleScoringMode mode)
        {
            if (chosen.Count == 0 || correct.Count == 0) return 0;
            if (chosen.SetEquals(correct)) return score;
            if (!chosen.IsSubsetOf(correct)) return 0; // 有错选
            switch (mode)
            {
                case MultipleScoringMode.HalfForPartial: return score / 2;
                case MultipleScoringMode.Proportional: return score * chosen.Count / correct.Count;
                default: return 0;
            }
        }

        public static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>填空题答案比较前的规范化：全角转半角、去首尾空白和句末标点、合并空白、忽略大小写。</summary>
        public static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            var lastSpace = false;
            foreach (var raw in text)
            {
                var c = raw;
                if (c == '　') c = ' ';
                else if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace && sb.Length > 0) sb.Append(' ');
                    lastSpace = true;
                    continue;
                }
                lastSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Trim().TrimEnd('。', '.', ',', '，', ';', '；', '!', '！', '?', '？').Trim();
        }
    }
}
