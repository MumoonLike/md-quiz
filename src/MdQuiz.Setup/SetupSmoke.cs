using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MdQuiz.Setup
{
    /// <summary>
    /// 界面截图测试（--smoke 目录）：依次展示安装、升级、卸载的各个页面并截图，不执行任何安装操作。
    /// 截图和日志写到目录中，有错误（含绑定错误）时退出码为 1。
    /// </summary>
    internal static class SetupSmoke
    {
        private static StreamWriter _log;
        private static string _dir;
        private static string _prefix;
        private static int _shot;
        private static int _errors;

        public static async void Run(Application app, string outDir)
        {
            try
            {
                _dir = Path.GetFullPath(outDir);
                Directory.CreateDirectory(_dir);
                _prefix = "setup-" + Product.Current.Key.ToLowerInvariant();
                _log = new StreamWriter(Path.Combine(_dir, _prefix + "-smoke.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                Step($"smoke start: {Product.Current.DisplayName} v{Product.VersionText}");

                var payload = Payload.Open();
                Step(payload == null ? "no payload embedded" : $"payload: {payload.Files.Count} files, {payload.TotalBytes} bytes");
                var dir = Product.Current.DefaultDir;

                // 全新安装
                var w = Open(SetupContext.ForInstall(payload, null, smoke: true));
                await Shot(w, "welcome");
                w.ShowPage(w.OptionsView, animate: false);
                await Shot(w, "options");
                w.SmokeSetPath(@"C:\");
                await Shot(w, "options-invalid");
                w.SmokeSetPath(dir);
                _ = w.ConfirmAsync("需要先关闭程序", $"{Product.Current.DisplayName} 正在运行，需要先关闭它才能继续。", "关闭并继续");
                await Shot(w, "confirm");
                w.SmokeCloseDialog();
                w.SmokeShowProgress(46, @"正在复制 lib\HandyControl.dll", 0);
                await Shot(w, "progress");
                w.SmokeShowDone(dir);
                await Shot(w, "done");
                w.ShowError("无法写入文件 lib\\HandyControl.dll：文件正由另一进程使用，因此该进程无法访问此文件。\n\n请确认程序已经关闭，没有被杀毒软件拦截，然后重试。");
                await Shot(w, "error");
                w.Close();

                // 升级
                var existing = new ExistingInstall { Dir = dir, Version = new Version(0, 0, 9), DesktopShortcut = true, Firewall = true };
                w = Open(SetupContext.ForInstall(payload, existing, smoke: true));
                await Shot(w, "upgrade");
                w.ShowPage(w.OptionsView, animate: false);
                await Shot(w, "upgrade-options");
                w.Close();

                // 卸载
                w = Open(SetupContext.ForUninstall(dir, smoke: true));
                await Shot(w, "uninstall");
                w.SmokeCheckDeleteData();
                await Shot(w, "uninstall-deletedata");
                w.SmokeShowProgress(62, @"正在删除 lib\Markdig.dll", 0);
                await Shot(w, "uninstall-progress");
                w.SmokeShowDone(dir);
                await Shot(w, "uninstall-done");
                w.Close();
                payload?.Dispose();
            }
            catch (Exception ex)
            {
                Error("smoke failed", ex);
            }
            Step($"smoke end: errors={_errors}");
            _log?.Dispose();
            app.Shutdown(_errors > 0 ? 1 : 0);
        }

        private static MainWindow Open(SetupContext context)
        {
            var w = new MainWindow(context);
            w.Show();
            w.Activate();
            return w;
        }

        private static async Task Shot(Window window, string name)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, name);
        }

        /// <summary>把整个窗口（含透明的阴影边）画在浅灰底上保存为 PNG，能看出圆角和阴影。</summary>
        private static void Capture(Window window, string name)
        {
            try
            {
                var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                var w = (int)Math.Ceiling(root.ActualWidth);
                var h = (int)Math.Ceiling(root.ActualHeight);
                var background = new DrawingVisual();
                using (var dc = background.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xF0)), null, new Rect(0, 0, w, h));
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(background);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(_dir, $"{_prefix}-{++_shot:00}-{name}.png");
                using (var fs = File.Create(file)) encoder.Save(fs);
                Step($"captured {Path.GetFileName(file)} ({w}x{h})");
            }
            catch (Exception ex)
            {
                Error("capture " + name, ex);
            }
        }

        private static void Step(string message) => _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

        private static void Error(string message, Exception ex = null)
        {
            _errors++;
            Step("ERROR " + message + (ex == null ? "" : Environment.NewLine + ex));
        }

        private sealed class BindingListener : TraceListener
        {
            private readonly StringBuilder _pending = new StringBuilder();

            public override void Write(string message) => _pending.Append(message);

            public override void WriteLine(string message)
            {
                _pending.Append(message);
                Error("BINDING " + _pending);
                _pending.Clear();
            }
        }
    }
}
