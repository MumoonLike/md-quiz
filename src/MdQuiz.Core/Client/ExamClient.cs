using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MdQuiz.Core.Models;
using MdQuiz.Core.Net;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Core.Client
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        /// <summary>登录后连接中断，正在自动重连。</summary>
        Reconnecting,
    }

    /// <summary>
    /// 学生端与教师机的会话：连接、登录、作答同步、断线重连、本地答案缓存、交卷。
    /// 所有事件都在后台线程触发，界面层需要自行切回 UI 线程。
    /// </summary>
    public sealed class ExamClient : IDisposable
    {
        private const int RequestTimeoutMs = 10000;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan ServerSilenceLimit = TimeSpan.FromSeconds(15);

        private readonly object _sync = new object();
        private readonly string _cacheDir;
        private readonly string _machineName;
        private readonly Dictionary<string, TaskCompletionSource<Packet>> _pending = new Dictionary<string, TaskCompletionSource<Packet>>();
        private readonly Dictionary<long, AnswerEntry> _answers = new Dictionary<long, AnswerEntry>();
        private readonly Dictionary<long, long> _acked = new Dictionary<long, long>();
        private PacketConnection _conn;
        private Timer _pingTimer;
        private ClientCache _cache;
        private string _cachePath;
        private long _lastTicks;
        private bool _disposed;
        private bool _reconnecting;

        public ExamClient(string cacheDir, string machineName)
        {
            _cacheDir = cacheDir;
            _machineName = machineName ?? Environment.MachineName;
        }

        public string Host { get; private set; }
        public int Port { get; private set; }
        public ConnectionState State { get; private set; }
        public WelcomeMessage Welcome { get; private set; }
        public LoginResult Login { get; private set; }
        public ExamInfo Exam { get; private set; }
        public StudentPaper Paper { get; private set; }
        public SubmitResult Result { get; private set; }
        /// <summary>考试截止时间（本机 UTC 时间，按服务端剩余时间换算）。</summary>
        public DateTime? DeadlineUtc { get; private set; }
        public int FocusLostCount { get; private set; }
        public bool IsLoggedIn => Login != null && Login.Ok;
        public bool IsSubmitted => Result != null && Result.Ok;

        public event Action<ConnectionState> StateChanged;
        public event Action<ExamInfo> ExamChanged;
        public event Action<StudentPaper> PaperReceived;
        public event Action SyncChanged;
        public event Action<SubmitResult> Submitted;
        /// <summary>被教师踢下线，或重连时登录被拒绝。参数为原因。</summary>
        public event Action<string> SessionLost;

        public IDictionary<long, AnswerEntry> AnswersSnapshot()
        {
            lock (_sync) return _answers.ToDictionary(kv => kv.Key, kv => Copy(kv.Value));
        }

        public AnswerEntry GetAnswer(long questionId)
        {
            lock (_sync) return _answers.TryGetValue(questionId, out var a) ? Copy(a) : null;
        }

        /// <summary>尚未被服务端确认保存的作答数。</summary>
        public int PendingSyncCount
        {
            get
            {
                lock (_sync) return _answers.Values.Count(a => !_acked.TryGetValue(a.QuestionId, out var t) || t < a.Ticks);
            }
        }

        // ---------------- 连接与登录 ----------------

        public async Task<WelcomeMessage> ConnectAsync(string host, int port)
        {
            ThrowIfDisposed();
            Disconnect();
            Host = host;
            Port = port;
            SetState(ConnectionState.Connecting);
            try
            {
                var welcome = await OpenAsync().ConfigureAwait(false);
                SetState(ConnectionState.Connected);
                return welcome;
            }
            catch
            {
                SetState(ConnectionState.Disconnected);
                throw;
            }
        }

        private async Task<WelcomeMessage> OpenAsync()
        {
            var conn = await PacketConnection.ConnectAsync(Host, Port).ConfigureAwait(false);
            conn.PacketReceived += OnPacket;
            conn.Closed += OnClosed;
            lock (_sync) _conn = conn;
            conn.Start();
            var hello = new HelloMessage { MachineName = _machineName, ClientVersion = typeof(ExamClient).Assembly.GetName().Version.ToString() };
            var reply = await RequestAsync(Packet.Create(PacketTypes.Hello, hello), PacketTypes.Welcome).ConfigureAwait(false);
            var welcome = reply.As<WelcomeMessage>();
            Welcome = welcome;
            if (welcome.Exam != null) UpdateExam(welcome.Exam);
            EnsurePingTimer();
            return welcome;
        }

        public async Task<IdentifyResult> IdentifyAsync(string studentNo)
        {
            var reply = await RequestAsync(Packet.Create(PacketTypes.Identify, new IdentifyRequest { StudentNo = studentNo?.Trim() }), PacketTypes.IdentifyResult).ConfigureAwait(false);
            return reply.As<IdentifyResult>();
        }

        public async Task<LoginResult> LoginAsync(string studentNo)
        {
            studentNo = studentNo?.Trim();
            var examId = Exam?.ExamId ?? Welcome?.Exam?.ExamId ?? 0;
            LoadCache(examId, studentNo);
            var request = new LoginRequest { StudentNo = studentNo, Token = _cache?.Token, MachineName = _machineName };
            var reply = await RequestAsync(Packet.Create(PacketTypes.Login, request), PacketTypes.LoginResult).ConfigureAwait(false);
            var result = reply.As<LoginResult>();
            if (!result.Ok) return result;
            ApplyLogin(result);
            return result;
        }

        private void ApplyLogin(LoginResult result)
        {
            Login = result;
            if (result.Exam != null)
            {
                if (_cache == null || _cache.ExamId != result.Exam.ExamId) LoadCache(result.Exam.ExamId, result.StudentNo);
                UpdateExam(result.Exam);
            }
            lock (_sync)
            {
                if (_cache != null)
                {
                    _cache.Token = result.Token;
                    SaveCacheLocked();
                }
            }
            if (result.Status == ParticipantStatus.Submitted && result.Submitted != null) ApplySubmitResult(result.Submitted);
        }

        public void Disconnect()
        {
            PacketConnection conn;
            lock (_sync)
            {
                conn = _conn;
                _conn = null;
            }
            if (conn != null)
            {
                conn.Closed -= OnClosed;
                conn.Close();
            }
            FailPending(new IOException("连接已断开"));
            Login = null;
            Paper = null;
            Result = null;
            DeadlineUtc = null;
            lock (_sync)
            {
                _answers.Clear();
                _acked.Clear();
                _cache = null;
                _cachePath = null;
            }
            SetState(ConnectionState.Disconnected);
        }

        private void OnClosed(PacketConnection conn)
        {
            lock (_sync)
            {
                if (_conn != conn) return;
                _conn = null;
            }
            FailPending(new IOException("与教师机的连接已断开"));
            if (_disposed) return;
            if (IsLoggedIn && !IsSubmitted)
            {
                SetState(ConnectionState.Reconnecting);
                StartReconnectLoop();
            }
            else
            {
                SetState(ConnectionState.Disconnected);
            }
        }

        private void StartReconnectLoop()
        {
            lock (_sync)
            {
                if (_reconnecting) return;
                _reconnecting = true;
            }
            Task.Run(async () =>
            {
                try
                {
                    while (!_disposed && IsLoggedIn && !IsSubmitted)
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        try
                        {
                            await OpenAsync().ConfigureAwait(false);
                            var studentNo = Login.StudentNo;
                            var request = new LoginRequest { StudentNo = studentNo, Token = Login.Token, MachineName = _machineName };
                            var reply = await RequestAsync(Packet.Create(PacketTypes.Login, request), PacketTypes.LoginResult).ConfigureAwait(false);
                            var result = reply.As<LoginResult>();
                            if (!result.Ok)
                            {
                                var reason = result.Error ?? "重新登录失败";
                                CloseCurrent();
                                Login = null;
                                SetState(ConnectionState.Disconnected);
                                SessionLost?.Invoke(reason);
                                return;
                            }
                            ApplyLogin(result);
                            SetState(ConnectionState.Connected);
                            ResendUnsynced();
                            return;
                        }
                        catch (Exception)
                        {
                            CloseCurrent();
                            // 继续重试
                        }
                    }
                }
                finally
                {
                    lock (_sync) _reconnecting = false;
                }
            });
        }

        private void CloseCurrent()
        {
            PacketConnection conn;
            lock (_sync)
            {
                conn = _conn;
                _conn = null;
            }
            if (conn != null)
            {
                conn.Closed -= OnClosed;
                conn.Close();
            }
        }

        private void EnsurePingTimer()
        {
            if (_pingTimer != null) return;
            _pingTimer = new Timer(_ => PingTick(), null, PingInterval, PingInterval);
        }

        private void PingTick()
        {
            PacketConnection conn;
            lock (_sync) conn = _conn;
            if (conn == null || conn.IsClosed) return;
            if (DateTime.UtcNow - conn.LastReceivedUtc > ServerSilenceLimit)
            {
                conn.Close(); // 触发重连
                return;
            }
            conn.Send(Packet.Create(PacketTypes.Ping));
        }

        // ---------------- 作答 ----------------

        /// <summary>记录作答：立即写入本地缓存，并在联网时发送给教师机。</summary>
        public void SetAnswer(long questionId, IList<string> values)
        {
            if (IsSubmitted) return;
            AnswerEntry entry;
            lock (_sync)
            {
                var ticks = Math.Max(DateTime.UtcNow.Ticks, _lastTicks + 1);
                _lastTicks = ticks;
                entry = new AnswerEntry { QuestionId = questionId, Values = (values ?? new List<string>()).ToList(), Ticks = ticks };
                _answers[questionId] = entry;
                SaveCacheLocked();
            }
            SendAnswer(entry);
            SyncChanged?.Invoke();
        }

        private void SendAnswer(AnswerEntry entry)
        {
            PacketConnection conn;
            lock (_sync) conn = _conn;
            if (conn == null || !IsLoggedIn || State != ConnectionState.Connected) return;
            conn.Send(Packet.Create(PacketTypes.Answer, new AnswerMessage { Answer = Copy(entry) }));
        }

        private void ResendUnsynced()
        {
            List<AnswerEntry> list;
            lock (_sync) list = _answers.Values.Where(a => !_acked.TryGetValue(a.QuestionId, out var t) || t < a.Ticks).Select(Copy).ToList();
            foreach (var a in list) SendAnswer(a);
        }

        public void ReportFocusLost()
        {
            if (IsSubmitted) return;
            int count;
            lock (_sync)
            {
                FocusLostCount++;
                count = FocusLostCount;
                if (_cache != null)
                {
                    _cache.FocusLost = count;
                    SaveCacheLocked();
                }
            }
            PacketConnection conn;
            lock (_sync) conn = _conn;
            conn?.Send(Packet.Create(PacketTypes.FocusLost, new FocusLostMessage { Count = count, AtTicks = DateTime.UtcNow.Ticks }));
        }

        public async Task<SubmitResult> SubmitAsync()
        {
            List<AnswerEntry> answers;
            lock (_sync) answers = _answers.Values.Select(Copy).ToList();
            var reply = await RequestAsync(Packet.Create(PacketTypes.Submit, new SubmitRequest { Answers = answers }), PacketTypes.SubmitResult, 20000).ConfigureAwait(false);
            return reply.As<SubmitResult>();
        }

        // ---------------- 消息处理 ----------------

        private async Task<Packet> RequestAsync(Packet request, string responseType, int timeoutMs = RequestTimeoutMs)
        {
            PacketConnection conn;
            var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                conn = _conn;
                if (conn == null) throw new IOException("尚未连接教师机");
                if (_pending.TryGetValue(responseType, out var old)) old.TrySetCanceled();
                _pending[responseType] = tcs;
            }
            await conn.SendAsync(request).ConfigureAwait(false);
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false) != tcs.Task)
            {
                lock (_sync)
                {
                    if (_pending.TryGetValue(responseType, out var cur) && cur == tcs) _pending.Remove(responseType);
                }
                throw new TimeoutException("教师机没有响应，请稍后重试");
            }
            return await tcs.Task.ConfigureAwait(false);
        }

        private void FailPending(Exception error)
        {
            List<TaskCompletionSource<Packet>> list;
            lock (_sync)
            {
                list = _pending.Values.ToList();
                _pending.Clear();
            }
            foreach (var tcs in list) tcs.TrySetException(error);
        }

        private void OnPacket(PacketConnection conn, Packet packet)
        {
            switch (packet.Type)
            {
                case PacketTypes.ExamState:
                    UpdateExam(packet.As<ExamInfo>());
                    break;
                case PacketTypes.Paper:
                    ApplyPaper(packet.As<PaperMessage>());
                    break;
                case PacketTypes.AnswerAck:
                {
                    var ack = packet.As<AnswerAck>();
                    lock (_sync)
                    {
                        if (!_acked.TryGetValue(ack.QuestionId, out var t) || t < ack.Ticks) _acked[ack.QuestionId] = ack.Ticks;
                    }
                    SyncChanged?.Invoke();
                    break;
                }
                case PacketTypes.SubmitResult:
                {
                    var r = packet.As<SubmitResult>();
                    if (r.Ok) ApplySubmitResult(r);
                    break;
                }
                case PacketTypes.Pong:
                {
                    var pong = packet.As<PongMessage>();
                    if (pong.Status == ExamStatus.Running && pong.RemainingMs > 0)
                        DeadlineUtc = DateTime.UtcNow.AddMilliseconds(pong.RemainingMs);
                    break;
                }
                case PacketTypes.Kicked:
                {
                    var reason = packet.As<KickedMessage>().Reason ?? "已被教师移出考试";
                    Login = null;
                    CloseCurrent();
                    FailPending(new IOException(reason));
                    SetState(ConnectionState.Disconnected);
                    SessionLost?.Invoke(reason);
                    return;
                }
            }

            TaskCompletionSource<Packet> tcs = null;
            lock (_sync)
            {
                if (_pending.TryGetValue(packet.Type, out tcs)) _pending.Remove(packet.Type);
            }
            tcs?.TrySetResult(packet);
        }

        private void UpdateExam(ExamInfo info)
        {
            if (info == null) return;
            Exam = info;
            if (info.Status == ExamStatus.Running && info.RemainingMs > 0)
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(info.RemainingMs);
            else if (info.Status != ExamStatus.Running)
                DeadlineUtc = null;
            ExamChanged?.Invoke(info);
        }

        private void ApplyPaper(PaperMessage msg)
        {
            if (msg?.Paper == null) return;
            List<AnswerEntry> toResend;
            lock (_sync)
            {
                if (_cache == null || _cache.ExamId != msg.Paper.ExamId) LoadCache(msg.Paper.ExamId, Login?.StudentNo);
                foreach (var server in msg.Answers ?? new List<AnswerEntry>())
                {
                    if (!_answers.TryGetValue(server.QuestionId, out var local) || local.Ticks < server.Ticks)
                        _answers[server.QuestionId] = server;
                    if (!_acked.TryGetValue(server.QuestionId, out var t) || t < server.Ticks) _acked[server.QuestionId] = server.Ticks;
                    _lastTicks = Math.Max(_lastTicks, server.Ticks);
                }
                SaveCacheLocked();
                toResend = _answers.Values.Where(a => !_acked.TryGetValue(a.QuestionId, out var t) || t < a.Ticks).Select(Copy).ToList();
            }
            Paper = msg.Paper;
            if (msg.RemainingMs > 0) DeadlineUtc = DateTime.UtcNow.AddMilliseconds(msg.RemainingMs);
            PaperReceived?.Invoke(msg.Paper);
            foreach (var a in toResend) SendAnswer(a);
            SyncChanged?.Invoke();
        }

        private void ApplySubmitResult(SubmitResult result)
        {
            var first = !IsSubmitted;
            Result = result;
            lock (_sync)
            {
                foreach (var a in _answers.Values) _acked[a.QuestionId] = a.Ticks;
            }
            if (first) Submitted?.Invoke(result);
        }

        private void SetState(ConnectionState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(state);
        }

        // ---------------- 本地缓存 ----------------

        private sealed class ClientCache
        {
            public long ExamId { get; set; }
            public string StudentNo { get; set; }
            public string Token { get; set; }
            public int FocusLost { get; set; }
            public List<AnswerEntry> Answers { get; set; } = new List<AnswerEntry>();
        }

        private void LoadCache(long examId, string studentNo)
        {
            if (string.IsNullOrEmpty(_cacheDir) || examId <= 0 || string.IsNullOrEmpty(studentNo)) return;
            lock (_sync)
            {
                var name = $"{Sanitize(Host)}_{Port}_{examId}_{Sanitize(studentNo)}.json";
                var path = Path.Combine(_cacheDir, name);
                if (_cachePath == path && _cache != null) return;
                _cachePath = path;
                _cache = null;
                try
                {
                    if (File.Exists(path)) _cache = Json.Deserialize<ClientCache>(File.ReadAllText(path));
                }
                catch (Exception)
                {
                    _cache = null; // 缓存损坏时忽略
                }
                if (_cache == null) _cache = new ClientCache { ExamId = examId, StudentNo = studentNo };
                foreach (var a in _cache.Answers ?? new List<AnswerEntry>())
                {
                    if (!_answers.TryGetValue(a.QuestionId, out var cur) || cur.Ticks < a.Ticks) _answers[a.QuestionId] = a;
                    _lastTicks = Math.Max(_lastTicks, a.Ticks);
                }
                FocusLostCount = Math.Max(FocusLostCount, _cache.FocusLost);
            }
        }

        private void SaveCacheLocked()
        {
            if (_cache == null || _cachePath == null) return;
            try
            {
                _cache.Answers = _answers.Values.ToList();
                _cache.FocusLost = FocusLostCount;
                Directory.CreateDirectory(_cacheDir);
                var tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(_cache));
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
                File.Move(tmp, _cachePath);
            }
            catch (Exception)
            {
                // 本地缓存只是保险，写入失败不影响作答
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray());
        }

        private static AnswerEntry Copy(AnswerEntry a) => new AnswerEntry { QuestionId = a.QuestionId, Ticks = a.Ticks, Values = (a.Values ?? new List<string>()).ToList() };

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExamClient));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pingTimer?.Dispose();
            CloseCurrent();
            FailPending(new ObjectDisposedException(nameof(ExamClient)));
        }
    }
}
