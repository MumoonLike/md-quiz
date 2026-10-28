using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandyControl.Data;
using MdQuiz.Server.Hosting;
using MdQuiz.Teacher.Services;

namespace MdQuiz.Teacher.Views.Dialogs
{
    /// <summary>自定义加减考试时间。确定后由调用方执行 <see cref="ExamHost.AdjustTime"/>。</summary>
    public partial class TimeAdjustWindow
    {
        private readonly DispatcherTimer _timer;

        public TimeAdjustWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => UpdatePreview();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
            Loaded += (s, e) => MinutesInput.Focus();
            UpdatePreview();
        }

        /// <summary>调整的分钟数：正数延长，负数缩短。</summary>
        public int Minutes => (int)Math.Round(MinutesInput.Value) * (ShortenOption.IsChecked == true ? -1 : 1);

        private void OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

        private void OnMinutesChanged(object sender, FunctionEventArgs<double> e) => UpdatePreview();

        private void OnPreset(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && double.TryParse(b.Tag as string, out var m)) MinutesInput.Value = m;
        }

        private void UpdatePreview()
        {
            // 构造过程中控件尚未全部创建
            if (PreviewText == null || OkButton == null || MinutesInput == null) return;
            var exam = AppServices.Current.Host.CurrentExam;
            var remaining = AppServices.Current.Host.Remaining;
            if (exam?.DeadlineUtc == null || remaining == null)
            {
                CurrentRemaining.Text = "--:--";
                CurrentEnd.Text = "--:--";
                PreviewText.Text = "考试不在进行中，无法调整时间。";
                OkButton.IsEnabled = false;
                return;
            }
            CurrentRemaining.Text = FormatSpan(remaining.Value);
            CurrentEnd.Text = exam.DeadlineUtc.Value.ToLocalTime().ToString("HH:mm:ss");
            var minutes = Minutes;
            var newEnd = exam.DeadlineUtc.Value.AddMinutes(minutes);
            var newRemaining = newEnd - DateTime.UtcNow;
            var ok = minutes > 0 || newRemaining >= ExamHost.MinRemainingAfterAdjust;
            OkButton.IsEnabled = ok;
            PreviewBox.Background = (System.Windows.Media.Brush)FindResource(ok ? "LightPrimaryBrush" : "LightDangerBrush");
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "DarkPrimaryBrush" : "DangerBrush");
            PreviewText.Text = ok
                ? $"{(minutes > 0 ? "延长" : "缩短")} {Math.Abs(minutes)} 分钟后：{newEnd.ToLocalTime():HH:mm:ss} 结束，剩余约 {FormatSpan(newRemaining)}。学生端倒计时会立即同步。"
                : $"缩短 {Math.Abs(minutes)} 分钟后剩余时间不足 1 分钟。如需立即收卷，请直接“结束考试”。";
        }

        private static string FormatSpan(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
