using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MdQuiz.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MdQuiz.Server.Mcp
{
    /// <summary>MCP 工具的权限分级（可组合）。</summary>
    [Flags]
    public enum McpPermissions
    {
        None = 0,
        /// <summary>只读查询：题库、试卷、考试、成绩、监考状态。</summary>
        Read = 1,
        /// <summary>编辑题库、组卷、创建考试。</summary>
        Edit = 2,
        /// <summary>考试控制与改分：开放 / 开始 / 结束考试、加减时间、人工改分。</summary>
        Control = 4,
    }

    public sealed class McpOptions
    {
        public const int DefaultPort = 47330;

        public int Port { get; set; } = DefaultPort;
        /// <summary>允许局域网访问（监听所有网卡）。开启后必须携带访问令牌。</summary>
        public bool AllowLan { get; set; }
        public string Token { get; set; }
        public McpPermissions Permissions { get; set; } = McpPermissions.Read;

        public bool RequireToken => AllowLan;

        public McpOptions Clone() => (McpOptions)MemberwiseClone();

        public static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    /// <summary>一次工具调用记录（教师端显示在设置页，写操作弹出通知）。</summary>
    public sealed class McpActivity
    {
        public DateTime Time { get; set; }
        public string Tool { get; set; }
        public string Title { get; set; }
        public McpPermissions Level { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Client { get; set; }
    }

    /// <summary>JSON-RPC 协议错误。</summary>
    public sealed class McpProtocolException : Exception
    {
        public McpProtocolException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }

    /// <summary>
    /// MCP（Model Context Protocol）服务：让 AI 助手通过工具查询和管理题库、试卷、考试和成绩。
    /// <list type="bullet">
    /// <item>Streamable HTTP：POST /mcp（JSON 响应，无会话状态）</item>
    /// <item>旧版 HTTP+SSE（2024-11-05）：GET /sse + POST /messages?sessionId=…</item>
    /// </list>
    /// 默认只监听 127.0.0.1；允许局域网访问时监听所有网卡并要求 Authorization: Bearer 令牌。
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        public const string Endpoint = "/mcp";
        public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        private static readonly JsonSerializerSettings OutputSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Local,
            DateFormatString = "yyyy-MM-dd HH:mm:ss",
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };

        private readonly object _lock = new object();
        private readonly McpToolset _tools;
        private readonly ConcurrentDictionary<string, SseSession> _sessions = new ConcurrentDictionary<string, SseSession>();
        private MiniHttpServer _http;
        private McpOptions _options = new McpOptions();

        public McpServer(McpToolset tools)
        {
            _tools = tools;
        }

        public bool IsRunning => _http?.IsRunning == true;
        public int Port => _http?.Port ?? 0;

        public McpOptions Options
        {
            get { lock (_lock) return _options.Clone(); }
        }

        /// <summary>每次工具调用后触发（后台线程）。</summary>
        public event Action<McpActivity> Activity;

        public static string Version => Brand.FormatVersion(typeof(McpServer).Assembly.GetName().Version);

        // ---------------- 启停 ----------------

        /// <summary>按设置启动（已在运行时先停止）。端口被占用时抛出异常。</summary>
        public void Start(McpOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.RequireToken && string.IsNullOrWhiteSpace(options.Token))
                throw new InvalidOperationException("允许局域网访问时必须设置访问令牌");
            lock (_lock)
            {
                Stop();
                _options = options.Clone();
                var http = new MiniHttpServer(Route);
                http.Start(options.AllowLan ? IPAddress.Any : IPAddress.Loopback, options.Port);
                _http = http;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _http?.Stop();
                _http = null;
                foreach (var s in _sessions.Values) s.Close();
                _sessions.Clear();
            }
        }

        public void Dispose() => Stop();

        /// <summary>只修改权限（不需要重启监听）。</summary>
        public void UpdatePermissions(McpPermissions permissions)
        {
            lock (_lock) _options.Permissions = permissions;
        }

        // ---------------- HTTP 路由 ----------------

        private HttpReply Route(HttpRequest req)
        {
            var options = Options;
            var origin = req.Header("Origin");
            if (!string.IsNullOrEmpty(origin) && !OriginAllowed(origin, options))
                return HttpReply.Text(403, "Forbidden origin");

            HttpReply reply;
            if (req.Method == "OPTIONS")
            {
                reply = HttpReply.Empty(204);
                reply.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                reply.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, Accept, Mcp-Session-Id, MCP-Protocol-Version, Last-Event-ID";
                reply.Headers["Access-Control-Max-Age"] = "600";
            }
            else if (req.Path == "/" && req.Method == "GET")
            {
                reply = HttpReply.Text(200, $"{Brand.ProductName} MCP 服务运行中。\n\nStreamable HTTP 端点：{Endpoint}\nSSE 端点（旧版客户端）：/sse\n");
            }
            else if (!IsKnownPath(req.Path))
            {
                reply = HttpReply.Text(404, "Not Found");
            }
            else if (!Authorized(req, options))
            {
                reply = HttpReply.Json(401, "{\"error\":\"unauthorized\",\"message\":\"缺少或错误的访问令牌，请在请求头中携带 Authorization: Bearer <令牌>\"}");
                reply.Headers["WWW-Authenticate"] = "Bearer realm=\"mdquiz\"";
            }
            else if (req.Path == Endpoint)
            {
                reply = req.Method == "POST" ? HandlePost(req) : MethodNotAllowed();
            }
            else if (req.Path == "/sse")
            {
                reply = req.Method == "GET" ? OpenSse(req) : MethodNotAllowed();
            }
            else
            {
                reply = req.Method == "POST" ? HandleSseMessage(req) : MethodNotAllowed();
            }

            if (!string.IsNullOrEmpty(origin))
            {
                reply.Headers["Access-Control-Allow-Origin"] = origin;
                reply.Headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id, WWW-Authenticate";
                reply.Headers["Vary"] = "Origin";
            }
            return reply;
        }

        private static bool IsKnownPath(string path) => path == Endpoint || path == "/sse" || path == "/messages";

        private static HttpReply MethodNotAllowed()
        {
            var reply = HttpReply.Text(405, "Method Not Allowed");
            reply.Headers["Allow"] = "POST";
            return reply;
        }

        /// <summary>防止 DNS 重绑定：浏览器发来的请求只接受本机来源（允许局域网时由令牌保护）。</summary>
        private static bool OriginAllowed(string origin, McpOptions options)
        {
            if (options.AllowLan) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Authorized(HttpRequest req, McpOptions options)
        {
            if (!options.RequireToken) return true;
            var given = req.QueryValue("token");
            var auth = req.Header("Authorization");
            if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = auth.Substring(7).Trim();
            return given != null && FixedTimeEquals(given, options.Token ?? "");
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private HttpReply HandlePost(HttpRequest req)
        {
            JToken message;
            try
            {
                message = JToken.Parse(req.BodyText);
            }
            catch (JsonException)
            {
                return HttpReply.Json(400, ErrorResponse(null, -32700, "Parse error").ToString(Formatting.None));
            }
            var response = HandleMessage(message, req.RemoteAddress);
            // 只有通知或响应时返回 202，不带内容
            return response == null ? HttpReply.Empty(202) : HttpReply.Json(200, response.ToString(Formatting.None));
        }

        // ---------------- 旧版 SSE 传输 ----------------

        private sealed class SseSession
        {
            private readonly BlockingCollection<string> _queue = new BlockingCollection<string>();

            public void Post(string json)
            {
                try
                {
                    _queue.Add(json);
                }
                catch (InvalidOperationException)
                {
                    // 会话已关闭
                }
            }

            public void Close() => _queue.CompleteAdding();

            public bool TryTake(out string json, int timeoutMs)
            {
                try
                {
                    return _queue.TryTake(out json, timeoutMs);
                }
                catch (InvalidOperationException)
                {
                    json = null;
                    return false;
                }
            }

            public bool IsClosed => _queue.IsAddingCompleted;
        }

        private HttpReply OpenSse(HttpRequest req)
        {
            var id = Guid.NewGuid().ToString("N");
            var session = new SseSession();
            _sessions[id] = session;
            var endpoint = "/messages?sessionId=" + id;
            var token = req.QueryValue("token");
            if (!string.IsNullOrEmpty(token)) endpoint += "&token=" + Uri.EscapeDataString(token);
            return new HttpReply
            {
                Stream = async (writer, ct) =>
                {
                    try
                    {
                        await writer.SendAsync("endpoint", endpoint).ConfigureAwait(false);
                        while (!ct.IsCancellationRequested && !session.IsClosed)
                        {
                            // 阻塞等待放到线程池，每 15 秒发送一次心跳
                            var got = await Task.Run(() => session.TryTake(out var json, 15000) ? json : null).ConfigureAwait(false);
                            if (got != null) await writer.SendAsync("message", got).ConfigureAwait(false);
                            else await writer.CommentAsync("ping").ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                        // 客户端断开
                    }
                    finally
                    {
                        session.Close();
                        _sessions.TryRemove(id, out _);
                    }
                },
            };
        }

        private HttpReply HandleSseMessage(HttpRequest req)
        {
            var id = req.QueryValue("sessionId");
            if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var session))
                return HttpReply.Text(404, "Session not found");
            JToken message;
            try
            {
                message = JToken.Parse(req.BodyText);
            }
            catch (JsonException)
            {
                return HttpReply.Text(400, "Invalid JSON");
            }
            var response = HandleMessage(message, req.RemoteAddress);
            if (response != null) session.Post(response.ToString(Formatting.None));
            return HttpReply.Text(202, "Accepted");
        }

        // ---------------- JSON-RPC ----------------

        /// <summary>处理一条（或一批）JSON-RPC 消息，返回响应；全是通知时返回 null。</summary>
        public JToken HandleMessage(JToken message, string client = null)
        {
            if (message is JArray batch)
            {
                if (batch.Count == 0) return ErrorResponse(null, -32600, "Invalid Request");
                var responses = new JArray();
                foreach (var m in batch)
                {
                    var r = HandleSingle(m, client);
                    if (r != null) responses.Add(r);
                }
                return responses.Count == 0 ? null : responses;
            }
            return HandleSingle(message, client);
        }

        private JToken HandleSingle(JToken message, string client)
        {
            if (!(message is JObject obj)) return ErrorResponse(null, -32600, "Invalid Request");
            var id = obj["id"];
            var method = obj.Value<string>("method");
            if (method == null) return null; // 客户端发来的响应，本服务不发请求，忽略
            var isNotification = id == null;
            try
            {
                var result = Dispatch(method, obj["params"] as JObject, client);
                if (isNotification) return null;
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JObject() };
            }
            catch (McpProtocolException ex)
            {
                return isNotification ? null : ErrorResponse(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return isNotification ? null : ErrorResponse(id, -32603, "Internal error: " + ex.Message);
            }
        }

        private static JObject ErrorResponse(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };
        }

        private JToken Dispatch(string method, JObject p, string client)
        {
            switch (method)
            {
                case "initialize":
                    return Initialize(p);
                case "ping":
                case "logging/setLevel":
                    return new JObject();
                case "tools/list":
                    return new JObject { ["tools"] = new JArray(VisibleTools().Select(t => t.Describe())) };
                case "tools/call":
                    return CallTool(p, client);
                case "resources/list":
                    return new JObject { ["resources"] = new JArray() };
                case "resources/templates/list":
                    return new JObject { ["resourceTemplates"] = new JArray() };
                case "prompts/list":
                    return new JObject { ["prompts"] = new JArray() };
                default:
                    if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null;
                    throw new McpProtocolException(-32601, "Method not found: " + method);
            }
        }

        private JObject Initialize(JObject p)
        {
            var requested = p?.Value<string>("protocolVersion");
            var version = SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
            var perms = Options.Permissions;
            var levels = new List<string>();
            if (perms.HasFlag(McpPermissions.Read)) levels.Add("只读查询");
            if (perms.HasFlag(McpPermissions.Edit)) levels.Add("编辑题库 / 组卷 / 创建考试");
            if (perms.HasFlag(McpPermissions.Control)) levels.Add("考试控制与改分");
            return new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                ["serverInfo"] = new JObject { ["name"] = "mdquiz", ["title"] = Brand.ProductName, ["version"] = Version },
                ["instructions"] = $"{Brand.ProductName}（局域网考试系统）教师端。可用的操作：{(levels.Count == 0 ? "无（教师端未开放任何权限）" : string.Join("、", levels))}。"
                                   + "题型：单选、多选、判断、填空、简答。选择题答案用选项字母（如 \"AC\"），判断题用 T/F，填空题每空一个答案、多个可接受答案用 | 分隔。"
                                   + "时间均为教师机本地时间。",
            };
        }

        private IEnumerable<McpTool> VisibleTools()
        {
            var perms = Options.Permissions;
            return _tools.All.Where(t => perms.HasFlag(t.Level));
        }

        private JObject CallTool(JObject p, string client)
        {
            var name = p?.Value<string>("name");
            if (string.IsNullOrEmpty(name)) throw new McpProtocolException(-32602, "缺少工具名称");
            var tool = _tools.Find(name) ?? throw new McpProtocolException(-32602, "Unknown tool: " + name);
            var activity = new McpActivity { Time = DateTime.Now, Tool = tool.Name, Title = tool.Title, Level = tool.Level, Client = client };
            if (!Options.Permissions.HasFlag(tool.Level))
            {
                activity.Message = "未开放的权限：" + LevelName(tool.Level);
                Raise(activity);
                return ToolError($"教师端没有开放“{LevelName(tool.Level)}”权限，不能使用工具 {tool.Name}。请老师在“设置 → MCP 服务”中开启后重试。");
            }
            try
            {
                var output = tool.Run(new McpArgs(p["arguments"] as JObject ?? new JObject()));
                activity.Success = true;
                activity.Message = output.Summary ?? tool.Title;
                Raise(activity);
                var text = JsonConvert.SerializeObject(output.Data, OutputSettings);
                return new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = false,
                };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is KeyNotFoundException)
            {
                activity.Message = ex.Message;
                Raise(activity);
                return ToolError(ex.Message);
            }
        }

        private static JObject ToolError(string message)
        {
            return new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = message }),
                ["isError"] = true,
            };
        }

        private void Raise(McpActivity activity)
        {
            try
            {
                Activity?.Invoke(activity);
            }
            catch (Exception)
            {
                // 界面回调异常不影响调用结果
            }
        }

        public static string LevelName(McpPermissions level)
        {
            switch (level)
            {
                case McpPermissions.Read: return "只读查询";
                case McpPermissions.Edit: return "编辑题库、组卷、创建考试";
                case McpPermissions.Control: return "考试控制与改分";
                default: return level.ToString();
            }
        }
    }
}
