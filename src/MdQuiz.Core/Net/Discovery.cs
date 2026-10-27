using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MdQuiz.Core.Models;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Core.Net
{
    /// <summary>教师机信息（UDP 发现的应答内容）。</summary>
    public class DiscoveryInfo
    {
        public string Magic { get; set; } = Discovery.ResponseMagic;
        public string ServerName { get; set; }
        /// <summary>由学生端根据应答来源填写。</summary>
        public string Address { get; set; }
        public int Port { get; set; }
        public string ExamTitle { get; set; }
        public ExamStatus? ExamStatus { get; set; }
    }

    public static class Discovery
    {
        public const int DefaultTcpPort = 47320;
        public const int DefaultUdpPort = 47321;
        public const string RequestMagic = "MDQUIZ_DISCOVER_V1";
        public const string ResponseMagic = "MDQUIZ_SERVER_V1";

        /// <summary>
        /// 学生端搜索教师机：向全网广播、各网卡子网广播和本机发送请求，收集超时前的应答。
        /// 由学生端发起、教师端单播回复，因此学生机不需要在防火墙放行入站。
        /// </summary>
        public static async Task<List<DiscoveryInfo>> SearchAsync(int udpPort = DefaultUdpPort, int timeoutMs = 1500)
        {
            var found = new Dictionary<string, DiscoveryInfo>();
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                udp.EnableBroadcast = true;
                IgnoreConnectionReset(udp);
                var request = Encoding.UTF8.GetBytes(RequestMagic);
                foreach (var target in BroadcastTargets())
                {
                    try
                    {
                        await udp.SendAsync(request, request.Length, new IPEndPoint(target, udpPort)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 某些网卡不允许广播，忽略
                    }
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var receive = udp.ReceiveAsync();
                    if (await Task.WhenAny(receive, Task.Delay(left)).ConfigureAwait(false) != receive)
                    {
                        _ = receive.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        break;
                    }
                    UdpReceiveResult result;
                    try
                    {
                        result = await receive.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    try
                    {
                        var info = Json.Deserialize<DiscoveryInfo>(Encoding.UTF8.GetString(result.Buffer));
                        if (info == null || info.Magic != ResponseMagic || info.Port <= 0) continue;
                        info.Address = result.RemoteEndPoint.Address.ToString();
                        var key = info.Address + ":" + info.Port;
                        // 同一台机器可能经回环和网卡各应答一次，优先保留非回环地址
                        if (!found.ContainsKey(key)) found[key] = info;
                    }
                    catch (Exception)
                    {
                        // 忽略无法识别的应答
                    }
                }
            }

            var list = found.Values.ToList();
            var nonLoopback = list.Where(i => i.Address != "127.0.0.1").ToList();
            // 本机既有回环应答又有网卡应答时，去掉回环那条
            return list.Where(i => i.Address != "127.0.0.1" || !nonLoopback.Any(n => n.ServerName == i.ServerName && n.Port == i.Port && IsLocalAddress(n.Address))).ToList();
        }

        private static IEnumerable<IPAddress> BroadcastTargets()
        {
            var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            foreach (var (address, mask) in LocalIPv4WithMasks())
            {
                var ip = address.GetAddressBytes();
                var m = mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~m[i]);
                targets.Add(new IPAddress(broadcast));
            }
            return targets;
        }

        private static IEnumerable<(IPAddress address, IPAddress mask)> LocalIPv4WithMasks()
        {
            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    IPAddress mask = null;
                    try
                    {
                        mask = ua.IPv4Mask;
                    }
                    catch (Exception)
                    {
                        // 个别平台不支持读取掩码
                    }
                    if (mask == null || mask.Equals(IPAddress.Any)) mask = IPAddress.Parse("255.255.255.0");
                    yield return (ua.Address, mask);
                }
            }
        }

        /// <summary>本机的 IPv4 地址（不含回环），用于在教师端显示连接地址。</summary>
        public static List<string> LocalIPv4Addresses()
        {
            return LocalIPv4WithMasks().Select(x => x.address.ToString())
                .Where(a => !a.StartsWith("169.254.")).Distinct().ToList();
        }

        /// <summary>
        /// Windows 上向无人监听的端口发 UDP 后，下一次接收会抛 WSAECONNRESET；关闭该行为。
        /// </summary>
        internal static void IgnoreConnectionReset(UdpClient udp)
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try
            {
                udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // 非 Windows 平台不支持，忽略
            }
        }

        private static bool IsLocalAddress(string address) => LocalIPv4Addresses().Contains(address);
    }

    /// <summary>教师端：应答学生端的 UDP 搜索请求。</summary>
    public sealed class DiscoveryResponder : IDisposable
    {
        private readonly Func<DiscoveryInfo> _info;
        private readonly int _udpPort;
        private UdpClient _udp;
        private volatile bool _running;

        public DiscoveryResponder(Func<DiscoveryInfo> info, int udpPort = Discovery.DefaultUdpPort)
        {
            _info = info;
            _udpPort = udpPort;
        }

        public void Start()
        {
            if (_running) return;
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
            _udp.EnableBroadcast = true;
            Discovery.IgnoreConnectionReset(_udp);
            _running = true;
            Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            var request = Encoding.UTF8.GetBytes(Discovery.RequestMagic);
            while (_running)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Windows 上收到 ICMP 端口不可达时会抛 SocketException，继续接收
                    if (!_running) break;
                    continue;
                }
                if (!result.Buffer.SequenceEqual(request)) continue;
                try
                {
                    var reply = Encoding.UTF8.GetBytes(Json.Serialize(_info()));
                    await _udp.SendAsync(reply, reply.Length, result.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 忽略单次应答失败
                }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _udp?.Close(); } catch (Exception) { }
            _udp = null;
        }

        public void Dispose() => Stop();
    }
}
