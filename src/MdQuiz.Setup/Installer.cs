using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MdQuiz.Core;
using Microsoft.Win32;

namespace MdQuiz.Setup
{
    internal sealed class InstallRequest
    {
        public string TargetDir { get; set; }
        public bool DesktopShortcut { get; set; }
        public bool Firewall { get; set; }
    }

    /// <summary>
    /// 安装与卸载。安装目录结构：
    ///   MdQuiz.Teacher.exe、.exe.config、lib\（依赖 DLL）、samples\ 等 —— 来自 payload.zip
    ///   data\          —— 程序数据，安装时创建并给 Users 写权限；卸载时默认保留
    ///   uninstall.exe  —— 本安装程序的副本
    ///   uninstall.lst  —— 安装的文件清单（隐藏），卸载、升级时据此删除，不会误删用户放进来的其他文件
    /// </summary>
    internal static class Installer
    {
        public const string UninstallerName = "uninstall.exe";
        public const string ManifestName = "uninstall.lst";
        public const string DataDirName = "data";
        public const string LibDirName = "lib";

        private static Product P => Product.Current;

        // ---------------- 安装 ----------------

        /// <summary>执行安装（在后台 STA 线程调用）。progress(百分比 0~100, 状态文字)。</summary>
        public static void Install(Payload payload, InstallRequest request, Action<double, string> progress)
        {
            var dir = request.TargetDir;
            SetupLog.Info($"安装到 {dir}（桌面快捷方式：{request.DesktopShortcut}，防火墙例外：{request.Firewall}）");
            progress(1, "正在准备安装…");
            Directory.CreateDirectory(dir);

            // 升级：删除旧版本有、新版本已经没有的文件
            var obsolete = ReadManifest(dir).Where(f => !payload.Contains(f)).ToList();
            // 覆盖旧的免安装版（DLL 直接放在程序目录）时，程序目录里的同名 DLL 会优先于 lib 中的新版本加载，一并删除
            obsolete.AddRange(payload.Files
                .Where(f => f.Path.StartsWith(LibDirName + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path.Substring(LibDirName.Length + 1))
                .Where(f => !payload.Contains(f) && File.Exists(Path.Combine(dir, f))));
            foreach (var file in obsolete) TryDeleteFile(Path.Combine(dir, file));
            if (obsolete.Count > 0) SetupLog.Info($"删除旧版本文件 {obsolete.Count} 个：{string.Join("、", obsolete)}");

            var total = Math.Max(1, payload.TotalBytes);
            payload.ExtractTo(dir, (done, name) =>
            {
                if (name != null) progress(2 + 86.0 * done / total, "正在复制 " + name);
            });
            RemoveEmptyDirectories(dir);
            SetupLog.Info($"已复制 {payload.Files.Count} 个文件，共 {payload.TotalBytes / 1024} KB");

            progress(89, "正在创建数据目录…");
            PrepareDataDir(Path.Combine(dir, DataDirName));

            progress(92, "正在注册卸载程序…");
            File.Copy(SelfPath, Path.Combine(dir, UninstallerName), true);
            WriteManifest(dir, payload.Files.Select(f => f.Path));
            WriteUninstallInfo(dir, payload.TotalBytes, request);

            progress(95, "正在创建快捷方式…");
            var exe = Path.Combine(dir, P.ExeName);
            Directory.CreateDirectory(StartMenuDir);
            ShellLink.Create(StartMenuLink, exe, dir, P.DisplayName);
            if (request.DesktopShortcut) ShellLink.Create(DesktopLink, exe, dir, P.DisplayName);
            else TryDeleteFile(DesktopLink);

            if (P.OffersFirewall)
            {
                progress(97, request.Firewall ? "正在添加防火墙例外…" : "正在检查防火墙设置…");
                if (request.Firewall) Firewall.Allow(exe);
                else Firewall.Remove();
            }

            progress(100, "安装完成");
            SetupLog.Info("安装完成");
        }

        /// <summary>创建数据目录，并允许普通用户读写（程序装在 Program Files 时，标准用户也要能保存题库、成绩和日志）。</summary>
        private static void PrepareDataDir(string dataDir)
        {
            var info = Directory.CreateDirectory(dataDir);
            try
            {
                var security = info.GetAccessControl();
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                // FAT32、U 盘等不支持权限的磁盘上本来就都能写
                SetupLog.Warn("设置数据目录权限失败：" + ex.Message);
            }
        }

        private static void WriteUninstallInfo(string dir, long bytes, InstallRequest request)
        {
            var uninstaller = Quote(Path.Combine(dir, UninstallerName));
            using (var key = Registry.LocalMachine.CreateSubKey(P.UninstallRegistryKey))
            {
                key.SetValue("DisplayName", P.DisplayName);
                key.SetValue("DisplayVersion", Product.VersionText);
                key.SetValue("Publisher", Brand.ProductName);
                key.SetValue("DisplayIcon", Path.Combine(dir, P.ExeName) + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", uninstaller + " /uninstall");
                key.SetValue("QuietUninstallString", uninstaller + " /uninstall /S");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                // 升级时沿用上次的选择
                key.SetValue("MdQuizDesktopShortcut", request.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("MdQuizFirewall", request.Firewall ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        // ---------------- 卸载 ----------------

        /// <summary>执行卸载（在后台 STA 线程调用）。uninstall.exe 自身最后由 <see cref="ScheduleSelfDelete"/> 删除。</summary>
        public static void Uninstall(string dir, bool deleteData, Action<double, string> progress)
        {
            SetupLog.Info($"卸载 {dir}（删除数据：{deleteData}）");
            if (P.OffersFirewall)
            {
                progress(5, "正在移除防火墙例外…");
                Firewall.Remove();
            }

            progress(12, "正在删除快捷方式…");
            TryDeleteFile(DesktopLink);
            TryDeleteFile(StartMenuLink);
            TryDeleteEmptyDir(StartMenuDir);

            var files = ReadManifest(dir);
            if (files.Count == 0) files = FallbackFileList(dir);
            for (var i = 0; i < files.Count; i++)
            {
                progress(15 + 70.0 * i / files.Count, "正在删除 " + files[i]);
                TryDeleteFile(Path.Combine(dir, files[i]));
            }
            TryDeleteFile(Path.Combine(dir, ManifestName));

            var dataDir = Path.Combine(dir, DataDirName);
            if (deleteData && Directory.Exists(dataDir))
            {
                progress(88, "正在删除数据…");
                try
                {
                    Directory.Delete(dataDir, true);
                }
                catch (Exception ex)
                {
                    SetupLog.Warn("删除数据目录失败：" + ex.Message);
                }
            }
            RemoveEmptyDirectories(dir);
            TryDeleteEmptyDir(dataDir); // 从没运行过程序时 data 是空的

            progress(95, "正在清理注册信息…");
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(P.UninstallRegistryKey, false);
            }
            catch (Exception ex)
            {
                SetupLog.Warn("删除卸载信息失败：" + ex.Message);
            }
            progress(100, "卸载完成");
            SetupLog.Info("卸载完成");
        }

        /// <summary>uninstall.exe 退出后删除它自己，再删除已经空了的安装目录和上级“明点知识考核系统”目录。</summary>
        public static void ScheduleSelfDelete(string dir)
        {
            var self = Path.Combine(dir, UninstallerName);
            var parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
            var script = new StringBuilder("ping -n 3 127.0.0.1 >nul");
            script.Append($" & del /f /q {Quote(self)} 2>nul");
            script.Append($" & rd {Quote(dir)} 2>nul"); // rd 不带 /s：保留了数据时目录不为空，不会删除
            if (!string.IsNullOrEmpty(parent) && string.Equals(Path.GetFileName(parent), Brand.ProductName, StringComparison.OrdinalIgnoreCase))
                script.Append($" & rd {Quote(parent)} 2>nul");
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + script + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            catch (Exception ex)
            {
                SetupLog.Warn("无法删除卸载程序自身：" + ex.Message);
            }
        }

        /// <summary>清单丢失时，按已知的程序文件删除。</summary>
        private static List<string> FallbackFileList(string dir)
        {
            SetupLog.Warn("没有找到文件清单，按默认文件列表卸载");
            var list = new List<string> { P.ExeName, P.ExeName + ".config", "firewall-teacher.bat" };
            foreach (var sub in new[] { "lib", "samples" })
            {
                var path = Path.Combine(dir, sub);
                if (Directory.Exists(path))
                    list.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(f => f.Substring(dir.TrimEnd('\\').Length + 1)));
            }
            return list;
        }

        // ---------------- 已安装信息 ----------------

        /// <summary>找已安装的版本；没有安装（或程序文件已被手动删除）时返回 null。</summary>
        public static ExistingInstall FindExisting()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(P.UninstallRegistryKey))
                {
                    var dir = key?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, P.ExeName))) return null;
                    Version version;
                    if (Version.TryParse(key.GetValue("DisplayVersion") as string ?? "", out version))
                        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
                    return new ExistingInstall
                    {
                        Dir = dir.TrimEnd('\\'),
                        Version = version,
                        DesktopShortcut = !(key.GetValue("MdQuizDesktopShortcut") is int d) || d != 0,
                        Firewall = !(key.GetValue("MdQuizFirewall") is int f) || f != 0,
                    };
                }
            }
            catch (Exception ex)
            {
                SetupLog.Warn("读取已安装信息失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>目录中是否是本程序的安装（用于确认 uninstall.exe 所在目录、升级目录）。</summary>
        public static bool LooksInstalled(string dir) => File.Exists(Path.Combine(dir, P.ExeName)) || File.Exists(Path.Combine(dir, ManifestName));

        // ---------------- 工具 ----------------

        public static string SelfPath => typeof(Installer).Assembly.Location;

        public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), P.DisplayName + ".lnk");

        public static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Brand.ProductName);

        public static string StartMenuLink => Path.Combine(StartMenuDir, P.DisplayName + ".lnk");

        private static List<string> ReadManifest(string dir)
        {
            var path = Path.Combine(dir, ManifestName);
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path, Encoding.UTF8)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && IsSafeRelative(l))
                .ToList();
        }

        private static void WriteManifest(string dir, IEnumerable<string> files)
        {
            var path = Path.Combine(dir, ManifestName);
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllLines(path, files, new UTF8Encoding(false));
            File.SetAttributes(path, FileAttributes.Hidden);
        }

        /// <summary>清单中的路径必须在安装目录内，且不在 data 目录中。</summary>
        private static bool IsSafeRelative(string path) =>
            !Path.IsPathRooted(path) && !path.Split('\\', '/').Any(s => s == "..")
            && !path.StartsWith(DataDirName + "\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>删除安装目录下空的子目录（不碰 data 目录）。</summary>
        private static void RemoveEmptyDirectories(string dir, bool top = true)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (top && string.Equals(Path.GetFileName(sub), DataDirName, StringComparison.OrdinalIgnoreCase)) continue;
                RemoveEmptyDirectories(sub, false);
                TryDeleteEmptyDir(sub);
            }
        }

        private static void TryDeleteEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"删除目录 {dir} 失败：{ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"删除 {path} 失败：{ex.Message}");
            }
        }

        public static string Quote(string path) => "\"" + path + "\"";
    }

    internal sealed class ExistingInstall
    {
        public string Dir { get; set; }
        public Version Version { get; set; }
        public bool DesktopShortcut { get; set; }
        public bool Firewall { get; set; }

        public string VersionText => Product.FormatVersion(Version);
    }
}
