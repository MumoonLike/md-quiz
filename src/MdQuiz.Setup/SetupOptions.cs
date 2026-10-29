using System;
using System.IO;
using System.Linq;

namespace MdQuiz.Setup
{
    /// <summary>
    /// 命令行参数（不区分大小写，/ 与 - 开头均可）：
    ///   /S                   静默安装 / 卸载，不显示界面（也认 /silent、/quiet、/verysilent）
    ///   /D=路径              安装位置（与 NSIS 相同，必须放在最后，路径含空格也不用加引号；也可写 /dir=路径）
    ///   /nodesktop           不创建桌面快捷方式
    ///   /nofirewall          不添加防火墙例外（教师端）
    ///   /uninstall           卸载（安装目录中的 uninstall.exe 默认就是卸载）
    ///   /deletedata          卸载时同时删除数据（仅静默卸载时使用；界面中由用户勾选）
    ///   /log=文件            日志写到指定文件（默认 %TEMP%\MdQuiz-产品-Setup.log）
    ///   --smoke 目录         界面截图测试，不安装任何东西
    /// </summary>
    internal sealed class SetupOptions
    {
        public bool Silent { get; private set; }
        public bool Uninstall { get; private set; }
        public string TargetDir { get; private set; }
        public bool NoDesktop { get; private set; }
        public bool NoFirewall { get; private set; }
        public bool DeleteData { get; private set; }
        public string LogFile { get; private set; }
        public string SmokeDir { get; private set; }

        /// <summary>本程序（安装程序或 uninstall.exe）的完整路径。</summary>
        public string SelfPath { get; private set; }

        public static SetupOptions Parse(string[] args, string selfPath)
        {
            var o = new SetupOptions { SelfPath = selfPath };
            for (var i = 0; i < args.Length; i++)
            {
                var raw = args[i];
                var a = raw.TrimStart('/', '-');
                var lower = a.ToLowerInvariant();
                if (raw.Length == a.Length) continue; // 不是开关
                if (lower == "s" || lower == "silent" || lower == "quiet" || lower == "verysilent" || lower == "q") o.Silent = true;
                else if (lower == "uninstall") o.Uninstall = true;
                else if (lower == "nodesktop") o.NoDesktop = true;
                else if (lower == "nofirewall") o.NoFirewall = true;
                else if (lower == "deletedata") o.DeleteData = true;
                else if (lower.StartsWith("d=", StringComparison.Ordinal))
                {
                    // NSIS 习惯：/D= 之后的全部内容都是路径
                    o.TargetDir = string.Join(" ", new[] { a.Substring(2) }.Concat(args.Skip(i + 1))).Trim().Trim('"');
                    break;
                }
                else if (lower.StartsWith("dir=", StringComparison.Ordinal)) o.TargetDir = a.Substring(4).Trim().Trim('"');
                else if (lower.StartsWith("log=", StringComparison.Ordinal)) o.LogFile = a.Substring(4).Trim().Trim('"');
                else if (lower == "smoke" && i + 1 < args.Length) o.SmokeDir = args[++i];
            }
            // 安装目录中的 uninstall.exe：直接双击也是卸载
            if (string.Equals(Path.GetFileName(selfPath), Installer.UninstallerName, StringComparison.OrdinalIgnoreCase)) o.Uninstall = true;
            return o;
        }
    }
}
