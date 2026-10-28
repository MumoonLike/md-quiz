using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MdQuiz.Setup
{
    public partial class App : Application
    {
        private Mutex _mutex;
        private static string _title = Product.Current.DisplayName;

        /// <summary>
        /// 注意：本方法只能用 .NET 4.0 就有的功能。安装程序不带 .exe.config，可能运行在没装 4.8 的电脑上，
        /// 要先检查框架版本，再进入 <see cref="Start"/>（其中会用到更新的功能）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var options = SetupOptions.Parse(e.Args, Installer.SelfPath);
            _title = Product.Current.DisplayName + (options.Uninstall ? " 卸载" : " 安装");
            SetupLog.Open(options);
            DispatcherUnhandledException += OnUnhandledException;

            bool createdNew;
            _mutex = new Mutex(true, @"Global\MdQuiz.Setup." + Product.Current.Key, out createdNew);
            if (!createdNew && options.SmokeDir == null)
            {
                SetupLog.Warn("另一个安装 / 卸载程序正在运行");
                if (!options.Silent) MessageBox.Show(Product.Current.DisplayName + " 的安装或卸载程序已经在运行。", _title, MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(ExitCodes.AlreadyRunning);
                return;
            }

            if (options.SmokeDir == null && !NetFramework.Has48())
            {
                SetupLog.Error(".NET Framework 4.8 未安装");
                if (!options.Silent) PromptNetFramework(options);
                Shutdown(ExitCodes.NoNetFramework);
                return;
            }
            Start(options);
        }

        private void Start(SetupOptions options)
        {
            if (options.SmokeDir != null)
            {
                SetupSmoke.Run(this, options.SmokeDir);
                return;
            }
            if (options.Silent)
            {
                Shutdown(SilentSetup.Run(options));
                return;
            }

            SetupContext context;
            if (options.Uninstall)
            {
                var dir = FindUninstallDir(options);
                if (dir == null)
                {
                    MessageBox.Show("没有找到已安装的 " + Product.Current.DisplayName + "。", _title, MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(ExitCodes.NotInstalled);
                    return;
                }
                context = SetupContext.ForUninstall(dir);
            }
            else
            {
                var payload = Payload.Open();
                if (payload == null)
                {
                    MessageBox.Show("安装程序不完整（没有包含程序文件），请重新下载。", _title, MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(ExitCodes.Failed);
                    return;
                }
                context = SetupContext.ForInstall(payload, Installer.FindExisting());
            }

            var window = new MainWindow(context);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }

        /// <summary>要卸载的目录：uninstall.exe 所在目录；用安装程序加 /uninstall 运行时取注册表中的安装位置。</summary>
        internal static string FindUninstallDir(SetupOptions options)
        {
            var selfDir = Path.GetDirectoryName(options.SelfPath);
            if (Installer.LooksInstalled(selfDir)) return selfDir;
            return Installer.FindExisting()?.Dir;
        }

        /// <summary>没有 .NET Framework 4.8：有离线安装包就运行它，否则打开下载页面。</summary>
        private static void PromptNetFramework(SetupOptions options)
        {
            var offline = NetFramework.FindOfflineInstaller(options.SelfPath);
            var name = Product.Current.DisplayName;
            if (offline != null)
            {
                var answer = MessageBox.Show(
                    name + " 需要 Microsoft .NET Framework 4.8，这台电脑还没有安装。\n\n" +
                    "检测到同一文件夹中的安装包：" + Path.GetFileName(offline) + "\n是否现在安装？安装完成后（如提示重启，请先重启电脑）再重新运行本安装程序。",
                    _title, MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (answer != MessageBoxResult.OK) return;
                try
                {
                    Process.Start(offline);
                }
                catch (Exception ex)
                {
                    SetupLog.Error("无法运行 .NET Framework 安装包", ex);
                    MessageBox.Show("无法运行 " + Path.GetFileName(offline) + "：" + ex.Message, _title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }
            var open = MessageBox.Show(
                name + " 需要 Microsoft .NET Framework 4.8，这台电脑还没有安装（Windows 10 1903 及以上、Windows 11 已自带）。\n\n" +
                "是否打开微软官网下载页面？请下载“.NET Framework 4.8 Runtime”，安装后再重新运行本安装程序。\n\n" +
                "机房电脑没有网络时，可以把离线安装包 ndp48-x86-x64-allos-enu.exe 和本安装程序放在同一文件夹，运行本程序时会提示安装。",
                _title, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes) Shell.OpenAsUser(NetFramework.DownloadUrl);
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            SetupLog.Error("未处理的异常", e.Exception);
            MessageBox.Show("出现了意外错误：" + e.Exception.Message + "\n\n详细信息见日志：" + SetupLog.FilePath, _title, MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SetupLog.Info("退出，代码 " + e.ApplicationExitCode);
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
