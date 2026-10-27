using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Core.Net
{
    /// <summary>
    /// 一条 TCP 连接。消息帧格式：4 字节小端长度 + UTF-8 JSON。
    /// 同一连接上收到的消息按顺序在后台线程回调 <see cref="PacketReceived"/>。
    /// </summary>
    public sealed class PacketConnection : IDisposable
    {
        public const int MaxFrameBytes = 32 * 1024 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private int _closed;
        private int _started;

        public PacketConnection(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _client.NoDelay = true;
            _stream = client.GetStream();
            try
            {
                RemoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            }
            catch (Exception)
            {
                RemoteAddress = "?";
            }
            LastReceivedUtc = DateTime.UtcNow;
        }

        public string RemoteAddress { get; }

        /// <summary>供上层挂载会话信息。</summary>
        public object Tag { get; set; }

        public DateTime LastReceivedUtc { get; private set; }

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public event Action<PacketConnection, Packet> PacketReceived;

        public event Action<PacketConnection> Closed;

        public static async Task<PacketConnection> ConnectAsync(string host, int port, int timeoutMs = 5000)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var connect = client.ConnectAsync(host, port);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect)
                {
                    _ = connect.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException($"连接 {host}:{port} 超时");
                }
                await connect.ConfigureAwait(false);
                return new PacketConnection(client);
            }
            catch
            {
                client.Close();
                throw;
            }
        }

        /// <summary>开始接收消息（只能调用一次）。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Task.Run(ReceiveLoopAsync);
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                var header = new byte[4];
                while (!IsClosed)
                {
                    if (!await ReadExactlyAsync(header, 4).ConfigureAwait(false)) break;
                    var length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                    if (length < 0 || length > MaxFrameBytes) break;
                    var payload = new byte[length];
                    if (!await ReadExactlyAsync(payload, length).ConfigureAwait(false)) break;
                    LastReceivedUtc = DateTime.UtcNow;

                    Packet packet;
                    try
                    {
                        packet = Packet.FromBytes(payload);
                    }
                    catch (Exception)
                    {
                        continue; // 忽略无法解析的消息
                    }
                    if (packet?.Type == null) continue;
                    try
                    {
                        PacketReceived?.Invoke(this, packet);
                    }
                    catch (Exception)
                    {
                        // 上层处理异常不应中断连接
                    }
                }
            }
            catch (Exception)
            {
                // 网络错误：关闭连接
            }
            finally
            {
                Close();
            }
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await _stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public async Task SendAsync(Packet packet)
        {
            if (IsClosed) throw new IOException("连接已关闭");
            var payload = packet.ToBytes();
            if (payload.Length > MaxFrameBytes) throw new InvalidOperationException("消息过大");
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                Close();
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>发送消息，不等待、不抛异常（失败时连接会被关闭）。</summary>
        public void Send(Packet packet)
        {
            if (IsClosed) return;
            SendAsync(packet).ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _stream.Close(); } catch (Exception) { }
            try { _client.Close(); } catch (Exception) { }
            try { Closed?.Invoke(this); } catch (Exception) { }
        }

        public void Dispose() => Close();
    }
}
