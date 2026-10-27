using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MdQuiz.Core.Models;

namespace MdQuiz.Core.Exams
{
    /// <summary>题目中图片的引用格式：![](res:哈希)。图片数据保存在教师端数据库中。</summary>
    public static class ResourceLinks
    {
        public const string Scheme = "res:";
        private static readonly Regex Pattern = new Regex(@"res:(?<hash>[0-9a-fA-F]{16,64})", RegexOptions.Compiled);

        public static IEnumerable<string> Find(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) yield break;
            foreach (Match m in Pattern.Matches(markdown)) yield return m.Groups["hash"].Value.ToLowerInvariant();
        }

        public static IEnumerable<string> FindInQuestion(Question q)
        {
            return Find(q.Stem).Concat(q.Options.SelectMany(Find)).Concat(Find(q.Analysis)).Concat(Find(q.Answer)).Distinct();
        }

        /// <summary>从 res:xxx 形式的 URL 中取出哈希，不是资源链接时返回 null。</summary>
        public static string HashOf(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
            var m = Pattern.Match(url);
            return m.Success ? m.Groups["hash"].Value.ToLowerInvariant() : null;
        }
    }

    /// <summary>为每个学生生成下发的试卷。乱序结果由（考试, 学号）决定，断线重连后保持不变。</summary>
    public static class StudentPaperBuilder
    {
        public static StudentPaper Build(Exam exam, string studentNo, Func<IEnumerable<string>, IDictionary<string, string>> loadResources)
        {
            var ordered = OrderQuestions(exam, studentNo);
            var paper = new StudentPaper
            {
                ExamId = exam.Id,
                Title = exam.Title,
                TotalScore = exam.TotalScore,
            };
            var number = 0;
            foreach (var q in ordered)
            {
                var sq = new StudentQuestion
                {
                    Id = q.Id,
                    Number = ++number,
                    Type = q.Type,
                    Stem = q.Stem,
                    Score = q.Score,
                    BlankCount = q.Type == QuestionType.FillBlank ? q.Blanks.Count : 0,
                };
                if (QuestionTypes.HasOptions(q.Type))
                {
                    var options = q.Options.Select((text, i) => new StudentOption { Key = Question.OptionKey(i), Text = text }).ToList();
                    if (exam.Settings.ShuffleOptions) Shuffle(options, Rng(exam.Id, studentNo, "opt" + q.Id));
                    sq.Options = options;
                }
                paper.Questions.Add(sq);
            }

            var hashes = ordered.SelectMany(q => ResourceLinks.Find(q.Stem).Concat(q.Options.SelectMany(ResourceLinks.Find))).Distinct().ToList();
            if (hashes.Count > 0 && loadResources != null)
            {
                foreach (var kv in loadResources(hashes)) paper.Resources[kv.Key] = kv.Value;
            }
            return paper;
        }

        /// <summary>
        /// 题目顺序。开启“题目乱序”时只在相邻的同题型题目之间打乱，
        /// 保持“单选 → 多选 → …”这样的大题结构不变。
        /// </summary>
        public static List<ExamQuestion> OrderQuestions(Exam exam, string studentNo)
        {
            var list = exam.Questions.ToList();
            if (!exam.Settings.ShuffleQuestions) return list;
            var rng = Rng(exam.Id, studentNo, "questions");
            var result = new List<ExamQuestion>(list.Count);
            var i = 0;
            while (i < list.Count)
            {
                var j = i;
                while (j < list.Count && list[j].Type == list[i].Type) j++;
                var run = list.GetRange(i, j - i);
                Shuffle(run, rng);
                result.AddRange(run);
                i = j;
            }
            return result;
        }

        private static void Shuffle<T>(IList<T> list, StableRandom rng)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        private static StableRandom Rng(long examId, string studentNo, string salt)
        {
            return new StableRandom(StableRandom.Hash(examId + "|" + studentNo + "|" + salt));
        }
    }

    /// <summary>与运行时无关的确定性随机数（SplitMix64）。</summary>
    public sealed class StableRandom
    {
        private ulong _state;

        public StableRandom(ulong seed) { _state = seed; }

        public static ulong Hash(string text)
        {
            const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
            var hash = offset;
            foreach (var b in Encoding.UTF8.GetBytes(text ?? ""))
            {
                hash ^= b;
                hash *= prime;
            }
            return hash;
        }

        public ulong NextULong()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>返回 [0, maxExclusive) 内的整数。</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return (int)(NextULong() % (ulong)maxExclusive);
        }
    }
}
