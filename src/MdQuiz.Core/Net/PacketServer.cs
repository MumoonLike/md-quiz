using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Core.Net
{
    /// <summary>TCP 服务端：接受连接，并关闭长时间没有收到任何消息（含心跳）的连接。</summary>
    public sealed class PacketServer : IDisposable
    {
        private readonly ConcurrentDictionary<PacketConnection, byte> _connections = new ConcurrentDictionary<PacketConnection, byte>();
        private TcpListener _listener;
        private Timer _idleTimer;
        private volatile bool _running;

        public PacketServer(int port)
        {
            Port = port;
        }

        public int Port { get; private set; }

        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(20);

        public bool IsRunning => _running;

        public IReadOnlyCollection<PacketConnection> Connections => _connections.Keys.ToList();

        public event Action<PacketConnection> ClientConnected;

        public event Action<PacketConnection> ClientDisconnected;

        public event Action<PacketConnection, Packet> PacketReceived;

        /// <summary>开始监听。端口被占用时抛出 <see cref="SocketException"/>。</summary>
        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            Task.Run(AcceptLoopAsync);
            _idleTimer = new Timer(_ => CloseIdleConnections(), null, 2000, 2000);
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (!_running) break;
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                PacketConnection conn;
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    conn = new PacketConnection(client);
                }
                catch (Exception)
                {
                    client.Close();
                    continue;
                }
                conn.PacketReceived += (c, p) => PacketReceived?.Invoke(c, p);
                conn.Closed += c =>
                {
                    if (_connections.TryRemove(c, out _)) ClientDisconnected?.Invoke(c);
                };
                _connections.TryAdd(conn, 0);
                try
                {
                    ClientConnected?.Invoke(conn);
                }
                catch (Exception)
                {
                    // 忽略上层异常
                }
                conn.Start();
            }
        }

        private void CloseIdleConnections()
        {
            var limit = DateTime.UtcNow - IdleTimeout;
            foreach (var conn in _connections.Keys)
            {
                if (conn.LastReceivedUtc < limit) conn.Close();
            }
        }

        public void Broadcast(Packet packet, Func<PacketConnection, bool> filter = null)
        {
            foreach (var conn in _connections.Keys)
            {
                if (filter == null || filter(conn)) conn.Send(packet);
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _idleTimer?.Dispose();
            _idleTimer = null;
            try { _listener.Stop(); } catch (Exception) { }
            foreach (var conn in _connections.Keys) conn.Close();
        }

        public void Dispose() => Stop();
    }
}
