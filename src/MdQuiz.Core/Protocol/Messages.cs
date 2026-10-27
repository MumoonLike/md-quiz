using System.Collections.Generic;
using MdQuiz.Core.Models;

namespace MdQuiz.Core.Protocol
{
    /// <summary>消息类型。客户端 → 服务端 的请求会收到对应的 *_result。</summary>
    public static class PacketTypes
    {
        public const int ProtocolVersion = 1;

        // 客户端 → 服务端
        public const string Hello = "hello";
        public const string Identify = "identify";
        public const string Login = "login";
        public const string Answer = "answer";
        public const string Submit = "submit";
        public const string FocusLost = "focus_lost";
        public const string Ping = "ping";

        // 服务端 → 客户端
        public const string Welcome = "welcome";
        public const string IdentifyResult = "identify_result";
        public const string LoginResult = "login_result";
        public const string ExamState = "exam_state";
        public const string Paper = "paper";
        public const string AnswerAck = "answer_ack";
        public const string SubmitResult = "submit_result";
        public const string Pong = "pong";
        public const string Kicked = "kicked";
        public const string Error = "error";
    }

    public class HelloMessage
    {
        public int ProtocolVersion { get; set; } = PacketTypes.ProtocolVersion;
        public string ClientVersion { get; set; }
        public string MachineName { get; set; }
    }

    public class WelcomeMessage
    {
        public int ProtocolVersion { get; set; } = PacketTypes.ProtocolVersion;
        public string ServerName { get; set; }
        public ExamInfo Exam { get; set; }
    }

    /// <summary>考试概况（不含题目）。没有开放的考试时为 null。</summary>
    public class ExamInfo
    {
        public long ExamId { get; set; }
        public string Title { get; set; }
        public ExamStatus Status { get; set; }
        public int DurationMinutes { get; set; }
        public int QuestionCount { get; set; }
        public double TotalScore { get; set; }
        /// <summary>考试中剩余毫秒数；未开始或已结束时为 0。</summary>
        public long RemainingMs { get; set; }
        public bool AntiCheat { get; set; }
    }

    public class IdentifyRequest
    {
        public string StudentNo { get; set; }
    }

    public class IdentifyResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string StudentNo { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
    }

    public class LoginRequest
    {
        public string StudentNo { get; set; }
        /// <summary>上次登录获得的令牌；断线重连时携带，用于接管旧连接。</summary>
        public string Token { get; set; }
        public string MachineName { get; set; }
    }

    public class LoginResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Token { get; set; }
        public string StudentNo { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public ExamInfo Exam { get; set; }
        public ParticipantStatus Status { get; set; }
        /// <summary>已交卷时附带的交卷结果。</summary>
        public SubmitResult Submitted { get; set; }
    }

    public class PaperMessage
    {
        public StudentPaper Paper { get; set; }
        /// <summary>服务端已保存的作答（重连时用于恢复）。</summary>
        public List<AnswerEntry> Answers { get; set; } = new List<AnswerEntry>();
        public long RemainingMs { get; set; }
    }

    public class AnswerMessage
    {
        public AnswerEntry Answer { get; set; }
    }

    public class AnswerAck
    {
        public long QuestionId { get; set; }
        public long Ticks { get; set; }
    }

    public class SubmitRequest
    {
        /// <summary>交卷时附带全部作答，保证服务端拿到最新答案。</summary>
        public List<AnswerEntry> Answers { get; set; } = new List<AnswerEntry>();
    }

    public class SubmitResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        /// <summary>是否因考试结束被强制收卷。</summary>
        public bool Forced { get; set; }
        public bool ShowScore { get; set; }
        public double ObjectiveScore { get; set; }
        public double ObjectiveTotal { get; set; }
        /// <summary>是否有待教师批改的简答题。</summary>
        public bool HasPendingManual { get; set; }
        public double TotalScore { get; set; }
        public long SubmittedAtTicks { get; set; }
    }

    public class FocusLostMessage
    {
        /// <summary>本场考试累计切屏次数（学生端本地累计，服务端取较大值）。</summary>
        public int Count { get; set; }
        public long AtTicks { get; set; }
    }

    public class PongMessage
    {
        public ExamStatus? Status { get; set; }
        public long RemainingMs { get; set; }
    }

    public class KickedMessage
    {
        public string Reason { get; set; }
    }

    public class ErrorMessage
    {
        public string Message { get; set; }
    }
}
