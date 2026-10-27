using System.Collections.Generic;

namespace MdQuiz.Core.Models
{
    /// <summary>题型。数值会写入数据库，不要修改已有取值。</summary>
    public enum QuestionType
    {
        Single = 1,
        Multiple = 2,
        TrueFalse = 3,
        FillBlank = 4,
        ShortAnswer = 5,
    }

    public static class QuestionTypes
    {
        public static readonly IReadOnlyList<QuestionType> All = new[]
        {
            QuestionType.Single, QuestionType.Multiple, QuestionType.TrueFalse,
            QuestionType.FillBlank, QuestionType.ShortAnswer,
        };

        public static string DisplayName(QuestionType type)
        {
            switch (type)
            {
                case QuestionType.Single: return "单选题";
                case QuestionType.Multiple: return "多选题";
                case QuestionType.TrueFalse: return "判断题";
                case QuestionType.FillBlank: return "填空题";
                case QuestionType.ShortAnswer: return "简答题";
                default: return type.ToString();
            }
        }

        public static string ShortName(QuestionType type)
        {
            switch (type)
            {
                case QuestionType.Single: return "单选";
                case QuestionType.Multiple: return "多选";
                case QuestionType.TrueFalse: return "判断";
                case QuestionType.FillBlank: return "填空";
                case QuestionType.ShortAnswer: return "简答";
                default: return type.ToString();
            }
        }

        public static bool HasOptions(QuestionType type) => type == QuestionType.Single || type == QuestionType.Multiple;

        /// <summary>简答题需要教师人工批改，其余题型自动判分。</summary>
        public static bool IsAutoGraded(QuestionType type) => type != QuestionType.ShortAnswer;

        /// <summary>解析题型文字，如“单选”“单选题”“single”。</summary>
        public static bool TryParse(string text, out QuestionType type)
        {
            type = QuestionType.Single;
            if (string.IsNullOrWhiteSpace(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "单选": case "单选题": case "单项选择": case "单项选择题": case "single":
                    type = QuestionType.Single; return true;
                case "多选": case "多选题": case "多项选择": case "多项选择题": case "multiple": case "multi":
                    type = QuestionType.Multiple; return true;
                case "判断": case "判断题": case "是非": case "是非题": case "truefalse": case "tf": case "judge":
                    type = QuestionType.TrueFalse; return true;
                case "填空": case "填空题": case "fill": case "blank": case "fillblank":
                    type = QuestionType.FillBlank; return true;
                case "简答": case "简答题": case "问答": case "问答题": case "主观": case "主观题": case "short": case "essay":
                    type = QuestionType.ShortAnswer; return true;
                default:
                    return false;
            }
        }
    }
}
