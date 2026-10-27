using System;
using System.Collections.Generic;
using System.Linq;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;

namespace MdQuiz.Server.Services
{
    public class DrawResult
    {
        public List<PaperItem> Items { get; } = new List<PaperItem>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>组卷（随机抽题）与创建考试。</summary>
    public sealed class PaperService
    {
        private readonly QuestionRepository _questions;
        private readonly ExamRepository _exams;
        private readonly Random _random = new Random();

        public PaperService(QuestionRepository questions, ExamRepository exams)
        {
            _questions = questions;
            _exams = exams;
        }

        public int CountAvailable(QuestionType type, string tag, ISet<long> exclude)
        {
            return _questions.FindIds(type, tag).Count(id => exclude == null || !exclude.Contains(id));
        }

        /// <summary>按规则随机抽题，跳过已在试卷中的题目。题目不足时尽量多抽并给出提示。</summary>
        public DrawResult Draw(IEnumerable<RandomDrawRule> rules, ISet<long> exclude)
        {
            var result = new DrawResult();
            var used = new HashSet<long>(exclude ?? new HashSet<long>());
            foreach (var rule in rules.Where(r => r.Count > 0))
            {
                var pool = _questions.FindIds(rule.Type, rule.Tag).Where(id => !used.Contains(id)).ToList();
                for (var i = pool.Count - 1; i > 0; i--)
                {
                    var j = _random.Next(i + 1);
                    var tmp = pool[i];
                    pool[i] = pool[j];
                    pool[j] = tmp;
                }
                var picked = pool.Take(rule.Count).ToList();
                if (picked.Count < rule.Count)
                {
                    var tagText = string.IsNullOrWhiteSpace(rule.Tag) ? "" : $"（标签“{rule.Tag}”）";
                    result.Warnings.Add($"{QuestionTypes.DisplayName(rule.Type)}{tagText}只有 {picked.Count} 道可用，少于要求的 {rule.Count} 道");
                }
                foreach (var id in picked)
                {
                    used.Add(id);
                    result.Items.Add(new PaperItem { QuestionId = id, Score = rule.ScoreEach });
                }
            }
            return result;
        }

        /// <summary>用试卷创建一场考试（冻结题目内容）。返回考试 id。</summary>
        public long CreateExam(Paper paper, string title, IEnumerable<long> classIds, ExamSettings settings, out int missing)
        {
            var questions = _questions.GetMany(paper.Items.Select(i => i.QuestionId));
            var snapshot = new List<ExamQuestion>();
            missing = 0;
            foreach (var item in paper.Items)
            {
                if (questions.TryGetValue(item.QuestionId, out var q)) snapshot.Add(ExamQuestion.From(q, item.Score));
                else missing++;
            }
            if (snapshot.Count == 0) throw new InvalidOperationException("试卷中没有可用的题目");
            var exam = new Exam
            {
                Title = string.IsNullOrWhiteSpace(title) ? paper.Name : title.Trim(),
                PaperId = paper.Id,
                PaperName = paper.Name,
                Questions = snapshot,
                ClassIds = classIds.Distinct().ToList(),
                Settings = settings.Clone(),
                Status = ExamStatus.Draft,
            };
            if (exam.ClassIds.Count == 0) throw new InvalidOperationException("请至少选择一个班级");
            return _exams.Insert(exam);
        }
    }
}
