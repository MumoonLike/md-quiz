using System.Collections.Generic;

namespace MdQuiz.Core.Models
{
    /// <summary>下发给学生端的试卷：不含答案和解析，题目和选项顺序可能已打乱。</summary>
    public class StudentPaper
    {
        public long ExamId { get; set; }
        public string Title { get; set; } = "";
        public double TotalScore { get; set; }
        public List<StudentQuestion> Questions { get; set; } = new List<StudentQuestion>();
        /// <summary>题目中引用的图片：资源哈希 → Base64 数据。</summary>
        public Dictionary<string, string> Resources { get; set; } = new Dictionary<string, string>();
    }

    public class StudentQuestion
    {
        public long Id { get; set; }
        /// <summary>显示题号（从 1 开始）。</summary>
        public int Number { get; set; }
        public QuestionType Type { get; set; }
        public string Stem { get; set; } = "";
        /// <summary>选项（显示顺序）。Key 为原始选项字母，答案按 Key 记录。</summary>
        public List<StudentOption> Options { get; set; } = new List<StudentOption>();
        public int BlankCount { get; set; }
        public double Score { get; set; }
    }

    public class StudentOption
    {
        public string Key { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>学生的一道题作答。值的约定见 <see cref="AnswerValues"/>。</summary>
    public class AnswerEntry
    {
        public long QuestionId { get; set; }
        public List<string> Values { get; set; } = new List<string>();
        public long Ticks { get; set; }
    }

    /// <summary>
    /// 作答值约定（统一为字符串列表）：
    /// 单选 ["B"]；多选 ["A","C"]；判断 ["T"] 或 ["F"]；填空 每个空一项；简答 [文本]。
    /// </summary>
    public static class AnswerValues
    {
        public static bool IsEmpty(IList<string> values)
        {
            if (values == null || values.Count == 0) return true;
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v)) return false;
            return true;
        }
    }
}
