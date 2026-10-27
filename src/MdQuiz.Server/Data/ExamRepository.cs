using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using MdQuiz.Core.Models;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Server.Data
{
    /// <summary>考试列表项（不含题目内容）。</summary>
    public class ExamSummary
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string PaperName { get; set; }
        public ExamStatus Status { get; set; }
        public int QuestionCount { get; set; }
        public double TotalScore { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int ParticipantCount { get; set; }
        public int SubmittedCount { get; set; }
        public int PendingManualCount { get; set; }
    }

    public sealed class ExamRepository
    {
        private readonly Database _db;

        public ExamRepository(Database db)
        {
            _db = db;
        }

        public Database Database => _db;

        // ---------------- 考试 ----------------

        private sealed class ExamRow
        {
            public long Id { get; set; }
            public string Title { get; set; }
            public long? PaperId { get; set; }
            public string PaperName { get; set; }
            public string QuestionsJson { get; set; }
            public string ClassIdsJson { get; set; }
            public string SettingsJson { get; set; }
            public long Status { get; set; }
            public long ExtraMinutes { get; set; }
            public long CreatedAt { get; set; }
            public long? StartedAt { get; set; }
            public long? EndedAt { get; set; }

            public Exam ToModel() => new Exam
            {
                Id = Id,
                Title = Title ?? "",
                PaperId = PaperId,
                PaperName = PaperName ?? "",
                Questions = Json.Deserialize<List<ExamQuestion>>(QuestionsJson) ?? new List<ExamQuestion>(),
                ClassIds = Json.Deserialize<List<long>>(ClassIdsJson) ?? new List<long>(),
                Settings = Json.Deserialize<ExamSettings>(SettingsJson) ?? new ExamSettings(),
                Status = (ExamStatus)Status,
                ExtraMinutes = (int)ExtraMinutes,
                CreatedAt = Time.FromUnixMs(CreatedAt),
                StartedAt = Time.FromUnixMs(StartedAt),
                EndedAt = Time.FromUnixMs(EndedAt),
            };
        }

        private sealed class SummaryRow
        {
            public long Id { get; set; }
            public string Title { get; set; }
            public string PaperName { get; set; }
            public long Status { get; set; }
            public long QuestionCount { get; set; }
            public double TotalScore { get; set; }
            public long CreatedAt { get; set; }
            public long? StartedAt { get; set; }
            public long? EndedAt { get; set; }
            public long ParticipantCount { get; set; }
            public long SubmittedCount { get; set; }
            public long PendingManualCount { get; set; }
        }

        public List<ExamSummary> List()
        {
            const string sql = @"SELECT e.id, e.title, e.paper_name, e.status, e.question_count, e.total_score, e.created_at, e.started_at, e.ended_at,
                       (SELECT COUNT(*) FROM participants p WHERE p.exam_id = e.id) AS participant_count,
                       (SELECT COUNT(*) FROM participants p WHERE p.exam_id = e.id AND p.status = 3) AS submitted_count,
                       (SELECT COUNT(*) FROM participants p WHERE p.exam_id = e.id AND p.pending_manual > 0) AS pending_manual_count
                FROM exams e ORDER BY e.id DESC";
            return _db.Read(c => c.Query<SummaryRow>(sql).Select(r => new ExamSummary
            {
                Id = r.Id,
                Title = r.Title,
                PaperName = r.PaperName,
                Status = (ExamStatus)r.Status,
                QuestionCount = (int)r.QuestionCount,
                TotalScore = r.TotalScore,
                CreatedAt = Time.FromUnixMs(r.CreatedAt),
                StartedAt = Time.FromUnixMs(r.StartedAt),
                EndedAt = Time.FromUnixMs(r.EndedAt),
                ParticipantCount = (int)r.ParticipantCount,
                SubmittedCount = (int)r.SubmittedCount,
                PendingManualCount = (int)r.PendingManualCount,
            }).ToList());
        }

        public Exam Get(long id)
        {
            return _db.Read(c => c.QueryFirstOrDefault<ExamRow>("SELECT * FROM exams WHERE id = @id", new { id }))?.ToModel();
        }

        /// <summary>候考中或考试中的考试（教师端重启后用于恢复）。</summary>
        public Exam FindActive()
        {
            return _db.Read(c => c.QueryFirstOrDefault<ExamRow>("SELECT * FROM exams WHERE status IN (1, 2) ORDER BY id DESC LIMIT 1"))?.ToModel();
        }

        public long Insert(Exam exam)
        {
            var now = Time.ToUnixMs(DateTime.UtcNow);
            return _db.Write((c, t) =>
            {
                c.Execute(@"INSERT INTO exams(title, paper_id, paper_name, questions_json, question_count, total_score, class_ids_json, settings_json, status, extra_minutes, created_at)
                            VALUES(@Title, @PaperId, @PaperName, @QuestionsJson, @QuestionCount, @TotalScore, @ClassIdsJson, @SettingsJson, @Status, 0, @Now)",
                    new
                    {
                        exam.Title,
                        exam.PaperId,
                        PaperName = exam.PaperName ?? "",
                        QuestionsJson = Json.Serialize(exam.Questions),
                        QuestionCount = exam.Questions.Count,
                        exam.TotalScore,
                        ClassIdsJson = Json.Serialize(exam.ClassIds),
                        SettingsJson = Json.Serialize(exam.Settings),
                        Status = (int)exam.Status,
                        Now = now,
                    }, t);
                exam.Id = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
                exam.CreatedAt = Time.FromUnixMs(now);
                return exam.Id;
            });
        }

        /// <summary>修改未开始考试的设置。</summary>
        public void UpdateDraft(Exam exam)
        {
            _db.Write((c, t) => c.Execute(@"UPDATE exams SET title = @Title, class_ids_json = @ClassIdsJson, settings_json = @SettingsJson WHERE id = @Id",
                new { exam.Id, exam.Title, ClassIdsJson = Json.Serialize(exam.ClassIds), SettingsJson = Json.Serialize(exam.Settings) }, t));
        }

        public void UpdateState(Exam exam)
        {
            _db.Write((c, t) => UpdateState(c, t, exam));
        }

        internal static void UpdateState(IDbConnection c, IDbTransaction t, Exam exam)
        {
            c.Execute("UPDATE exams SET status = @Status, extra_minutes = @ExtraMinutes, started_at = @StartedAt, ended_at = @EndedAt WHERE id = @Id",
                new
                {
                    exam.Id,
                    Status = (int)exam.Status,
                    exam.ExtraMinutes,
                    StartedAt = Time.ToUnixMs(exam.StartedAt),
                    EndedAt = Time.ToUnixMs(exam.EndedAt),
                }, t);
        }

        public void Delete(long id)
        {
            _db.Write((c, t) =>
            {
                c.Execute("DELETE FROM answers WHERE exam_id = @id", new { id }, t);
                c.Execute("DELETE FROM participants WHERE exam_id = @id", new { id }, t);
                c.Execute("DELETE FROM exams WHERE id = @id", new { id }, t);
            });
        }

        // ---------------- 考生 ----------------

        private sealed class ParticipantRow
        {
            public long ExamId { get; set; }
            public string StudentNo { get; set; }
            public string Name { get; set; }
            public string ClassName { get; set; }
            public long Status { get; set; }
            public string Token { get; set; }
            public string Machine { get; set; }
            public string Ip { get; set; }
            public long? LoginAt { get; set; }
            public long? SubmittedAt { get; set; }
            public long Forced { get; set; }
            public long FocusLost { get; set; }
            public double? ObjectiveScore { get; set; }
            public double? SubjectiveScore { get; set; }
            public double? TotalScore { get; set; }
            public long PendingManual { get; set; }

            public Participant ToModel() => new Participant
            {
                ExamId = ExamId,
                StudentNo = StudentNo,
                Name = Name ?? "",
                ClassName = ClassName ?? "",
                Status = (ParticipantStatus)Status,
                Token = Token,
                Machine = Machine,
                Ip = Ip,
                LoginAt = Time.FromUnixMs(LoginAt),
                SubmittedAt = Time.FromUnixMs(SubmittedAt),
                Forced = Forced != 0,
                FocusLost = (int)FocusLost,
                ObjectiveScore = ObjectiveScore,
                SubjectiveScore = SubjectiveScore,
                TotalScore = TotalScore,
                PendingManual = (int)PendingManual,
            };
        }

        /// <summary>开放登录时按花名册生成考生名单（已存在的考生保持不变）。</summary>
        public void EnsureParticipants(long examId, IEnumerable<Student> students)
        {
            _db.Write((c, t) =>
            {
                foreach (var s in students)
                {
                    c.Execute("INSERT OR IGNORE INTO participants(exam_id, student_no, name, class_name) VALUES(@examId, @StudentNo, @Name, @ClassName)",
                        new { examId, s.StudentNo, s.Name, s.ClassName }, t);
                }
            });
        }

        public List<Participant> ListParticipants(long examId)
        {
            return _db.Read(c => c.Query<ParticipantRow>("SELECT * FROM participants WHERE exam_id = @examId ORDER BY class_name, student_no", new { examId })
                .Select(r => r.ToModel()).ToList());
        }

        public Participant GetParticipant(long examId, string studentNo)
        {
            return _db.Read(c => c.QueryFirstOrDefault<ParticipantRow>("SELECT * FROM participants WHERE exam_id = @examId AND student_no = @studentNo",
                new { examId, studentNo }))?.ToModel();
        }

        public void UpdateParticipant(Participant p)
        {
            _db.Write((c, t) => UpdateParticipant(c, t, p));
        }

        internal static void UpdateParticipant(IDbConnection c, IDbTransaction t, Participant p)
        {
            c.Execute(@"UPDATE participants SET status = @Status, token = @Token, machine = @Machine, ip = @Ip, login_at = @LoginAt,
                        submitted_at = @SubmittedAt, forced = @Forced, focus_lost = @FocusLost, objective_score = @ObjectiveScore,
                        subjective_score = @SubjectiveScore, total_score = @TotalScore, pending_manual = @PendingManual
                        WHERE exam_id = @ExamId AND student_no = @StudentNo",
                new
                {
                    p.ExamId,
                    p.StudentNo,
                    Status = (int)p.Status,
                    p.Token,
                    p.Machine,
                    p.Ip,
                    LoginAt = Time.ToUnixMs(p.LoginAt),
                    SubmittedAt = Time.ToUnixMs(p.SubmittedAt),
                    Forced = p.Forced ? 1 : 0,
                    p.FocusLost,
                    p.ObjectiveScore,
                    p.SubjectiveScore,
                    p.TotalScore,
                    p.PendingManual,
                }, t);
        }

        // ---------------- 作答 ----------------

        private sealed class AnswerRow
        {
            public long ExamId { get; set; }
            public string StudentNo { get; set; }
            public long QuestionId { get; set; }
            public string ValueJson { get; set; }
            public long Ticks { get; set; }
            public double? Score { get; set; }
            public long Manual { get; set; }

            public AnswerRecord ToModel() => new AnswerRecord
            {
                ExamId = ExamId,
                StudentNo = StudentNo,
                QuestionId = QuestionId,
                Values = Json.Deserialize<List<string>>(ValueJson) ?? new List<string>(),
                Ticks = Ticks,
                Score = Score,
                Manual = Manual != 0,
            };
        }

        /// <summary>保存作答；只有比已保存的更新（Ticks 更大）时才覆盖。返回是否写入。</summary>
        public bool SaveAnswerIfNewer(long examId, string studentNo, AnswerEntry entry)
        {
            return _db.Write((c, t) => SaveAnswerIfNewer(c, t, examId, studentNo, entry));
        }

        internal static bool SaveAnswerIfNewer(IDbConnection c, IDbTransaction t, long examId, string studentNo, AnswerEntry entry)
        {
            var n = c.Execute(@"INSERT INTO answers(exam_id, student_no, question_id, value_json, ticks) VALUES(@examId, @studentNo, @QuestionId, @ValueJson, @Ticks)
                                ON CONFLICT(exam_id, student_no, question_id) DO UPDATE SET value_json = excluded.value_json, ticks = excluded.ticks
                                WHERE excluded.ticks > answers.ticks",
                new { examId, studentNo, entry.QuestionId, ValueJson = Json.Serialize(entry.Values ?? new List<string>()), entry.Ticks }, t);
            return n > 0;
        }

        public List<AnswerRecord> ListAnswers(long examId, string studentNo)
        {
            return _db.Read(c => c.Query<AnswerRow>("SELECT * FROM answers WHERE exam_id = @examId AND student_no = @studentNo", new { examId, studentNo })
                .Select(r => r.ToModel()).ToList());
        }

        public List<AnswerRecord> ListAnswersForQuestion(long examId, long questionId)
        {
            return _db.Read(c => c.Query<AnswerRow>("SELECT * FROM answers WHERE exam_id = @examId AND question_id = @questionId", new { examId, questionId })
                .Select(r => r.ToModel()).ToList());
        }

        public List<AnswerRecord> ListAllAnswers(long examId)
        {
            return _db.Read(c => c.Query<AnswerRow>("SELECT * FROM answers WHERE exam_id = @examId", new { examId })
                .Select(r => r.ToModel()).ToList());
        }

        internal static void SaveScore(IDbConnection c, IDbTransaction t, AnswerRecord a)
        {
            c.Execute(@"INSERT INTO answers(exam_id, student_no, question_id, value_json, ticks, score, manual) VALUES(@ExamId, @StudentNo, @QuestionId, @ValueJson, @Ticks, @Score, @Manual)
                        ON CONFLICT(exam_id, student_no, question_id) DO UPDATE SET score = excluded.score, manual = excluded.manual",
                new { a.ExamId, a.StudentNo, a.QuestionId, ValueJson = Json.Serialize(a.Values ?? new List<string>()), a.Ticks, a.Score, Manual = a.Manual ? 1 : 0 }, t);
        }
    }
}
