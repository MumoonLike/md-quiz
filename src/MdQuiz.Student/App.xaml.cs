using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MdQuiz.Student.ViewModels;
using MdQuiz.Wpf;

namespace MdQuiz.Student
{
    public partial class App : Application
    {
        public static string DataDir { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            Ui.Configure();
            base.OnStartup(e);
            var smoke = SmokeKit.TryInit(e.Args, "student");
            if (smoke)
            {
                DataDir = Path.Combine(SmokeKit.OutDir, "student-data");
                if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
                Directory.CreateDirectory(DataDir);
            }
            else
            {
                DataDir = AppPaths.ResolveDataDir("Student");
            }
            Log.Init(DataDir);
            Log.Info("学生端启动");
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log.Error("未处理的异常", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Error("未观察的任务异常", args.Exception);
                args.SetObserved();
            };
            var main = new MainWindow();
            MainWindow = main;
            if (smoke)
            {
                main.WindowState = WindowState.Normal;
                main.Width = 1366;
                main.Height = 800;
            }
            main.Show();
            if (smoke) _ = StudentSmoke.RunAsync(main, (ShellViewModel)main.DataContext, e.Args);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            if (SmokeKit.Enabled) SmokeKit.Error("unhandled UI exception", e.Exception);
            Ui.ToastError("出现了一个错误：" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
