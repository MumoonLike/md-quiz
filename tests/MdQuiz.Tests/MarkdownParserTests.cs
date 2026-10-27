using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdQuiz.Core.Markdown;
using MdQuiz.Core.Models;
using Xunit;

namespace MdQuiz.Tests
{
    public class MarkdownParserTests
    {
        public static string SamplePath => Path.Combine(TestPaths.RepoRoot, "samples", "sample-questions.md");

        [Fact]
        public void ParsesSampleBank()
        {
            var result = QuestionMarkdown.Parse(File.ReadAllText(SamplePath));
            Assert.Equal("示例题库", result.Title);
            Assert.All(result.Questions, q => Assert.True(q.IsValid, $"line {q.Line}: {q.Error}"));
            Assert.Equal(10, result.Questions.Count);
            Assert.Empty(result.Warnings);

            var q1 = result.Questions[0];
            Assert.Equal(QuestionType.Single, q1.Question.Type);
            Assert.Equal(2, q1.Score);
            Assert.Equal("下列哪个数是**质数**？", q1.Question.Stem);
            Assert.Equal(new[] { "4", "7", "9", "15" }, q1.Question.Options);
            Assert.Equal("B", q1.Question.Answer);
            Assert.Equal(new[] { "数学", "质数" }, q1.Question.Tags);
            Assert.Equal("7 只能被 1 和它本身整除。", q1.Question.Analysis);

            var q2 = result.Questions[1];
            Assert.Equal(new[] { "$-1$", "$0$", "$1$", "$3$" }, q2.Question.Options);
            Assert.Equal("A", q2.Question.Answer);

            // 一行多个选项、代码块题干
            var q3 = result.Questions[2];
            Assert.Equal(new[] { "6", "14", "36", "报错" }, q3.Question.Options);
            Assert.Equal("B", q3.Question.Answer);
            Assert.Contains("```python", q3.Question.Stem);
            Assert.Contains("print(sum(n * n for n in nums))", q3.Question.Stem);

            var q5 = result.Questions[4];
            Assert.Equal(QuestionType.Multiple, q5.Question.Type);
            Assert.Equal("ABD", q5.Question.Answer);
            Assert.Equal(5, q5.Question.Options.Count);

            Assert.Equal("T", result.Questions[5].Question.Answer);
            Assert.Equal("F", result.Questions[6].Question.Answer);

            var fill = result.Questions[7].Question;
            Assert.Equal(QuestionType.FillBlank, fill.Type);
            Assert.Equal(new[] { "北京|北京市", "新疆|新疆维吾尔自治区" }, fill.Blanks);

            var shortQ = result.Questions[9].Question;
            Assert.Equal(QuestionType.ShortAnswer, shortQ.Type);
            Assert.StartsWith("1. 客户端发送 SYN", shortQ.Answer);
            Assert.EndsWith("防止已失效的连接请求被误建立。", shortQ.Answer);
            Assert.Equal(10, result.Questions[9].Score);
        }

        [Fact]
        public void ReportsErrorsWithLineNumbers()
        {
            var md = "## [单选] 没有答案\n- [ ] a\n- [ ] b\n\n## [选择] 未知题型\n\n## [填空] 没有空位\n答案: x\n\n## [单选] 两个答案\n- [x] a\n- [x] b\n";
            var result = QuestionMarkdown.Parse(md);
            Assert.Equal(4, result.Questions.Count);
            Assert.Equal(0, result.ValidCount);
            Assert.Equal(1, result.Questions[0].Line);
            Assert.Contains("正确答案", result.Questions[0].Error);
            Assert.Equal(5, result.Questions[1].Line);
            Assert.Contains("题型", result.Questions[1].Error);
            Assert.Contains("___", result.Questions[2].Error);
            Assert.Contains("只能有一个", result.Questions[3].Error);
        }

        [Fact]
        public void SupportsAlternativeSyntax()
        {
            var md = "### 1. 【单选题】 第一题\nA、甲\nB、乙\n正确答案：b\n\n## [多选|3] 第二题\n- [X] a\n- [ ] b\n- [√] c\n\n## [判断题] 第三题\n答案：×\n\n## [填空 2分] 1 + 1 = ___，2 + 2 = ___\n答案: 2；4\n";
            var result = QuestionMarkdown.Parse(md);
            Assert.All(result.Questions, q => Assert.True(q.IsValid, q.Error));
            Assert.Equal("B", result.Questions[0].Question.Answer);
            Assert.Equal(new[] { "甲", "乙" }, result.Questions[0].Question.Options);
            Assert.Equal("AC", result.Questions[1].Question.Answer);
            Assert.Equal(3, result.Questions[1].Score);
            Assert.Equal("F", result.Questions[2].Question.Answer);
            Assert.Equal(new[] { "2", "4" }, result.Questions[3].Question.Blanks);
        }

        [Fact]
        public void WriteThenParseRoundTrips()
        {
            var original = QuestionMarkdown.Parse(File.ReadAllText(SamplePath));
            var md = QuestionMarkdown.Write(original.Questions.Select(q => new KeyValuePair<Question, double?>(q.Question, q.Score)), "导出");
            var again = QuestionMarkdown.Parse(md);
            Assert.Equal(original.Questions.Count, again.Questions.Count);
            for (var i = 0; i < original.Questions.Count; i++)
            {
                var a = original.Questions[i];
                var b = again.Questions[i];
                Assert.True(b.IsValid, $"#{i + 1}: {b.Error}\n{md}");
                Assert.Equal(a.Question.Type, b.Question.Type);
                Assert.Equal(a.Question.Stem, b.Question.Stem);
                Assert.Equal(a.Question.Options, b.Question.Options);
                Assert.Equal(a.Question.Answer, b.Question.Answer);
                Assert.Equal(a.Question.Blanks, b.Question.Blanks);
                Assert.Equal(a.Question.Analysis, b.Question.Analysis);
                Assert.Equal(a.Question.Tags, b.Question.Tags);
                Assert.Equal(a.Score, b.Score);
            }
        }

        [Fact]
        public void ReadmeExampleParses()
        {
            var readme = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "README.md"));
            var start = readme.IndexOf("```markdown\n", System.StringComparison.Ordinal) + "```markdown\n".Length;
            var end = readme.IndexOf("\n```", start, System.StringComparison.Ordinal);
            var result = QuestionMarkdown.Parse(readme.Substring(start, end - start));
            Assert.Equal(5, result.Questions.Count);
            Assert.All(result.Questions, q => Assert.True(q.IsValid, $"line {q.Line}: {q.Error}"));
            Assert.Equal(new[] { "TCP", "UDP", "IP", "HTTP" }, result.Questions[1].Question.Options);
            Assert.Equal("AB", result.Questions[1].Question.Answer);
        }

        [Fact]
        public void SummaryKeepsBlanksAndStripsMarkup()
        {
            Assert.Equal("首都是 ____ 。", MarkdownText.ToPlainSummaryFor("首都是 ____ 。"));
            Assert.Equal("下列哪个数是质数？", MarkdownText.ToPlainSummaryFor("下列哪个数是**质数**？"));
            Assert.Equal("看图 [图片]", MarkdownText.ToPlainSummaryFor("看图 ![](res:0123456789abcdef0123456789abcdef)"));
        }
    }

    internal static class MarkdownText
    {
        public static string ToPlainSummaryFor(string md) => MdQuiz.Core.MarkdownText.ToPlainSummary(md);
    }

    internal static class TestPaths
    {
        public static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
                return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
            }
        }
    }
}
