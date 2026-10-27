using System.Collections.Generic;
using System.Linq;
using MdQuiz.Core.Exams;
using MdQuiz.Core.Models;
using MdQuiz.Core.Scoring;
using Xunit;

namespace MdQuiz.Tests
{
    public class GraderTests
    {
        private static ExamQuestion Multi() => new ExamQuestion
        {
            Id = 1, Type = QuestionType.Multiple, Stem = "?", Options = { "a", "b", "c", "d" }, Answer = "ACD", Score = 6,
        };

        [Theory]
        [InlineData(MultipleScoringMode.AllOrNothing, new[] { "A", "C", "D" }, 6)]
        [InlineData(MultipleScoringMode.AllOrNothing, new[] { "A", "C" }, 0)]
        [InlineData(MultipleScoringMode.HalfForPartial, new[] { "D", "A", "C" }, 6)]
        [InlineData(MultipleScoringMode.HalfForPartial, new[] { "A" }, 3)]
        [InlineData(MultipleScoringMode.HalfForPartial, new[] { "A", "B" }, 0)]
        [InlineData(MultipleScoringMode.Proportional, new[] { "A", "C" }, 4)]
        [InlineData(MultipleScoringMode.Proportional, new[] { "A" }, 2)]
        [InlineData(MultipleScoringMode.Proportional, new[] { "A", "B", "C" }, 0)]
        [InlineData(MultipleScoringMode.Proportional, new string[0], 0)]
        public void MultipleChoiceModes(MultipleScoringMode mode, string[] chosen, double expected)
        {
            Assert.Equal(expected, Grader.Grade(Multi(), chosen, mode));
        }

        [Fact]
        public void SingleAndTrueFalse()
        {
            var single = new ExamQuestion { Type = QuestionType.Single, Options = { "x", "y" }, Answer = "B", Score = 2 };
            Assert.Equal(2, Grader.Grade(single, new[] { "b" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(0, Grader.Grade(single, new[] { "A" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(0, Grader.Grade(single, new string[0], MultipleScoringMode.AllOrNothing));

            var tf = new ExamQuestion { Type = QuestionType.TrueFalse, Answer = "F", Score = 1 };
            Assert.Equal(1, Grader.Grade(tf, new[] { "F" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(0, Grader.Grade(tf, new[] { "T" }, MultipleScoringMode.AllOrNothing));
        }

        [Fact]
        public void FillBlankNormalizesAndScoresPerBlank()
        {
            var q = new ExamQuestion { Type = QuestionType.FillBlank, Stem = "___ ___ ___", Blanks = { "北京|北京市", "HTTP", "8" }, Score = 3 };
            Assert.Equal(3, Grader.Grade(q, new[] { " 北京市 ", "ｈｔｔｐ", "８。" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(2, Grader.Grade(q, new[] { "北京", "", "8" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(1, Grader.Grade(q, new[] { "上海", "http" }, MultipleScoringMode.AllOrNothing));
            var q2 = new ExamQuestion { Type = QuestionType.FillBlank, Stem = "___ ___ ___", Blanks = { "a", "b", "c" }, Score = 2 };
            Assert.Equal(0.67, Grader.Grade(q2, new[] { "a" }, MultipleScoringMode.AllOrNothing));
        }

        [Fact]
        public void ShortAnswerNeedsManualUnlessEmpty()
        {
            var q = new ExamQuestion { Type = QuestionType.ShortAnswer, Score = 10 };
            Assert.Null(Grader.Grade(q, new[] { "我的答案" }, MultipleScoringMode.AllOrNothing));
            Assert.Equal(0, Grader.Grade(q, new[] { "  " }, MultipleScoringMode.AllOrNothing));
        }

        [Fact]
        public void StudentPaperShuffleIsStableAndKeepsTypeGroups()
        {
            var exam = new Exam { Id = 7, Title = "t", Settings = { ShuffleQuestions = true, ShuffleOptions = true } };
            long id = 0;
            foreach (var type in new[] { QuestionType.Single, QuestionType.Single, QuestionType.Single, QuestionType.Single, QuestionType.Multiple, QuestionType.Multiple, QuestionType.ShortAnswer })
                exam.Questions.Add(new ExamQuestion { Id = ++id, Type = type, Stem = "q" + id, Options = { "a", "b", "c", "d" }, Answer = "A", Score = 1 });

            var p1 = StudentPaperBuilder.Build(exam, "001", null);
            var p1Again = StudentPaperBuilder.Build(exam, "001", null);
            Assert.Equal(p1.Questions.Select(q => q.Id), p1Again.Questions.Select(q => q.Id));
            Assert.Equal(p1.Questions.SelectMany(q => q.Options.Select(o => o.Key)), p1Again.Questions.SelectMany(q => q.Options.Select(o => o.Key)));
            // 大题顺序不变
            Assert.Equal(exam.Questions.Select(q => q.Type), p1.Questions.Select(q => q.Type));
            Assert.Equal(Enumerable.Range(1, 7), p1.Questions.Select(q => q.Number));
            // 不同学生顺序（极大概率）不同
            var orders = Enumerable.Range(0, 20).Select(i => string.Join(",", StudentPaperBuilder.Build(exam, "s" + i, null).Questions.Select(q => q.Id))).Distinct().Count();
            Assert.True(orders > 1);
            // 选项文字与原始字母对应
            foreach (var sq in p1.Questions.Where(q => q.Options.Count > 0))
                Assert.All(sq.Options, o => Assert.Equal("abcd"[Question.OptionIndex(o.Key)].ToString(), o.Text));
        }

        [Fact]
        public void ValidateCatchesProblems()
        {
            Assert.NotNull(new Question { Type = QuestionType.Single, Stem = "s", Options = { "a" }, Answer = "A" }.Validate());
            Assert.NotNull(new Question { Type = QuestionType.Single, Stem = "s", Options = { "a", "b" }, Answer = "C" }.Validate());
            Assert.Null(new Question { Type = QuestionType.Single, Stem = "s", Options = { "a", "b" }, Answer = "B" }.Validate());
            Assert.NotNull(new Question { Type = QuestionType.FillBlank, Stem = "___ and ___", Blanks = { "a" } }.Validate());
            Assert.Null(new Question { Type = QuestionType.FillBlank, Stem = "___ and ___", Blanks = { "a", "b|c" } }.Validate());
            Assert.Null(new Question { Type = QuestionType.ShortAnswer, Stem = "s" }.Validate());
        }
    }
}
