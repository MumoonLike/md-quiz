using System;
using System.IO;

namespace MdQuiz.Setup
{
    /// <summary>退出码（静默安装时供部署脚本判断）。</summary>
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int Failed = 1;
        public const int InvalidTarget = 2;
        public const int NoNetFramework = 3;
        public const int AlreadyRunning = 4;
        public const int NotInstalled = 5;
    }

    /// <summary>静默安装 / 卸载（/S）：不显示任何界面，结果写入日志和退出码，便于机房批量部署。</summary>
    internal static class SilentSetup
    {
        public static int Run(SetupOptions options)
        {
            try
            {
                return options.Uninstall ? Uninstall(options) : Install(options);
            }
            catch (Exception ex)
            {
                SetupLog.Error(options.Uninstall ? "卸载失败" : "安装失败", ex);
                return ExitCodes.Failed;
            }
        }

        private static int Install(SetupOptions options)
        {
            var product = Product.Current;
            using (var payload = Payload.Open())
            {
                if (payload == null)
                {
                    SetupLog.Error("安装程序中没有程序文件");
                    return ExitCodes.Failed;
                }
                var existing = Installer.FindExisting();
                var dir = InstallPath.Normalize(options.TargetDir ?? existing?.Dir ?? product.DefaultDir);
                if (existing != null && !InstallPath.AreSame(existing.Dir, dir))
                {
                    SetupLog.Error($"已经安装在 {existing.Dir}，不能再安装到 {dir}。如需更换位置，请先卸载（数据会保留）。");
                    return ExitCodes.InvalidTarget;
                }
                var error = InstallPath.Validate(dir, payload.TotalBytes, out _);
                if (error != null)
                {
                    SetupLog.Error($"安装位置 {dir} 不可用：{error}");
                    return ExitCodes.InvalidTarget;
                }
                RunningApp.CloseAll(RunningApp.Find(dir));
                var request = new InstallRequest
                {
                    TargetDir = Path.GetFullPath(dir),
                    DesktopShortcut = !options.NoDesktop && (existing?.DesktopShortcut ?? true),
                    Firewall = product.OffersFirewall && !options.NoFirewall && (existing?.Firewall ?? true),
                };
                Installer.Install(payload, request, LogProgress());
                return ExitCodes.Success;
            }
        }

        private static int Uninstall(SetupOptions options)
        {
            var dir = App.FindUninstallDir(options);
            if (dir == null)
            {
                SetupLog.Error("没有找到已安装的程序");
                return ExitCodes.NotInstalled;
            }
            RunningApp.CloseAll(RunningApp.Find(dir));
            Installer.Uninstall(dir, options.DeleteData, LogProgress());
            Installer.ScheduleSelfDelete(dir);
            return ExitCodes.Success;
        }

        /// <summary>每 10% 记一行日志。</summary>
        private static Action<double, string> LogProgress()
        {
            var last = -10;
            return (percent, status) =>
            {
                var step = (int)percent / 10 * 10;
                if (step < last + 10) return;
                last = step;
                SetupLog.Info($"{step}% {status}");
            };
        }
    }
}
