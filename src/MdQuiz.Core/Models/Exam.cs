using System;
using System.Collections.Generic;
using System.Linq;

namespace MdQuiz.Core.Models
{
    /// <summary>考试状态。数值会写入数据库，不要修改已有取值。</summary>
    public enum ExamStatus
    {
        /// <summary>已创建，未开放登录。</summary>
        Draft = 0,
        /// <summary>已开放登录，学生候考中。</summary>
        Open = 1,
        /// <summary>考试进行中。</summary>
        Running = 2,
        /// <summary>考试已结束。</summary>
        Ended = 3,
    }

    /// <summary>多选题计分规则（每场考试单独设置）。</summary>
    public enum MultipleScoringMode
    {
        /// <summary>与答案完全一致才得分。</summary>
        AllOrNothing = 0,
        /// <summary>全对满分；少选且无错选得一半；有错选 0 分。</summary>
        HalfForPartial = 1,
        /// <summary>按选对的比例给分；有错选 0 分。</summary>
        Proportional = 2,
    }

    public static class ExamTexts
    {
        public static string StatusName(ExamStatus s)
        {
            switch (s)
            {
                case ExamStatus.Draft: return "未开始";
                case ExamStatus.Open: return "候考中";
                case ExamStatus.Running: return "考试中";
                case ExamStatus.Ended: return "已结束";
                default: return s.ToString();
            }
        }

        public static string ModeName(MultipleScoringMode m)
        {
            switch (m)
            {
                case MultipleScoringMode.AllOrNothing: return "全对才得分";
                case MultipleScoringMode.HalfForPartial: return "少选得一半，错选 0 分";
                case MultipleScoringMode.Proportional: return "按选对比例给分，错选 0 分";
                default: return m.ToString();
            }
        }

        public static string ParticipantStatusName(ParticipantStatus s)
        {
            switch (s)
            {
                case ParticipantStatus.Absent: return "未登录";
                case ParticipantStatus.Waiting: return "候考";
                case ParticipantStatus.Answering: return "答题中";
                case ParticipantStatus.Submitted: return "已交卷";
                default: return s.ToString();
            }
        }
    }

    public class ExamSettings
    {
        public int DurationMinutes { get; set; } = 60;
        public MultipleScoringMode MultipleMode { get; set; } = MultipleScoringMode.HalfForPartial;
        public bool ShuffleQuestions { get; set; }
        public bool ShuffleOptions { get; set; }
        /// <summary>交卷后是否向学生显示客观题得分。</summary>
        public bool ShowScoreAfterSubmit { get; set; } = true;
        /// <summary>防作弊：答题窗口全屏置顶，切换窗口上报教师。</summary>
        public bool AntiCheat { get; set; } = true;

        public ExamSettings Clone() => (ExamSettings)MemberwiseClone();
    }

    public class Exam
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public long? PaperId { get; set; }
        public string PaperName { get; set; } = "";
        /// <summary>开考时冻结的试卷内容（含答案）。</summary>
        public List<ExamQuestion> Questions { get; set; } = new List<ExamQuestion>();
        public List<long> ClassIds { get; set; } = new List<long>();
        public ExamSettings Settings { get; set; } = new ExamSettings();
        public ExamStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        /// <summary>考试中延长的分钟数。</summary>
        public int ExtraMinutes { get; set; }

        public double TotalScore => Questions.Sum(q => q.Score);

        public int TotalMinutes => Settings.DurationMinutes + ExtraMinutes;

        /// <summary>考试截止时间（UTC），未开始时为 null。</summary>
        public DateTime? DeadlineUtc => StartedAt?.AddMinutes(TotalMinutes);

        public ExamQuestion FindQuestion(long id) => Questions.FirstOrDefault(q => q.Id == id);
    }

    /// <summary>参加考试的学生状态。数值会写入数据库，不要修改已有取值。</summary>
    public enum ParticipantStatus
    {
        Absent = 0,
        Waiting = 1,
        Answering = 2,
        Submitted = 3,
    }

    /// <summary>某场考试中一名学生的记录（姓名、班级在开放登录时从花名册复制）。</summary>
    public class Participant
    {
        public long ExamId { get; set; }
        public string StudentNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string ClassName { get; set; } = "";
        public ParticipantStatus Status { get; set; }
        public string Token { get; set; }
        public string Machine { get; set; }
        public string Ip { get; set; }
        public DateTime? LoginAt { get; set; }
        public DateTime? SubmittedAt { get; set; }
        /// <summary>是否因考试结束被系统强制收卷。</summary>
        public bool Forced { get; set; }
        public int FocusLost { get; set; }
        public double? ObjectiveScore { get; set; }
        public double? SubjectiveScore { get; set; }
        public double? TotalScore { get; set; }
        /// <summary>还需人工批改的题目数。</summary>
        public int PendingManual { get; set; }

        public Participant Clone() => (Participant)MemberwiseClone();
    }

    public class AnswerRecord
    {
        public long ExamId { get; set; }
        public string StudentNo { get; set; } = "";
        public long QuestionId { get; set; }
        public List<string> Values { get; set; } = new List<string>();
        /// <summary>学生端修改答案的时间（UTC Ticks），用于断线重连后合并答案。</summary>
        public long Ticks { get; set; }
        /// <summary>得分；null 表示尚未判分（简答题待批改）。</summary>
        public double? Score { get; set; }
        /// <summary>分数是否由教师人工给出（人工分数不会被自动判分覆盖）。</summary>
        public bool Manual { get; set; }
    }

    public class SchoolClass
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int StudentCount { get; set; }
    }

    public class Student
    {
        public long Id { get; set; }
        public long ClassId { get; set; }
        public string StudentNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string ClassName { get; set; } = "";
    }
}
