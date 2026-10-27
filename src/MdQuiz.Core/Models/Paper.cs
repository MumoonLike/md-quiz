using System;
using System.Collections.Generic;
using System.Linq;

namespace MdQuiz.Core.Models
{
    /// <summary>试卷：从题库中挑选（手动或随机）的题目及每题分值，题目顺序即列表顺序。</summary>
    public class Paper
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<PaperItem> Items { get; set; } = new List<PaperItem>();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public double TotalScore => Items.Sum(i => i.Score);
    }

    public class PaperItem
    {
        public long QuestionId { get; set; }
        public double Score { get; set; }
    }

    /// <summary>随机抽题规则：从符合题型（和标签）的题目中随机抽取若干道。</summary>
    public class RandomDrawRule
    {
        public QuestionType Type { get; set; }
        /// <summary>为空表示不限标签。</summary>
        public string Tag { get; set; } = "";
        public int Count { get; set; }
        public double ScoreEach { get; set; }
    }

    /// <summary>考试开始时冻结的一道题（含答案和分值），之后题库修改不影响已有考试。</summary>
    public class ExamQuestion : Question
    {
        public double Score { get; set; }

        public static ExamQuestion From(Question q, double score)
        {
            return new ExamQuestion
            {
                Id = q.Id,
                Type = q.Type,
                Stem = q.Stem,
                Options = new List<string>(q.Options),
                Answer = q.Answer,
                Blanks = new List<string>(q.Blanks),
                Analysis = q.Analysis,
                Tags = new List<string>(q.Tags),
                CreatedAt = q.CreatedAt,
                UpdatedAt = q.UpdatedAt,
                Score = score,
            };
        }
    }
}
