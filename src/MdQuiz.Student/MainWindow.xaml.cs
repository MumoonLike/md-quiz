using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using MdQuiz.Student.ViewModels;
using MdQuiz.Wpf;

namespace MdQuiz.Student
{
    public partial class MainWindow : Window
    {
        private readonly ShellViewModel _vm;
        private readonly DispatcherTimer _focusCheck;
        private readonly DispatcherTimer _warningHide;
        private bool _locked;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new ShellViewModel();
            DataContext = _vm;
            _vm.LockdownChanged += ApplyLockdown;

            // 窗口失去焦点后稍等片刻再判断：打开本程序自己的对话框不算切屏
            _focusCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _focusCheck.Tick += (s, e) => CheckFocus();
            _warningHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _warningHide.Tick += (s, e) =>
            {
                _warningHide.Stop();
                FocusWarning.Visibility = Visibility.Collapsed;
            };
            Deactivated += (s, e) =>
            {
                if (_locked) _focusCheck.Start();
            };
        }

        /// <summary>防作弊：答题时全屏置顶（覆盖任务栏），交卷后恢复。</summary>
        private void ApplyLockdown()
        {
            var lockdown = _vm.Lockdown;
            if (lockdown == _locked) return;
            _locked = lockdown;
            if (lockdown)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                WindowState = WindowState.Maximized;
                Activate();
            }
            else
            {
                Topmost = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
        }

        private void CheckFocus()
        {
            _focusCheck.Stop();
            if (!_locked) return;
            if (Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) return;
            _vm.Client.ReportFocusLost();
            FocusWarningText.Text = $"检测到你离开了答题窗口，已记录并上报老师（第 {_vm.Client.FocusLostCount} 次）";
            FocusWarning.Visibility = Visibility.Visible;
            _warningHide.Stop();
            _warningHide.Start();
            Log.Warn("答题窗口失去焦点，已上报");
            // 抢回焦点
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Topmost = false;
                Topmost = true;
                Activate();
                Focus();
            }), DispatcherPriority.ApplicationIdle);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm.Screen == Screen.Exam && !_vm.Client.IsSubmitted)
            {
                if (_locked)
                {
                    e.Cancel = true;
                    Ui.ToastWarning("考试进行中，交卷后才能关闭程序");
                    return;
                }
                if (!Ui.Confirm("还没有交卷，确定要退出吗？\n\n已作答的内容已保存，重新打开并登录后可以继续答题。", "退出考试"))
                {
                    e.Cancel = true;
                    return;
                }
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.ExamVm.StopTimers();
            _vm.Client.Dispose();
            base.OnClosed(e);
        }
    }
}
