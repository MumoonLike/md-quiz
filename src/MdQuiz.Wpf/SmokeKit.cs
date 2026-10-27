using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MdQuiz.Wpf
{
    /// <summary>
    /// 界面冒烟测试工具（命令行 --smoke 目录）：记录步骤、截图、收集绑定错误和未处理异常。
    /// 仅用于自动化测试，正常使用时不启用。
    /// </summary>
    public static class SmokeKit
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> SeenBindingErrors = new HashSet<string>();
        private static StreamWriter _log;
        private static int _shot;

        public static string OutDir { get; private set; }
        public static bool Enabled => OutDir != null;
        public static int Errors { get; private set; }
        public static int BindingErrors { get; private set; }

        /// <summary>解析命令行中的 --smoke 目录；返回是否启用。</summary>
        public static bool TryInit(string[] args, string name)
        {
            var dir = Arg(args, "--smoke");
            if (dir == null) return false;
            OutDir = Path.GetFullPath(dir);
            Directory.CreateDirectory(OutDir);
            _log = new StreamWriter(Path.Combine(OutDir, name + "-smoke.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Ui.Headless = true;
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Step("smoke start: " + name);
            return true;
        }

        public static string Arg(string[] args, string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        public static void Step(string message)
        {
            if (_log == null) return;
            lock (Sync) _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }

        public static void Error(string message, Exception ex = null)
        {
            Errors++;
            Step("ERROR " + message + (ex == null ? "" : Environment.NewLine + ex));
        }

        /// <summary>等待界面完成布局和渲染。</summary>
        public static async Task Settle(int ms = 500)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(ms);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs, string what)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition()) return true;
                }
                catch (Exception)
                {
                    // 条件中的偶发异常视为未满足
                }
                await Task.Delay(250);
            }
            Error("timeout waiting for: " + what);
            return false;
        }

        /// <summary>把窗口（含标题栏）保存为 PNG。</summary>
        public static void Capture(Window window, string name)
        {
            try
            {
                var root = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;
                var target = (FrameworkElement)root ?? window;
                var w = (int)Math.Ceiling(target.ActualWidth);
                var h = (int)Math.Ceiling(target.ActualHeight);
                if (w <= 0 || h <= 0)
                {
                    Error($"capture {name}: element has no size");
                    return;
                }
                // 先画白底，再直接渲染窗口内容（VisualBrush 会按内容边界缩放，导致截图变形）
                var background = new DrawingVisual();
                using (var dc = background.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(background);
                bitmap.Render(target);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var file = Path.Combine(OutDir, $"{++_shot:00}-{name}.png");
                using (var fs = File.Create(file)) encoder.Save(fs);
                Step($"captured {Path.GetFileName(file)} ({w}x{h})");
            }
            catch (Exception ex)
            {
                Error("capture " + name, ex);
            }
        }

        public static int ExitCode()
        {
            Step($"smoke end: errors={Errors}, bindingErrors={BindingErrors}");
            return Errors > 0 ? 1 : 0;
        }

        private sealed class BindingListener : TraceListener
        {
            private readonly StringBuilder _pending = new StringBuilder();

            public override void Write(string message) => _pending.Append(message);

            public override void WriteLine(string message)
            {
                _pending.Append(message);
                var text = _pending.ToString();
                _pending.Clear();
                lock (Sync)
                {
                    if (!SeenBindingErrors.Add(text)) return;
                }
                BindingErrors++;
                Step("BINDING " + text);
            }
        }
    }
}
