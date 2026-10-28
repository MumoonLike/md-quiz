using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MdQuiz.Core;
using MdQuiz.Setup.Themes;

namespace MdQuiz.Setup
{
    public partial class MainWindow : Window
    {
        /// <summary>进度页至少显示这么久，避免一闪而过。</summary>
        private static readonly TimeSpan MinimumProgressTime = TimeSpan.FromMilliseconds(1600);

        private readonly SetupContext _context;
        private readonly Product _product = Product.Current;
        private readonly DispatcherTimer _tipTimer;
        private TaskCompletionSource<bool> _dialog;
        private bool _busy;
        private bool _uninstalled;
        private int _tipIndex;
        private string _installedDir;

        internal MainWindow(SetupContext context)
        {
            _context = context;
            InitializeComponent();
            Title = _product.DisplayName + (context.IsUninstall ? " 卸载程序" : " 安装程序");
            EditionText.Text = _product.Edition;
            VersionText.Text = context.IsUninstall ? "卸载程序" : $"v{Product.VersionText} · 安装程序";
            TaglineText.Text = _product.Tagline;
            Root.SizeChanged += (s, e) => Root.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);
            _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
            _tipTimer.Tick += (s, e) => ShowNextTip();
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;

            if (context.IsUninstall) InitUninstall();
            else InitInstall();
        }

        // ================= 安装 =================

        private void InitInstall()
        {
            FeatureList.ItemsSource = _product.Features.Select(f => new FeatureView(f, (Geometry)FindResource(f.Icon))).ToList();
            FirewallCheck.Visibility = _product.OffersFirewall ? Visibility.Visible : Visibility.Collapsed;

            var existing = _context.Existing;
            DesktopCheck.IsChecked = existing?.DesktopShortcut ?? true;
            FirewallCheck.IsChecked = existing?.Firewall ?? true;
            PathBox.Text = existing?.Dir ?? _product.DefaultDir;
            if (existing != null)
            {
                // 升级沿用原来的位置：换位置会留下旧文件和旧的卸载信息
                PathBox.IsReadOnly = true;
                BrowseButton.IsEnabled = false;
                var compare = existing.Version == null ? 1 : Product.Version.CompareTo(existing.Version);
                var verb = compare > 0 ? "升级" : compare == 0 ? "重新安装" : "安装";
                InstallButton.Content = OptionsInstallButton.Content = "立即" + verb;
                UpgradeText.Text = (compare > 0
                    ? $"已安装 v{existing.VersionText}，将升级到 v{Product.VersionText}。"
                    : compare == 0
                        ? $"已安装相同版本 v{existing.VersionText}，将重新安装（修复程序文件）。"
                        : $"已安装更高版本 v{existing.VersionText}，继续将换成 v{Product.VersionText}。") + _product.KeptDataNotice;
                UpgradeNotice.Visibility = Visibility.Visible;
                FeatureList.Visibility = Visibility.Collapsed;
            }
            UpdatePathInfo();
            ShowPage(WelcomePage, animate: false);
        }

        private void OnCustomizeClick(object sender, RoutedEventArgs e) => ShowPage(OptionsPage);

        private void OnBackClick(object sender, RoutedEventArgs e) => ShowPage(WelcomePage);

        private void OnPathChanged(object sender, TextChangedEventArgs e) => UpdatePathInfo();

        /// <summary>检查安装位置，显示所需 / 可用空间或错误。返回错误（没有时为 null）。</summary>
        private string UpdatePathInfo()
        {
            if (_context.Payload == null && !_context.IsSmoke) return null;
            var dir = InstallPath.Normalize(PathBox.Text);
            var error = InstallPath.Validate(dir, _context.Payload?.TotalBytes ?? 12L << 20, out var space);
            WelcomePathText.Text = "安装到 " + dir;
            WelcomePathText.ToolTip = dir;
            Assist.SetHasError(PathBox, error != null);
            PathInfo.Foreground = (Brush)FindResource(error != null ? "DangerBrush" : "SecondaryTextBrush");
            PathInfo.Text = error ?? (_context.Existing != null ? space + "。升级时不能更改位置，如需更换请先卸载旧版本（数据会保留）。" : space);
            return error;
        }

        private void OnBrowseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picked = FolderPicker.Pick(new WindowInteropHelper(this).Handle, "选择安装位置", InstallPath.Normalize(PathBox.Text));
                if (picked != null) PathBox.Text = InstallPath.FromPickedFolder(picked);
            }
            catch (Exception ex)
            {
                SetupLog.Error("选择文件夹失败", ex);
                PathBox.Focus();
            }
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var error = UpdatePathInfo();
            if (error != null)
            {
                ShowPage(OptionsPage);
                PathBox.Focus();
                PathBox.SelectAll();
                return;
            }
            var dir = Path.GetFullPath(InstallPath.Normalize(PathBox.Text));
            if (_context.Existing == null && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() && !Installer.LooksInstalled(dir))
            {
                var ok = await ConfirmAsync("文件夹不是空的", $"“{dir}”中已经有其他文件。\n安装只会放入本程序的文件，不会删除已有文件；以后卸载时也只删除本程序的文件。", "继续安装");
                if (!ok) return;
            }
            if (_context.Existing?.Version != null && Product.Version < _context.Existing.Version)
            {
                var ok = await ConfirmAsync("已安装更高版本", $"这台电脑上已经安装了 v{_context.Existing.VersionText}，比本安装程序（v{Product.VersionText}）更新。\n确定要换成旧版本吗？", "继续安装");
                if (!ok) return;
            }
            if (!await EnsureAppClosedAsync(dir)) return;

            var request = new InstallRequest
            {
                TargetDir = dir,
                DesktopShortcut = DesktopCheck.IsChecked == true,
                Firewall = _product.OffersFirewall && FirewallCheck.IsChecked == true,
            };
            ProgressTitle.Text = $"正在{(_context.Existing == null ? "安装" : "更新")} {_product.DisplayName}…";
            var ok2 = await RunWithProgressAsync(progress => Installer.Install(_context.Payload, request, progress));
            if (!ok2) return;
            _installedDir = dir;
            DoneTitle.Text = _context.Existing == null ? "安装完成" : "更新完成";
            DoneDetail.Text = $"{_product.DisplayName} 已安装到 {dir}\n可以从桌面或开始菜单中的“{_product.DisplayName}”打开。";
            LaunchButton.Visibility = Visibility.Visible;
            ShowPage(DonePage);
            LaunchButton.Focus();
        }

        /// <summary>安装完成后“立即启动”；卸载完成后“打开数据文件夹”。</summary>
        private void OnLaunchClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Shell.OpenAsUser(_context.IsUninstall
                    ? Path.Combine(_context.UninstallDir, Installer.DataDirName)
                    : Path.Combine(_installedDir, _product.ExeName));
            }
            catch (Exception ex)
            {
                SetupLog.Error(_context.IsUninstall ? "打开数据文件夹失败" : "启动程序失败", ex);
            }
            Close();
        }

        // ================= 卸载 =================

        private void InitUninstall()
        {
            UninstallTitle.Text = $"确定要卸载 {_product.DisplayName} 吗？";
            UninstallDetail.Text = $"将从 {_context.UninstallDir} 删除程序文件、快捷方式" + (_product.OffersFirewall ? "和防火墙例外。" : "。");
            DeleteDataText.Text = $"同时删除数据（{_product.DataDescription}）";
            DeleteDataWarningText.Text = _product.Key == "Teacher"
                ? "删除后无法恢复。建议先在教师端“设置”中备份数据库，或把 data 文件夹复制到其他位置。"
                : "删除后无法恢复。考试还没有结束时请不要删除。";
            ShowPage(UninstallPage, animate: false);
        }

        private void OnDeleteDataChanged(object sender, RoutedEventArgs e) =>
            DeleteDataWarning.Visibility = DeleteDataCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        private async void OnUninstallClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dir = _context.UninstallDir;
            if (!await EnsureAppClosedAsync(dir)) return;
            var deleteData = DeleteDataCheck.IsChecked == true;
            ProgressTitle.Text = $"正在卸载 {_product.DisplayName}…";
            var ok = await RunWithProgressAsync(progress => Installer.Uninstall(dir, deleteData, progress));
            if (!ok) return;
            _uninstalled = true;
            ShowUninstallDone(Directory.Exists(Path.Combine(dir, Installer.DataDirName)));
            ShowPage(DonePage);
            FinishButton.Focus();
        }

        /// <summary>卸载完成页；保留了数据时提供“打开数据文件夹”。</summary>
        private void ShowUninstallDone(bool dataKept)
        {
            DoneTitle.Text = "卸载完成";
            DoneDetail.Text = $"{_product.DisplayName} 已从这台电脑上移除。" +
                              (dataKept ? "\n数据保留在安装目录的 data 文件夹中，重新安装到原位置会继续使用。" : "");
            LaunchButton.Content = "打开数据文件夹";
            LaunchButton.Style = (Style)FindResource("SecondaryButton");
            LaunchButton.Visibility = dataKept ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= 公共流程 =================

        /// <summary>安装目录中的程序正在运行时，询问后关闭它。</summary>
        private async Task<bool> EnsureAppClosedAsync(string dir)
        {
            var running = RunningApp.Find(dir);
            if (running.Count == 0) return true;
            var ok = await ConfirmAsync("需要先关闭程序",
                $"{_product.DisplayName} 正在运行，需要先关闭它才能继续。\n" +
                (_product.Key == "Teacher" ? "正在进行的考试会中断（学生作答已实时保存，重新打开后可以恢复）。" : "正在进行的考试会中断（作答已实时保存到教师机）。"),
                "关闭并继续");
            if (!ok)
            {
                foreach (var p in running) p.Dispose();
                return false;
            }
            await Task.Run(() => RunningApp.CloseAll(running));
            return true;
        }

        /// <summary>显示进度页，在后台 STA 线程执行（快捷方式等 COM 操作需要 STA）。成功返回 true；失败时显示出错页。</summary>
        private async Task<bool> RunWithProgressAsync(Action<Action<double, string>> work)
        {
            _busy = true;
            CloseButton.IsEnabled = false;
            SetProgress(0, "正在准备…", animate: false);
            _tipIndex = 0;
            TipText.Text = _product.Tips[0];
            TipCard.Visibility = _context.IsUninstall ? Visibility.Collapsed : Visibility.Visible;
            ShowPage(ProgressPage);
            if (!_context.IsUninstall) _tipTimer.Start();

            var watch = Stopwatch.StartNew();
            Exception error = null;
            try
            {
                await RunOnStaThread(() => work((percent, status) => Dispatcher.BeginInvoke(new Action(() => SetProgress(percent, status)))));
                var remaining = MinimumProgressTime - watch.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                SetProgress(100, _context.IsUninstall ? "卸载完成" : "安装完成");
                await Task.Delay(350);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                _tipTimer.Stop();
                _busy = false;
                CloseButton.IsEnabled = true;
            }
            if (error == null) return true;

            SetupLog.Error(_context.IsUninstall ? "卸载失败" : "安装失败", error);
            ShowError(error is SetupException || error is UnauthorizedAccessException || error is IOException
                ? error.Message
                : "出现了意外错误：" + error.Message);
            return false;
        }

        private static Task RunOnStaThread(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            })
            { IsBackground = true, Name = "setup-worker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        internal void SetProgress(double percent, string status, bool animate = true)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            PercentText.Text = $"{Math.Floor(percent):0}%";
            if (status != null) ProgressStatus.Text = status;
            var animation = new DoubleAnimation(percent, animate ? TimeSpan.FromMilliseconds(260) : TimeSpan.Zero)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Progress.BeginAnimation(RangeBase.ValueProperty, animation);
        }

        internal void ShowError(string message)
        {
            ErrorTitle.Text = _context.IsUninstall ? "卸载没有完成" : "安装没有完成";
            ErrorMessage.Text = message + "\n\n日志：" + SetupLog.FilePath;
            ShowPage(ErrorPage);
            RetryButton.Focus();
        }

        private void OnRetryClick(object sender, RoutedEventArgs e) => ShowPage(_context.IsUninstall ? UninstallPage : WelcomePage);

        private void OnViewLogClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Installer.Quote(SetupLog.FilePath)));
            }
            catch (Exception ex)
            {
                SetupLog.Warn("打开日志失败：" + ex.Message);
            }
        }

        private void OnFinishClick(object sender, RoutedEventArgs e) => Close();

        private void ShowNextTip()
        {
            var tips = _product.Tips;
            if (tips.Length < 2) return;
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (s, e) =>
            {
                _tipIndex = (_tipIndex + 1) % tips.Length;
                TipText.Text = tips[_tipIndex];
                TipText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            };
            TipText.BeginAnimation(OpacityProperty, fadeOut);
        }

        // ================= 页面与对话框 =================

        internal void ShowPage(FrameworkElement page, bool animate = true)
        {
            foreach (FrameworkElement child in Pages.Children) child.Visibility = child == page ? Visibility.Visible : Visibility.Collapsed;
            if (!animate) return;
            var duration = TimeSpan.FromMilliseconds(240);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var shift = new TranslateTransform(0, 10);
            page.RenderTransform = shift;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        }

        /// <summary>窗口内的确认框（比系统 MessageBox 好看，也不会跑到窗口后面）。</summary>
        internal Task<bool> ConfirmAsync(string title, string message, string okText, bool danger = false)
        {
            _dialog?.TrySetResult(false);
            _dialog = new TaskCompletionSource<bool>();
            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogOk.Content = okText;
            DialogOk.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
            Overlay.Visibility = Visibility.Visible;
            Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            DialogOk.Focus();
            return _dialog.Task;
        }

        private void CloseDialog(bool result)
        {
            Overlay.Visibility = Visibility.Collapsed;
            var dialog = _dialog;
            _dialog = null;
            dialog?.TrySetResult(result);
        }

        private void OnDialogOk(object sender, RoutedEventArgs e) => CloseDialog(true);

        private void OnDialogCancel(object sender, RoutedEventArgs e) => CloseDialog(false);

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible)
            {
                CloseDialog(false);
                e.Handled = true;
            }
        }

        // ================= 窗口 =================

        private void OnHeroMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            // 安装、卸载进行中不能关闭，避免留下一半的文件
            if (_busy) e.Cancel = true;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (_uninstalled && !_context.IsSmoke) Installer.ScheduleSelfDelete(_context.UninstallDir);
        }

        // ================= 界面截图测试用 =================

        internal FrameworkElement WelcomeView => WelcomePage;
        internal FrameworkElement OptionsView => OptionsPage;
        internal FrameworkElement ProgressView => ProgressPage;
        internal FrameworkElement UninstallView => UninstallPage;

        internal void SmokeShowProgress(double percent, string status, int tip)
        {
            ProgressTitle.Text = $"正在{(_context.IsUninstall ? "卸载" : "安装")} {_product.DisplayName}…";
            TipCard.Visibility = _context.IsUninstall ? Visibility.Collapsed : Visibility.Visible;
            TipText.Text = _product.Tips[tip % _product.Tips.Length];
            ShowPage(ProgressPage, animate: false);
            SetProgress(percent, status, animate: false);
        }

        internal void SmokeShowDone(string dir)
        {
            _installedDir = dir;
            if (_context.IsUninstall) ShowUninstallDone(dataKept: true);
            else
            {
                DoneTitle.Text = "安装完成";
                DoneDetail.Text = $"{_product.DisplayName} 已安装到 {dir}\n可以从桌面或开始菜单中的“{_product.DisplayName}”打开。";
            }
            ShowPage(DonePage, animate: false);
        }

        internal void SmokeSetPath(string path) => PathBox.Text = path;

        internal void SmokeCheckDeleteData() => DeleteDataCheck.IsChecked = true;

        internal void SmokeCloseDialog() => CloseDialog(false);

        internal sealed class FeatureView
        {
            public FeatureView(Feature feature, Geometry geometry)
            {
                Title = feature.Title;
                Detail = feature.Detail;
                Geometry = geometry;
            }

            public string Title { get; }
            public string Detail { get; }
            public Geometry Geometry { get; }
        }
    }
}
