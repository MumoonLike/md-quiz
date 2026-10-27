using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MdQuiz.Core.Exams;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Core.Protocol;
using MdQuiz.Server.Data;

namespace MdQuiz.Server.Hosting
{
    /// <summary>监考界面使用的考生实时状态。</summary>
    public class ParticipantView
    {
        public string StudentNo { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public ParticipantStatus Status { get; set; }
        public bool Online { get; set; }
        public string Machine { get; set; }
        public string Ip { get; set; }
        public int Answered { get; set; }
        public int QuestionCount { get; set; }
        public int FocusLost { get; set; }
        public DateTime? LoginAt { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public bool Forced { get; set; }
        public double? ObjectiveScore { get; set; }
    }

    public enum HostLogLevel
    {
        Info,
        Success,
        Warning,
    }

    public class HostLogEntry
    {
        public DateTime Time { get; set; }
        public HostLogLevel Level { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 教师端考试服务：监听学生连接，处理登录、作答、交卷，并按时自动收卷。
    /// 同一时间只主持一场考试。事件在后台线程触发，界面层需切回 UI 线程且不要阻塞。
    /// </summary>
    public sealed class ExamHost : IDisposable
    {
        private readonly object _lock = new object();
        private readonly ExamRepository _exams;
        private readonly RosterRepository _roster;
        private readonly ResourceRepository _resources;
        private readonly ResultService _results;
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _resourceCache = new Dictionary<string, string>();
        private PacketServer _server;
        private DiscoveryResponder _discovery;
        private Timer _clock;
        private Exam _exam;

        private sealed class Session
        {
            public Participant Record;
            public PacketConnection Conn;
            public readonly HashSet<long> Answered = new HashSet<long>();
        }

        private sealed class ConnInfo
        {
            public string Machine;
            public string StudentNo;
        }

        public ExamHost(Database db)
        {
            _exams = new ExamRepository(db);
            _roster = new RosterRepository(db);
            _resources = new ResourceRepository(db);
            _results = new ResultService(_exams);
        }

        public string ServerName { get; set; } = Environment.MachineName;

        public int Port { get; private set; }

        public bool IsListening => _server?.IsRunning == true;

        public bool DiscoveryEnabled => _discovery != null;

        /// <summary>当前主持的考试（只读快照，不要修改）。</summary>
        public Exam CurrentExam
        {
            get { lock (_lock) return _exam; }
        }

        public event Action ExamStateChanged;
        public event Action<ParticipantView> ParticipantChanged;
        public event Action ParticipantsReset;
        public event Action<HostLogEntry> Log;

        // ---------------- 服务启停 ----------------

        /// <summary>开始监听。TCP 端口被占用时抛出异常；UDP 发现启动失败只记录警告。</summary>
        public void Start(int port, int udpPort = Discovery.DefaultUdpPort)
        {
            lock (_lock)
            {
                if (IsListening) return;
                var server = new PacketServer(port);
                server.PacketReceived += OnPacket;
                server.ClientDisconnected += OnDisconnected;
                server.Start();
                _server = server;
                Port = server.Port;
                try
                {
                    _discovery = new DiscoveryResponder(MakeDiscoveryInfo, udpPort);
                    _discovery.Start();
                }
                catch (Exception ex)
                {
                    _discovery = null;
                    Write(HostLogLevel.Warning, $"局域网自动发现未能启动（{ex.Message}），学生需手动输入教师机地址");
                }
                _clock = new Timer(_ => Tick(), null, 1000, 1000);
                Write(HostLogLevel.Info, $"考试服务已启动，端口 {Port}");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _clock?.Dispose();
                _clock = null;
                _discovery?.Stop();
                _discovery = null;
                _server?.Stop();
                _server = null;
            }
        }

        public void Dispose() => Stop();

        private DiscoveryInfo MakeDiscoveryInfo()
        {
            lock (_lock)
            {
                return new DiscoveryInfo
                {
                    ServerName = ServerName,
                    Port = Port,
                    ExamTitle = _exam?.Title,
                    ExamStatus = _exam?.Status,
                };
            }
        }

        // ---------------- 考试控制 ----------------

        public List<ParticipantView> GetParticipants()
        {
            lock (_lock) return _sessions.Values.Select(ToView).OrderBy(v => v.ClassName).ThenBy(v => v.StudentNo).ToList();
        }

        /// <summary>开放登录（未开始的考试进入“候考中”）；也用于教师端重启后恢复进行中的考试。</summary>
        public void OpenExam(long examId)
        {
            lock (_lock)
            {
                if (_exam != null && _exam.Id != examId && _exam.Status != ExamStatus.Ended)
                    throw new InvalidOperationException($"考试“{_exam.Title}”正在进行，请先结束或取消它");
                var exam = _exams.Get(examId) ?? throw new InvalidOperationException("考试不存在");
                if (exam.Status == ExamStatus.Ended) throw new InvalidOperationException("这场考试已经结束");
                if (exam.Questions.Count == 0) throw new InvalidOperationException("试卷中没有题目");

                var students = _roster.StudentsInClasses(exam.ClassIds);
                _exams.EnsureParticipants(exam.Id, students);
                var participants = _exams.ListParticipants(exam.Id);
                if (participants.Count == 0) throw new InvalidOperationException("所选班级中没有学生，请先在“学生”页面导入花名册");

                if (_exam != null && _exam.Id != examId) DetachLocked("本场考试已关闭");

                var resumed = exam.Status != ExamStatus.Draft;
                if (exam.Status == ExamStatus.Draft)
                {
                    exam.Status = ExamStatus.Open;
                    _exams.UpdateState(exam);
                }
                _exam = exam;
                _resourceCache.Clear();
                _sessions.Clear();
                var answered = _exams.ListAllAnswers(exam.Id).Where(a => !AnswerValues.IsEmpty(a.Values)).GroupBy(a => a.StudentNo)
                    .ToDictionary(g => g.Key, g => g.Select(a => a.QuestionId).ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var p in participants)
                {
                    var s = new Session { Record = p };
                    if (answered.TryGetValue(p.StudentNo, out var ids)) s.Answered.UnionWith(ids);
                    _sessions[p.StudentNo] = s;
                }
                Write(HostLogLevel.Success, resumed ? $"已恢复考试“{exam.Title}”（{ExamTexts.StatusName(exam.Status)}）" : $"考试“{exam.Title}”已开放登录，共 {participants.Count} 名考生");
                BroadcastExamStateLocked();
                ParticipantsReset?.Invoke();
                ExamStateChanged?.Invoke();
            }
        }

        public void StartExam()
        {
            lock (_lock)
            {
                if (_exam == null || _exam.Status != ExamStatus.Open) throw new InvalidOperationException("只有候考中的考试可以开始");
                _exam.Status = ExamStatus.Running;
                _exam.StartedAt = DateTime.UtcNow;
                _exams.UpdateState(_exam);
                foreach (var s in _sessions.Values.Where(s => s.Record.Status == ParticipantStatus.Waiting))
                {
                    s.Record.Status = ParticipantStatus.Answering;
                    _exams.UpdateParticipant(s.Record);
                }
                BroadcastExamStateLocked();
                foreach (var s in _sessions.Values.Where(s => s.Conn != null && s.Record.Status == ParticipantStatus.Answering))
                    SendPaperLocked(s);
                Write(HostLogLevel.Success, $"考试开始，时长 {_exam.TotalMinutes} 分钟");
                ParticipantsReset?.Invoke();
                ExamStateChanged?.Invoke();
            }
        }

        public void ExtendExam(int minutes)
        {
            if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
            lock (_lock)
            {
                if (_exam == null || _exam.Status != ExamStatus.Running) throw new InvalidOperationException("只有考试中可以延长时间");
                _exam.ExtraMinutes += minutes;
                _exams.UpdateState(_exam);
                BroadcastExamStateLocked();
                Write(HostLogLevel.Info, $"考试时间延长 {minutes} 分钟");
                ExamStateChanged?.Invoke();
            }
        }

        /// <summary>结束考试：所有已登录未交卷的考生被强制收卷。</summary>
        public void EndExam()
        {
            lock (_lock)
            {
                if (_exam == null || _exam.Status != ExamStatus.Running) throw new InvalidOperationException("只有考试中可以结束");
                _exam.Status = ExamStatus.Ended;
                _exam.EndedAt = DateTime.UtcNow;
                _exams.UpdateState(_exam);
                var forced = 0;
                foreach (var s in _sessions.Values.Where(s => s.Record.Status == ParticipantStatus.Waiting || s.Record.Status == ParticipantStatus.Answering))
                {
                    FinalizeLocked(s, true);
                    forced++;
                }
                BroadcastExamStateLocked();
                Write(HostLogLevel.Success, forced > 0 ? $"考试已结束，{forced} 名考生被自动收卷" : "考试已结束");
                ParticipantsReset?.Invoke();
                ExamStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 关闭当前考试：候考中的考试退回“未开始”；已结束的考试不再显示在监考页。考试中不能关闭。
        /// </summary>
        public void CloseExam()
        {
            lock (_lock)
            {
                if (_exam == null) return;
                if (_exam.Status == ExamStatus.Running) throw new InvalidOperationException("考试进行中，请先结束考试");
                if (_exam.Status == ExamStatus.Open)
                {
                    _exam.Status = ExamStatus.Draft;
                    _exams.UpdateState(_exam);
                    foreach (var s in _sessions.Values.Where(s => s.Record.Status == ParticipantStatus.Waiting))
                    {
                        s.Record.Status = ParticipantStatus.Absent;
                        s.Record.Token = null;
                        _exams.UpdateParticipant(s.Record);
                    }
                    Write(HostLogLevel.Info, $"已取消考试“{_exam.Title}”的登录");
                    DetachLocked("老师已取消本场考试的登录");
                }
                else
                {
                    DetachLocked("本场考试已关闭");
                }
                BroadcastExamStateLocked();
                ParticipantsReset?.Invoke();
                ExamStateChanged?.Invoke();
            }
        }

        private void DetachLocked(string kickReason)
        {
            foreach (var s in _sessions.Values.Where(s => s.Conn != null))
            {
                s.Conn.Send(Packet.Create(PacketTypes.Kicked, new KickedMessage { Reason = kickReason }));
                if (s.Conn.Tag is ConnInfo info) info.StudentNo = null;
                s.Conn = null;
            }
            _sessions.Clear();
            _resourceCache.Clear();
            _exam = null;
        }

        /// <summary>允许学生换电脑重新登录：断开当前连接并作废登录令牌。</summary>
        public void ReleaseStudent(string studentNo)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(studentNo ?? "", out var s)) return;
                if (s.Conn != null)
                {
                    s.Conn.Send(Packet.Create(PacketTypes.Kicked, new KickedMessage { Reason = "老师已允许你换到其他电脑登录，本机已退出" }));
                    if (s.Conn.Tag is ConnInfo info) info.StudentNo = null;
                    s.Conn = null;
                }
                s.Record.Token = null;
                _exams.UpdateParticipant(s.Record);
                Write(HostLogLevel.Info, $"{s.Record.Name}（{s.Record.StudentNo}）可以在其他电脑重新登录");
                ParticipantChanged?.Invoke(ToView(s));
            }
        }

        private void Tick()
        {
            lock (_lock)
            {
                if (_exam?.Status != ExamStatus.Running || _exam.DeadlineUtc == null) return;
                if (DateTime.UtcNow < _exam.DeadlineUtc.Value) return;
                Write(HostLogLevel.Info, "考试时间到，自动收卷");
                try
                {
                    EndExam();
                }
                catch (Exception ex)
                {
                    Write(HostLogLevel.Warning, "自动收卷失败：" + ex.Message);
                }
            }
        }

        // ---------------- 消息处理 ----------------

        private void OnPacket(PacketConnection conn, Packet packet)
        {
            lock (_lock)
            {
                try
                {
                    switch (packet.Type)
                    {
                        case PacketTypes.Hello:
                            conn.Tag = new ConnInfo { Machine = packet.As<HelloMessage>().MachineName };
                            conn.Send(Packet.Create(PacketTypes.Welcome, new WelcomeMessage { ServerName = ServerName, Exam = ExamInfoLocked() }));
                            break;
                        case PacketTypes.Identify:
                            HandleIdentify(conn, packet.As<IdentifyRequest>());
                            break;
                        case PacketTypes.Login:
                            HandleLogin(conn, packet.As<LoginRequest>());
                            break;
                        case PacketTypes.Answer:
                            HandleAnswer(conn, packet.As<AnswerMessage>());
                            break;
                        case PacketTypes.Submit:
                            HandleSubmit(conn, packet.As<SubmitRequest>());
                            break;
                        case PacketTypes.FocusLost:
                            HandleFocusLost(conn, packet.As<FocusLostMessage>());
                            break;
                        case PacketTypes.Ping:
                            var info = ExamInfoLocked();
                            conn.Send(Packet.Create(PacketTypes.Pong, new PongMessage { Status = info?.Status, RemainingMs = info?.RemainingMs ?? 0 }));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Write(HostLogLevel.Warning, $"处理来自 {conn.RemoteAddress} 的请求失败：{ex.Message}");
                    conn.Send(Packet.Create(PacketTypes.Error, new ErrorMessage { Message = "教师机处理请求出错：" + ex.Message }));
                }
            }
        }

        private void OnDisconnected(PacketConnection conn)
        {
            lock (_lock)
            {
                var s = SessionOf(conn);
                if (s == null) return;
                s.Conn = null;
                if (s.Record.Status != ParticipantStatus.Submitted)
                    Write(HostLogLevel.Warning, $"{s.Record.Name}（{s.Record.StudentNo}）断开连接");
                ParticipantChanged?.Invoke(ToView(s));
            }
        }

        private Session SessionOf(PacketConnection conn)
        {
            if (!(conn.Tag is ConnInfo info) || info.StudentNo == null) return null;
            return _sessions.TryGetValue(info.StudentNo, out var s) && s.Conn == conn ? s : null;
        }

        private string CheckExamOpen()
        {
            if (_exam == null || _exam.Status == ExamStatus.Draft) return "教师机目前没有开放的考试，请等待老师开放登录";
            return null;
        }

        private void HandleIdentify(PacketConnection conn, IdentifyRequest req)
        {
            var error = CheckExamOpen();
            var no = (req.StudentNo ?? "").Trim();
            Session s = null;
            if (error == null && (no.Length == 0 || !_sessions.TryGetValue(no, out s)))
                error = no.Length == 0 ? "请输入学号" : $"学号 {no} 不在本场考试的名单中，请检查学号或联系老师";
            conn.Send(Packet.Create(PacketTypes.IdentifyResult, error != null
                ? new IdentifyResult { Ok = false, Error = error }
                : new IdentifyResult { Ok = true, StudentNo = s.Record.StudentNo, Name = s.Record.Name, ClassName = s.Record.ClassName }));
        }

        private void HandleLogin(PacketConnection conn, LoginRequest req)
        {
            LoginResult Fail(string message) => new LoginResult { Ok = false, Error = message };

            var error = CheckExamOpen();
            if (error != null)
            {
                conn.Send(Packet.Create(PacketTypes.LoginResult, Fail(error)));
                return;
            }
            var no = (req.StudentNo ?? "").Trim();
            if (!_sessions.TryGetValue(no, out var s))
            {
                conn.Send(Packet.Create(PacketTypes.LoginResult, Fail($"学号 {no} 不在本场考试的名单中")));
                return;
            }
            var p = s.Record;
            var sameClient = !string.IsNullOrEmpty(req.Token) && req.Token == p.Token;
            var online = s.Conn != null && s.Conn != conn && !s.Conn.IsClosed;

            // 已登录过的学号只能由原来的电脑（持有令牌）继续使用；换电脑必须由老师在监考页面允许（清除令牌）
            if (!sameClient && !string.IsNullOrEmpty(p.Token))
            {
                var where = $"电脑 {p.Machine ?? "?"}（{p.Ip}）";
                conn.Send(Packet.Create(PacketTypes.LoginResult, Fail(online
                    ? $"学号 {no} 已在{where}上登录。同一学号不能同时在两台电脑登录。如需换电脑，请举手请老师在监考页面“允许换电脑重新登录”。"
                    : $"学号 {no} 已在{where}上登录过。如需换电脑，请举手请老师在监考页面“允许换电脑重新登录”。")));
                Write(HostLogLevel.Warning, $"{p.Name}（{no}）尝试在 {conn.RemoteAddress} 登录，已拒绝（需老师允许换电脑）");
                return;
            }

            if (online)
            {
                // 同一客户端重连：旧连接作废
                var old = s.Conn;
                if (old.Tag is ConnInfo oldInfo) oldInfo.StudentNo = null;
                old.Close();
            }

            if (!(conn.Tag is ConnInfo info))
            {
                info = new ConnInfo();
                conn.Tag = info;
            }
            info.StudentNo = p.StudentNo;
            s.Conn = conn;

            if (!sameClient) p.Token = Guid.NewGuid().ToString("N");
            p.Machine = info.Machine ?? req.MachineName;
            p.Ip = conn.RemoteAddress;
            if (p.LoginAt == null) p.LoginAt = DateTime.UtcNow;
            if (p.Status == ParticipantStatus.Absent)
                p.Status = _exam.Status == ExamStatus.Running ? ParticipantStatus.Answering : ParticipantStatus.Waiting;
            else if (p.Status == ParticipantStatus.Waiting && _exam.Status == ExamStatus.Running)
                p.Status = ParticipantStatus.Answering;
            _exams.UpdateParticipant(p);

            var result = new LoginResult
            {
                Ok = true,
                Token = p.Token,
                StudentNo = p.StudentNo,
                Name = p.Name,
                ClassName = p.ClassName,
                Exam = ExamInfoLocked(),
                Status = p.Status,
                Submitted = p.Status == ParticipantStatus.Submitted ? _results.BuildSubmitResult(_exam, p) : null,
            };
            conn.Send(Packet.Create(PacketTypes.LoginResult, result));
            if (_exam.Status == ExamStatus.Running && p.Status == ParticipantStatus.Answering) SendPaperLocked(s);

            Write(HostLogLevel.Info, $"{p.Name}（{p.StudentNo}）{(sameClient ? "重新连接" : "登录")}，{p.Machine} {p.Ip}");
            ParticipantChanged?.Invoke(ToView(s));
        }

        private void HandleAnswer(PacketConnection conn, AnswerMessage msg)
        {
            var s = SessionOf(conn);
            var entry = msg.Answer;
            if (s == null || entry == null) return;
            if (_exam.Status != ExamStatus.Running || s.Record.Status != ParticipantStatus.Answering) return;
            if (_exam.FindQuestion(entry.QuestionId) == null) return;
            SaveAnswerLocked(s, entry);
            conn.Send(Packet.Create(PacketTypes.AnswerAck, new AnswerAck { QuestionId = entry.QuestionId, Ticks = entry.Ticks }));
            ParticipantChanged?.Invoke(ToView(s));
        }

        private void SaveAnswerLocked(Session s, AnswerEntry entry)
        {
            if (!_exams.SaveAnswerIfNewer(_exam.Id, s.Record.StudentNo, entry)) return;
            if (AnswerValues.IsEmpty(entry.Values)) s.Answered.Remove(entry.QuestionId);
            else s.Answered.Add(entry.QuestionId);
        }

        private void HandleSubmit(PacketConnection conn, SubmitRequest req)
        {
            var s = SessionOf(conn);
            if (s == null)
            {
                conn.Send(Packet.Create(PacketTypes.SubmitResult, new SubmitResult { Ok = false, Error = "请先登录" }));
                return;
            }
            if (s.Record.Status == ParticipantStatus.Submitted)
            {
                conn.Send(Packet.Create(PacketTypes.SubmitResult, _results.BuildSubmitResult(_exam, s.Record)));
                return;
            }
            if (_exam.Status != ExamStatus.Running)
            {
                conn.Send(Packet.Create(PacketTypes.SubmitResult, new SubmitResult { Ok = false, Error = "考试还没有开始" }));
                return;
            }
            foreach (var a in req.Answers ?? new List<AnswerEntry>())
            {
                if (_exam.FindQuestion(a.QuestionId) != null) SaveAnswerLocked(s, a);
            }
            FinalizeLocked(s, false);
        }

        private void FinalizeLocked(Session s, bool forced)
        {
            var p = s.Record;
            p.Status = ParticipantStatus.Submitted;
            p.SubmittedAt = DateTime.UtcNow;
            p.Forced = forced;
            _results.GradeParticipant(_exam, p);
            s.Conn?.Send(Packet.Create(PacketTypes.SubmitResult, _results.BuildSubmitResult(_exam, p)));
            if (!forced) Write(HostLogLevel.Success, $"{p.Name}（{p.StudentNo}）已交卷，答题 {s.Answered.Count}/{_exam.Questions.Count}");
            ParticipantChanged?.Invoke(ToView(s));
        }

        private void HandleFocusLost(PacketConnection conn, FocusLostMessage msg)
        {
            var s = SessionOf(conn);
            if (s == null || s.Record.Status == ParticipantStatus.Submitted) return;
            var p = s.Record;
            var before = p.FocusLost;
            p.FocusLost = msg.Count > 0 ? Math.Max(p.FocusLost, msg.Count) : p.FocusLost + 1;
            if (p.FocusLost == before) return;
            _exams.UpdateParticipant(p);
            Write(HostLogLevel.Warning, $"{p.Name}（{p.StudentNo}）切换了窗口，累计 {p.FocusLost} 次");
            ParticipantChanged?.Invoke(ToView(s));
        }

        private void SendPaperLocked(Session s)
        {
            var paper = StudentPaperBuilder.Build(_exam, s.Record.StudentNo, LoadResourcesLocked);
            var answers = _exams.ListAnswers(_exam.Id, s.Record.StudentNo)
                .Where(a => a.Ticks > 0)
                .Select(a => new AnswerEntry { QuestionId = a.QuestionId, Values = a.Values, Ticks = a.Ticks }).ToList();
            s.Conn?.Send(Packet.Create(PacketTypes.Paper, new PaperMessage { Paper = paper, Answers = answers, RemainingMs = RemainingMsLocked() }));
        }

        private IDictionary<string, string> LoadResourcesLocked(IEnumerable<string> hashes)
        {
            var list = hashes.ToList();
            var missing = list.Where(h => !_resourceCache.ContainsKey(h)).ToList();
            if (missing.Count > 0)
            {
                foreach (var kv in _resources.GetBase64(missing)) _resourceCache[kv.Key] = kv.Value;
            }
            return list.Where(_resourceCache.ContainsKey).ToDictionary(h => h, h => _resourceCache[h]);
        }

        private long RemainingMsLocked()
        {
            if (_exam?.Status != ExamStatus.Running || _exam.DeadlineUtc == null) return 0;
            return Math.Max(0, (long)(_exam.DeadlineUtc.Value - DateTime.UtcNow).TotalMilliseconds);
        }

        private ExamInfo ExamInfoLocked()
        {
            if (_exam == null || _exam.Status == ExamStatus.Draft) return null;
            return new ExamInfo
            {
                ExamId = _exam.Id,
                Title = _exam.Title,
                Status = _exam.Status,
                DurationMinutes = _exam.TotalMinutes,
                QuestionCount = _exam.Questions.Count,
                TotalScore = _exam.TotalScore,
                RemainingMs = RemainingMsLocked(),
                AntiCheat = _exam.Settings.AntiCheat,
            };
        }

        private void BroadcastExamStateLocked()
        {
            var info = ExamInfoLocked();
            var packet = Packet.Create(PacketTypes.ExamState, info);
            _server?.Broadcast(packet, c => c.Tag is ConnInfo);
        }

        private ParticipantView ToView(Session s)
        {
            var p = s.Record;
            return new ParticipantView
            {
                StudentNo = p.StudentNo,
                Name = p.Name,
                ClassName = p.ClassName,
                Status = p.Status,
                Online = s.Conn != null && !s.Conn.IsClosed,
                Machine = p.Machine,
                Ip = p.Ip,
                Answered = s.Answered.Count,
                QuestionCount = _exam?.Questions.Count ?? 0,
                FocusLost = p.FocusLost,
                LoginAt = p.LoginAt,
                SubmittedAt = p.SubmittedAt,
                Forced = p.Forced,
                ObjectiveScore = p.ObjectiveScore,
            };
        }

        private void Write(HostLogLevel level, string message)
        {
            try
            {
                Log?.Invoke(new HostLogEntry { Time = DateTime.Now, Level = level, Message = message });
            }
            catch (Exception)
            {
                // 日志回调异常不影响考试
            }
        }
    }
}
