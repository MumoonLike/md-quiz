using System;
using System.Collections.Generic;
using System.Linq;
using MdQuiz.Core.Models;
using MdQuiz.Core.Protocol;
using MdQuiz.Core.Scoring;
using MdQuiz.Server.Data;

namespace MdQuiz.Server.Hosting
{
    /// <summary>判分、人工批改与成绩汇总。</summary>
    public sealed class ResultService
    {
        private readonly ExamRepository _exams;

        public ResultService(ExamRepository exams)
        {
            _exams = exams;
        }

        /// <summary>
        /// 对一名考生的全部题目自动判分（人工给过分的题保持不变），并更新总分。
        /// 未作答的题目也会生成 0 分记录，方便批改和导出。
        /// </summary>
        public void GradeParticipant(Exam exam, Participant p)
        {
            var answers = _exams.ListAnswers(exam.Id, p.StudentNo).ToDictionary(a => a.QuestionId);
            _exams.Database.Write((c, t) =>
            {
                foreach (var q in exam.Questions)
                {
                    if (!answers.TryGetValue(q.Id, out var a))
                    {
                        a = new AnswerRecord { ExamId = exam.Id, StudentNo = p.StudentNo, QuestionId = q.Id };
                        answers[q.Id] = a;
                    }
                    if (!a.Manual) a.Score = Grader.Grade(q, a.Values, exam.Settings.MultipleMode);
                    ExamRepository.SaveScore(c, t, a);
                }
                Summarize(exam, p, answers);
                ExamRepository.UpdateParticipant(c, t, p);
            });
        }

        /// <summary>按已保存的各题得分重新计算考生总分。</summary>
        public void Recalculate(Exam exam, Participant p)
        {
            var answers = _exams.ListAnswers(exam.Id, p.StudentNo).ToDictionary(a => a.QuestionId);
            Summarize(exam, p, answers);
            _exams.UpdateParticipant(p);
        }

        private static void Summarize(Exam exam, Participant p, IDictionary<long, AnswerRecord> answers)
        {
            double objective = 0, subjective = 0;
            var pending = 0;
            var hasSubjective = false;
            foreach (var q in exam.Questions)
            {
                answers.TryGetValue(q.Id, out var a);
                var score = a?.Score;
                if (QuestionTypes.IsAutoGraded(q.Type))
                {
                    objective += score ?? 0;
                }
                else
                {
                    hasSubjective = true;
                    if (score.HasValue) subjective += score.Value;
                    else pending++;
                }
            }
            p.ObjectiveScore = Grader.Round(objective);
            p.SubjectiveScore = hasSubjective ? Grader.Round(subjective) : (double?)null;
            p.TotalScore = Grader.Round(objective + subjective);
            p.PendingManual = pending;
        }

        /// <summary>教师人工给分（任何题型都可以改分）。</summary>
        public void SetManualScore(Exam exam, string studentNo, long questionId, double? score)
        {
            var q = exam.FindQuestion(questionId) ?? throw new ArgumentException("题目不存在");
            if (score.HasValue)
            {
                if (score.Value < 0 || score.Value > q.Score + 1e-9) throw new ArgumentException($"分数应在 0 ~ {q.Score} 之间");
                score = Grader.Round(score.Value);
            }
            var answers = _exams.ListAnswers(exam.Id, studentNo);
            var a = answers.FirstOrDefault(x => x.QuestionId == questionId)
                    ?? new AnswerRecord { ExamId = exam.Id, StudentNo = studentNo, QuestionId = questionId };
            if (score.HasValue)
            {
                a.Score = score;
                a.Manual = true;
            }
            else
            {
                // 清除人工分：恢复自动判分（简答题恢复为待批改）
                a.Manual = false;
                a.Score = Grader.Grade(q, a.Values, exam.Settings.MultipleMode);
            }
            var p = _exams.GetParticipant(exam.Id, studentNo) ?? throw new ArgumentException("考生不存在");
            _exams.Database.Write((c, t) => ExamRepository.SaveScore(c, t, a));
            Recalculate(exam, p);
        }

        public SubmitResult BuildSubmitResult(Exam exam, Participant p)
        {
            return new SubmitResult
            {
                Ok = true,
                Forced = p.Forced,
                ShowScore = exam.Settings.ShowScoreAfterSubmit,
                ObjectiveScore = exam.Settings.ShowScoreAfterSubmit ? p.ObjectiveScore ?? 0 : 0,
                ObjectiveTotal = exam.Settings.ShowScoreAfterSubmit ? exam.Questions.Where(q => QuestionTypes.IsAutoGraded(q.Type)).Sum(q => q.Score) : 0,
                HasPendingManual = p.PendingManual > 0,
                TotalScore = exam.Settings.ShowScoreAfterSubmit && p.PendingManual == 0 ? p.TotalScore ?? 0 : 0,
                SubmittedAtTicks = (p.SubmittedAt ?? DateTime.UtcNow).Ticks,
            };
        }

        /// <summary>
        /// 一名考生的答卷（按试卷题目顺序）。未交卷时客观题按当前答案临时判分（<see cref="AnswerSheetItem.Provisional"/>）。
        /// </summary>
        public List<AnswerSheetItem> BuildAnswerSheet(Exam exam, string studentNo)
        {
            var answers = _exams.ListAnswers(exam.Id, studentNo).ToDictionary(a => a.QuestionId);
            return exam.Questions.Select((q, i) => SheetItem(exam, q, i + 1, answers.TryGetValue(q.Id, out var a) ? a : null)).ToList();
        }

        public static AnswerSheetItem SheetItem(Exam exam, ExamQuestion q, int number, AnswerRecord a)
        {
            var values = a?.Values ?? new List<string>();
            var item = new AnswerSheetItem
            {
                Number = number,
                Question = q,
                Values = values,
                Answered = !AnswerValues.IsEmpty(values),
                Manual = a?.Manual ?? false,
                Score = a?.Score,
            };
            if (item.Score == null && QuestionTypes.IsAutoGraded(q.Type))
            {
                item.Score = Grader.Grade(q, values, exam.Settings.MultipleMode) ?? 0;
                item.Provisional = true;
            }
            if (!item.Answered && !item.Manual) item.Verdict = AnswerVerdict.Unanswered;
            else if (!QuestionTypes.IsAutoGraded(q.Type) && !item.Manual) item.Verdict = item.Score.HasValue ? AnswerVerdict.Graded : AnswerVerdict.Pending;
            else if (item.Score == null) item.Verdict = AnswerVerdict.Pending;
            else if (item.Score.Value >= q.Score - 1e-9) item.Verdict = AnswerVerdict.Correct;
            else item.Verdict = item.Score.Value > 1e-9 ? AnswerVerdict.Partial : AnswerVerdict.Wrong;
            return item;
        }

        /// <summary>成绩表：每名考生一行，含各题得分。</summary>
        public ResultTable BuildTable(Exam exam)
        {
            var participants = _exams.ListParticipants(exam.Id);
            var answers = _exams.ListAllAnswers(exam.Id).GroupBy(a => a.StudentNo).ToDictionary(g => g.Key, g => g.ToDictionary(a => a.QuestionId));
            var table = new ResultTable { Exam = exam };
            foreach (var p in participants)
            {
                answers.TryGetValue(p.StudentNo, out var mine);
                var row = new ResultRow { Participant = p };
                foreach (var q in exam.Questions)
                {
                    AnswerRecord a = null;
                    mine?.TryGetValue(q.Id, out a);
                    row.Answers[q.Id] = a;
                }
                table.Rows.Add(row);
            }
            return table;
        }
    }

    public enum AnswerVerdict
    {
        /// <summary>未作答。</summary>
        Unanswered,
        Correct,
        /// <summary>部分得分（多选少选、填空部分正确等）。</summary>
        Partial,
        Wrong,
        /// <summary>简答题待批改。</summary>
        Pending,
        /// <summary>简答题已批改。</summary>
        Graded,
    }

    /// <summary>答卷中的一道题。</summary>
    public class AnswerSheetItem
    {
        public int Number { get; set; }
        public ExamQuestion Question { get; set; }
        public List<string> Values { get; set; } = new List<string>();
        public bool Answered { get; set; }
        /// <summary>得分；简答题未批改时为 null。</summary>
        public double? Score { get; set; }
        /// <summary>得分是按当前答案临时计算的（考生尚未交卷）。</summary>
        public bool Provisional { get; set; }
        /// <summary>分数由教师人工给出。</summary>
        public bool Manual { get; set; }
        public AnswerVerdict Verdict { get; set; }
    }

    public class ResultTable
    {
        public Exam Exam { get; set; }
        public List<ResultRow> Rows { get; } = new List<ResultRow>();
    }

    public class ResultRow
    {
        public Participant Participant { get; set; }
        public Dictionary<long, AnswerRecord> Answers { get; } = new Dictionary<long, AnswerRecord>();
    }

    public static class AnswerFormatter
    {
        /// <summary>把作答值格式化为便于阅读的文字（导出 Excel、批改界面使用）。</summary>
        public static string Format(Question q, IList<string> values)
        {
            if (AnswerValues.IsEmpty(values)) return "";
            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                    return Question.NormalizeKeys(values);
                case QuestionType.TrueFalse:
                    return values[0] == "T" ? "对" : values[0] == "F" ? "错" : values[0];
                case QuestionType.FillBlank:
                    return string.Join("；", values.Select(v => (v ?? "").Trim()));
                default:
                    return string.Join("\n", values);
            }
        }

        public static string FormatCorrect(Question q)
        {
            switch (q.Type)
            {
                case QuestionType.Single:
                case QuestionType.Multiple:
                    return q.Answer;
                case QuestionType.TrueFalse:
                    return q.Answer == "T" ? "对" : "错";
                case QuestionType.FillBlank:
                    return string.Join("；", q.Blanks.Select(b => b.Replace("|", " / ")));
                default:
                    return q.Answer;
            }
        }
    }
}
