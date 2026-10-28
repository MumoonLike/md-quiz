using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MdQuiz.Server.Mcp
{
    public sealed class HttpRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Query { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];
        public string RemoteAddress { get; set; }

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        public string QueryValue(string name) => Query.TryGetValue(name, out var v) ? v : null;

        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    /// <summary>HTTP 响应。设置 <see cref="Stream"/> 时作为 SSE 长连接，函数返回后关闭连接。</summary>
    public sealed class HttpReply
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; }
        public byte[] Body { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Func<SseWriter, CancellationToken, Task> Stream { get; set; }

        public static HttpReply Json(int status, string json) => Text(status, json, "application/json; charset=utf-8");

        public static HttpReply Text(int status, string text, string contentType = "text/plain; charset=utf-8") =>
            new HttpReply { Status = status, ContentType = contentType, Body = Encoding.UTF8.GetBytes(text ?? "") };

        public static HttpReply Empty(int status) => new HttpReply { Status = status };
    }

    /// <summary>Server-Sent Events 输出。</summary>
    public sealed class SseWriter
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        internal SseWriter(Stream stream)
        {
            _stream = stream;
        }

        public Task SendAsync(string eventName, string data)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName).Append('\n');
            foreach (var line in (data ?? "").Replace("\r\n", "\n").Split('\n')) sb.Append("data: ").Append(line).Append('\n');
            sb.Append('\n');
            return WriteAsync(sb.ToString());
        }

        public Task CommentAsync(string text) => WriteAsync(": " + text + "\n\n");

        private async Task WriteAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// 极简 HTTP/1.1 服务（基于 TcpListener）：支持 keep-alive、Content-Length / chunked 请求体和 SSE 长连接。
    /// 不使用 HttpListener，避免 Windows 上监听局域网地址需要管理员权限（URL ACL）。
    /// </summary>
    public sealed class MiniHttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 32 * 1024;
        private const int MaxBodyBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

        private readonly Func<HttpRequest, HttpReply> _handler;
        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new ConcurrentDictionary<TcpClient, byte>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public MiniHttpServer(Func<HttpRequest, HttpReply> handler)
        {
            _handler = handler;
        }

        public int Port { get; private set; }
        public bool IsRunning => _listener != null;

        /// <summary>开始监听；端口被占用时抛出 SocketException。</summary>
        public void Start(IPAddress address, int port)
        {
            if (_listener != null) return;
            var listener = new TcpListener(address, port);
            listener.Start();
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Task.Run(() => AcceptLoop(listener, ct));
        }

        public void Stop()
        {
            var listener = _listener;
            if (listener == null) return;
            _listener = null;
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
                // 忽略
            }
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // 忽略
            }
            foreach (var c in _clients.Keys) Close(c);
            _clients.Clear();
        }

        public void Dispose() => Stop();

        private static void Close(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested) return;
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }
                _clients[client] = 0;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleConnection(client, ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 连接异常只影响该连接
                    }
                    finally
                    {
                        _clients.TryRemove(client, out _);
                        Close(client);
                    }
                });
            }
        }

        private async Task HandleConnection(TcpClient client, CancellationToken serverCt)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var input = new BufferedInput(stream);
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            while (!serverCt.IsCancellationRequested)
            {
                HttpRequest request;
                // 空闲超时：关闭连接以中断阻塞的读取（.NET Framework 的 ReadAsync 不响应取消）
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
                {
                    idle.CancelAfter(IdleTimeout);
                    using (idle.Token.Register(() => Close(client)))
                    {
                        request = await ReadRequest(input).ConfigureAwait(false);
                    }
                }
                if (request == null) return;
                request.RemoteAddress = remote;

                HttpReply reply;
                try
                {
                    reply = _handler(request) ?? HttpReply.Empty(404);
                }
                catch (Exception ex)
                {
                    reply = HttpReply.Text(500, "Internal Server Error: " + ex.Message);
                }

                var keepAlive = !string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase) && reply.Stream == null;
                await WriteHead(stream, reply, keepAlive).ConfigureAwait(false);
                if (reply.Stream != null)
                {
                    using (serverCt.Register(() => Close(client)))
                    {
                        await reply.Stream(new SseWriter(stream), serverCt).ConfigureAwait(false);
                    }
                    return;
                }
                if (reply.Body != null && reply.Body.Length > 0) await stream.WriteAsync(reply.Body, 0, reply.Body.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                if (!keepAlive) return;
            }
        }

        private static async Task WriteHead(Stream stream, HttpReply reply, bool keepAlive)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(reply.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(reply.Status)).Append("\r\n");
            if (reply.Stream != null)
            {
                sb.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
                sb.Append("Cache-Control: no-cache\r\n");
                sb.Append("Connection: keep-alive\r\n");
                sb.Append("X-Accel-Buffering: no\r\n");
            }
            else
            {
                if (!string.IsNullOrEmpty(reply.ContentType)) sb.Append("Content-Type: ").Append(reply.ContentType).Append("\r\n");
                sb.Append("Content-Length: ").Append((reply.Body?.Length ?? 0).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            }
            foreach (var h in reply.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            if (reply.Stream != null) await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 406: return "Not Acceptable";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                default: return status >= 500 ? "Internal Server Error" : "Status";
            }
        }

        /// <summary>读取一个请求；连接关闭时返回 null。</summary>
        private static async Task<HttpRequest> ReadRequest(BufferedInput input)
        {
            string line;
            do
            {
                line = await input.ReadLine(MaxHeaderBytes).ConfigureAwait(false);
                if (line == null) return null;
            } while (line.Length == 0); // 容忍请求之间多余的空行

            var parts = line.Split(' ');
            if (parts.Length < 3) return null;
            var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            var target = parts[1];
            var q = target.IndexOf('?');
            request.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString((eq >= 0 ? pair.Substring(0, eq) : pair).Replace('+', ' '));
                    var value = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : "";
                    request.Query[key] = value;
                }
            }

            var headerBytes = 0;
            while (true)
            {
                line = await input.ReadLine(MaxHeaderBytes).ConfigureAwait(false);
                if (line == null) return null;
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > MaxHeaderBytes) return null;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            if (string.Equals(request.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
            {
                request.Body = await ReadChunked(input).ConfigureAwait(false);
                if (request.Body == null) return null;
            }
            else if (long.TryParse(request.Header("Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0)
            {
                if (length > MaxBodyBytes) return null;
                request.Body = await input.ReadExact((int)length).ConfigureAwait(false);
                if (request.Body == null) return null;
            }
            return request;
        }

        private static async Task<byte[]> ReadChunked(BufferedInput input)
        {
            var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await input.ReadLine(1024).ConfigureAwait(false);
                if (sizeLine == null) return null;
                var semicolon = sizeLine.IndexOf(';');
                if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0) return null;
                if (size == 0)
                {
                    // 跳过 trailer
                    string trailer;
                    do
                    {
                        trailer = await input.ReadLine(MaxHeaderBytes).ConfigureAwait(false);
                        if (trailer == null) return null;
                    } while (trailer.Length > 0);
                    return body.ToArray();
                }
                if (body.Length + size > MaxBodyBytes) return null;
                var chunk = await input.ReadExact(size).ConfigureAwait(false);
                if (chunk == null) return null;
                body.Write(chunk, 0, chunk.Length);
                if (await input.ReadLine(16).ConfigureAwait(false) == null) return null;
            }
        }

        /// <summary>带缓冲的按行 / 按长度读取。</summary>
        private sealed class BufferedInput
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[16 * 1024];
            private int _start;
            private int _end;

            public BufferedInput(Stream stream)
            {
                _stream = stream;
            }

            private async Task<bool> Fill()
            {
                if (_start > 0 && _start == _end) _start = _end = 0;
                if (_end == _buffer.Length)
                {
                    if (_start == 0) return false;
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                int n;
                try
                {
                    n = await _stream.ReadAsync(_buffer, _end, _buffer.Length - _end).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return false;
                }
                if (n <= 0) return false;
                _end += n;
                return true;
            }

            /// <summary>读取一行（不含 CRLF）；连接关闭或超长时返回 null。</summary>
            public async Task<string> ReadLine(int maxLength)
            {
                var line = new MemoryStream();
                while (true)
                {
                    for (var i = _start; i < _end; i++)
                    {
                        if (_buffer[i] != (byte)'\n') continue;
                        line.Write(_buffer, _start, i - _start);
                        _start = i + 1;
                        var bytes = line.ToArray();
                        var len = bytes.Length > 0 && bytes[bytes.Length - 1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                        return Encoding.UTF8.GetString(bytes, 0, len);
                    }
                    line.Write(_buffer, _start, _end - _start);
                    _start = _end;
                    if (line.Length > maxLength) return null;
                    if (!await Fill().ConfigureAwait(false)) return null;
                }
            }

            public async Task<byte[]> ReadExact(int count)
            {
                var result = new byte[count];
                var copied = 0;
                while (copied < count)
                {
                    if (_start == _end && !await Fill().ConfigureAwait(false)) return null;
                    var n = Math.Min(count - copied, _end - _start);
                    Buffer.BlockCopy(_buffer, _start, result, copied, n);
                    _start += n;
                    copied += n;
                }
                return result;
            }
        }
    }
}
