using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MdQuiz.Wpf.Controls
{
    public enum ToastKind
    {
        Success,
        Info,
        Warning,
        Error,
    }

    /// <summary>一条右下角通知。产出文件的通知带“在资源管理器中打开 / 打开文件”。</summary>
    public sealed class ToastItem : INotifyPropertyChanged
    {
        private readonly DispatcherTimer _timer;
        private bool _closing;

        public ToastItem(ToastKind kind, string message, string filePath)
        {
            Kind = kind;
            Message = message ?? "";
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
            // 带操作按钮或出错的通知停留更久
            Duration = TimeSpan.FromSeconds(FilePath != null ? 10 : kind == ToastKind.Error || kind == ToastKind.Warning ? 6 : 4);
            _timer = new DispatcherTimer { Interval = Duration };
            _timer.Tick += (s, e) => Close();
            CloseCommand = new DelegateCommand(Close);
            RevealCommand = new DelegateCommand(Reveal);
            OpenFileCommand = new DelegateCommand(OpenFile);
        }

        public ToastKind Kind { get; }
        public string Message { get; }
        public string FilePath { get; }
        public TimeSpan Duration { get; }
        public bool HasFile => FilePath != null;
        public string FileName => FilePath == null ? "" : Path.GetFileName(FilePath.TrimEnd('\\', '/'));
        public bool IsDirectory => FilePath != null && Directory.Exists(FilePath);

        public ICommand CloseCommand { get; }
        public ICommand RevealCommand { get; }
        public ICommand OpenFileCommand { get; }

        internal event Action<ToastItem> CloseRequested;

        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }

        internal void StartTimer() => _timer.Start();

        internal void PauseTimer() => _timer.Stop();

        internal void ResumeTimer()
        {
            if (_closing) return;
            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(2, Duration.TotalSeconds / 2));
            _timer.Start();
        }

        public void Close()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            CloseRequested?.Invoke(this);
        }

        /// <summary>在资源管理器中打开文件所在目录并选中该文件。</summary>
        private void Reveal()
        {
            if (FilePath == null) return;
            try
            {
                if (Directory.Exists(FilePath)) Process.Start("explorer.exe", "\"" + FilePath + "\"");
                else if (File.Exists(FilePath)) Process.Start("explorer.exe", "/select,\"" + FilePath + "\"");
                else
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                    else Ui.Error("文件已不存在：" + FilePath);
                }
                Close();
            }
            catch (Exception ex)
            {
                Ui.Error("无法打开资源管理器：" + ex.Message);
            }
        }

        private void OpenFile()
        {
            if (FilePath == null) return;
            try
            {
                if (!File.Exists(FilePath) && !Directory.Exists(FilePath))
                {
                    Ui.Error("文件已不存在：" + FilePath);
                    return;
                }
                Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
                Close();
            }
            catch (Exception ex)
            {
                Ui.Error("无法打开文件：" + ex.Message + "\n\n可以点击“在资源管理器中打开”后手动打开。");
            }
        }
    }

    /// <summary>
    /// 窗口右下角的通知区域。每个窗口可以放一个；通知显示在当前活动窗口的通知区域中（没有时显示在主窗口）。
    /// </summary>
    public class ToastHost : ItemsControl
    {
        private const int MaxVisible = 4;
        private static readonly List<WeakReference<ToastHost>> Hosts = new List<WeakReference<ToastHost>>();
        private readonly ObservableCollection<ToastItem> _items = new ObservableCollection<ToastItem>();

        static ToastHost()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(typeof(ToastHost)));
        }

        public ToastHost()
        {
            ItemsSource = _items;
            Focusable = false;
            Loaded += (s, e) => Register(this);
            Unloaded += (s, e) => Unregister(this);
        }

        private static void Register(ToastHost host)
        {
            lock (Hosts)
            {
                Hosts.RemoveAll(w => !w.TryGetTarget(out var h) || h == host);
                Hosts.Add(new WeakReference<ToastHost>(host));
            }
        }

        private static void Unregister(ToastHost host)
        {
            lock (Hosts) Hosts.RemoveAll(w => !w.TryGetTarget(out var h) || h == host);
        }

        /// <summary>在当前活动窗口（或主窗口）的通知区域显示通知；没有通知区域时返回 false。必须在界面线程调用。</summary>
        public static bool Show(ToastItem item)
        {
            List<ToastHost> hosts;
            lock (Hosts) hosts = Hosts.Select(w => w.TryGetTarget(out var h) ? h : null).Where(h => h != null && h.IsVisible).ToList();
            if (hosts.Count == 0) return false;
            var active = Ui.ActiveWindow;
            var host = hosts.FirstOrDefault(h => Window.GetWindow(h) == active)
                       ?? hosts.FirstOrDefault(h => Window.GetWindow(h) == Application.Current?.MainWindow)
                       ?? hosts.Last();
            host.Add(item);
            return true;
        }

        private void Add(ToastItem item)
        {
            item.CloseRequested += Remove;
            _items.Add(item);
            while (_items.Count > MaxVisible) _items.First().Close();
            item.StartTimer();
        }

        private void Remove(ToastItem item)
        {
            if (!(ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container))
            {
                _items.Remove(item);
                return;
            }
            container.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (s, e) => _items.Remove(item);
            container.BeginAnimation(OpacityProperty, fade);
            if (container.RenderTransform is TranslateTransform t)
                t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(180)));
        }

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (!(element is FrameworkElement fe) || !(item is ToastItem toast)) return;
            var transform = new TranslateTransform(48, 0);
            fe.RenderTransform = transform;
            fe.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            fe.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            // 鼠标停留时暂停自动关闭，方便点击按钮
            fe.MouseEnter += (s, e) => toast.PauseTimer();
            fe.MouseLeave += (s, e) => toast.ResumeTimer();
        }
    }

    /// <summary>简单命令（MdQuiz.Wpf 不依赖 MVVM 工具包）。</summary>
    public sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;

        public DelegateCommand(Action execute)
        {
            _execute = execute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter) => _execute();
    }
}
