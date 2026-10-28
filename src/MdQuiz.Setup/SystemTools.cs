using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace MdQuiz.Setup
{
    /// <summary>
    /// 教师机的 Windows 防火墙例外，与 scripts/firewall-teacher.bat 相同（同名规则，重复执行会替换）：
    /// 放行教师端程序，以及默认的 TCP 47320（考试通信）、UDP 47321（局域网发现）端口。
    /// </summary>
    internal static class Firewall
    {
        private const string ProgramRule = "MdQuiz Teacher";
        private const string TcpRule = "MdQuiz Teacher TCP";
        private const string UdpRule = "MdQuiz Teacher UDP";

        // 与 MdQuiz.Core.Net.Discovery 的默认端口一致
        private const int TcpPort = 47320;
        private const int UdpPort = 47321;

        public static void Allow(string exePath)
        {
            Remove();
            var ok = Netsh($"advfirewall firewall add rule name=\"{ProgramRule}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any")
                     & Netsh($"advfirewall firewall add rule name=\"{TcpRule}\" dir=in action=allow protocol=TCP localport={TcpPort} profile=any")
                     & Netsh($"advfirewall firewall add rule name=\"{UdpRule}\" dir=in action=allow protocol=UDP localport={UdpPort} profile=any");
            if (!ok) SetupLog.Warn("部分防火墙规则没有添加成功（防火墙服务可能已停用），不影响安装");
        }

        public static void Remove()
        {
            foreach (var name in new[] { ProgramRule, TcpRule, UdpRule })
                Netsh($"advfirewall firewall delete rule name=\"{name}\"", quiet: true);
        }

        private static Encoding OemEncoding
        {
            get
            {
                try
                {
                    return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                }
                catch (Exception)
                {
                    return Encoding.Default;
                }
            }
        }

        private static bool Netsh(string arguments, bool quiet = false)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // netsh 按控制台代码页输出（中文系统为 GBK）
                    StandardOutputEncoding = OemEncoding,
                    StandardErrorEncoding = OemEncoding,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        SetupLog.Warn("netsh 超时：" + arguments);
                        return false;
                    }
                    if (p.ExitCode != 0 && !quiet) SetupLog.Warn($"netsh {arguments} 返回 {p.ExitCode}：{output.Trim()}");
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"无法运行 netsh {arguments}：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>安装目录中正在运行的程序（覆盖安装、卸载前需要关闭）。</summary>
    internal static class RunningApp
    {
        public static List<Process> Find(string dir)
        {
            var result = new List<Process>();
            foreach (var p in Process.GetProcessesByName(Product.Current.ProcessName))
            {
                string path = null;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // 读不到路径（其他用户的进程等）时按在安装目录中处理，宁可多提示一次
                }
                if (path == null || string.Equals(Path.GetDirectoryName(path), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) result.Add(p);
                else p.Dispose();
            }
            return result;
        }

        public static void CloseAll(IEnumerable<Process> processes)
        {
            foreach (var p in processes)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        SetupLog.Info($"结束正在运行的程序（PID {p.Id}）");
                        p.Kill();
                        p.WaitForExit(10000);
                    }
                }
                catch (Exception ex)
                {
                    SetupLog.Warn($"结束进程 {p.Id} 失败：{ex.Message}");
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// .NET Framework 4.8 检查。安装程序本身不带 .exe.config，能在任何 .NET 4.x 上启动；
    /// 这里只用 .NET 4.0 就有的功能，必须在用到更新功能的代码之前调用。
    /// </summary>
    internal static class NetFramework
    {
        public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        /// <summary>.NET Framework 4.8 的 Release 值。</summary>
        private const int Release48 = 528040;

        public static bool Has48()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var release = key?.GetValue("Release");
                    return release is int r && r >= Release48;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>安装程序旁边放着的 .NET Framework 4.8 离线安装包（ndp48-x86-x64-allos-enu.exe 等，不含语言包）。</summary>
        public static string FindOfflineInstaller(string setupPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(setupPath);
                var candidates = Directory.GetFiles(dir, "ndp48*.exe")
                    .Where(f => Path.GetFileName(f).IndexOf("-chs", StringComparison.OrdinalIgnoreCase) < 0)
                    .ToList();
                return candidates.FirstOrDefault(f => Path.GetFileName(f).IndexOf("enu", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? candidates.FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal static class Shell
    {
        /// <summary>
        /// 以普通权限打开程序或网址。安装程序以管理员身份运行，直接启动的程序也会是管理员权限
        /// （拖放文件、映射的网络盘都会出问题），通过资源管理器启动则是当前登录用户的普通权限。
        /// </summary>
        public static void OpenAsUser(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Installer.Quote(target))
                {
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"通过资源管理器打开 {target} 失败：{ex.Message}，改为直接打开");
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        }
    }
}
