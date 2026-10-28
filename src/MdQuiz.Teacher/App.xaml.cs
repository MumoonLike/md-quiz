using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MdQuiz.Core;
using MdQuiz.Teacher.Services;
using MdQuiz.Teacher.Views;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher
{
    public partial class App : Application
    {
        private AppServices _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            Ui.Configure();
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log.Error("未处理的异常", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error("未观察的任务异常", args.Exception);
                args.SetObserved();
            };

            var smoke = SmokeKit.TryInit(e.Args, "teacher");
            try
            {
                _services = AppServices.Create(smoke ? TeacherSmoke.PrepareDataDir() : null);
            }
            catch (Exception ex)
            {
                Log.Error("初始化失败", ex);
                MessageBox.Show("无法打开数据文件：" + ex.Message, Brand.TeacherTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            Log.Info("教师端启动，数据目录：" + _services.DataDir);

            if (e.Args.Any(a => string.Equals(a, "--reset-password", StringComparison.OrdinalIgnoreCase)))
            {
                if (MessageBox.Show("确定要清除管理密码吗？清除后下次启动需要重新设置密码。", "重置密码",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                {
                    _services.Settings.ClearPassword();
                    Log.Warn("管理密码已通过 --reset-password 清除");
                }
            }

            if (smoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = TeacherSmoke.RunAsync(_services);
                return;
            }

            var login = new LoginWindow(_services);
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            var main = new MainWindow(_services);
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _services?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("退出时关闭服务失败", ex);
            }
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            if (SmokeKit.Enabled) SmokeKit.Error("unhandled UI exception", e.Exception);
            Ui.Error("操作失败：" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
