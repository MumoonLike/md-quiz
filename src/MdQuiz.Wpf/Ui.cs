using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using HandyControl.Controls;
using HandyControl.Tools;
using HcMessageBox = HandyControl.Controls.MessageBox;
using Window = System.Windows.Window;

namespace MdQuiz.Wpf
{
    /// <summary>两个程序共用的界面初始化与对话框工具。</summary>
    public static class Ui
    {
        private static bool _configured;

        public static readonly FontFamily AppFont = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        /// <summary>在创建任何窗口之前调用：中文字体、中文语言、清晰文字渲染、HandyControl 中文。</summary>
        public static void Configure()
        {
            if (_configured) return;
            _configured = true;
            // 以下设置只影响观感，任何一项失败都不应阻止程序启动
            Try(() => FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("zh-CN"))));
            Try(() => TextElement.FontFamilyProperty.OverrideMetadata(typeof(Window),
                new FrameworkPropertyMetadata(AppFont, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.Inherits)));
            Try(() => TextOptions.TextFormattingModeProperty.OverrideMetadata(typeof(Window),
                new FrameworkPropertyMetadata(TextFormattingMode.Display, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits)));
            Try(() => ConfigHelper.Instance.SetLang("zh-cn"));
        }

        private static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warn("界面初始化设置未生效：" + ex.Message);
            }
        }

        public static Window ActiveWindow =>
            Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        public static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }

        /// <summary>
        /// 显示消息框。指定当前活动窗口为父窗口，保证在置顶（防作弊全屏）的窗口上方显示。
        /// </summary>
        /// <summary>无人值守（冒烟测试）模式：消息框不弹出，直接记录并视为“确定”。</summary>
        public static bool Headless { get; set; }

        private static MessageBoxResult Show(string message, string title, MessageBoxButton button, MessageBoxImage icon)
        {
            if (Headless)
            {
                SmokeKit.Step($"[dialog:{icon}] {title}: {message.Replace("\n", " ")}");
                if (icon == MessageBoxImage.Error) SmokeKit.Error("error dialog shown: " + message);
                return MessageBoxResult.OK;
            }
            var owner = ActiveWindow;
            return owner != null && owner.IsVisible
                ? HcMessageBox.Show(owner, message, title, button, icon, MessageBoxResult.None)
                : HcMessageBox.Show(message, title, button, icon, MessageBoxResult.None);
        }

        public static void Error(string message, string title = "出错了")
        {
            OnUi(() => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));
        }

        public static void Info(string message, string title = "提示")
        {
            OnUi(() => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        }

        public static bool Confirm(string message, string title = "请确认")
        {
            return Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        public static bool ConfirmDanger(string message, string title = "请确认")
        {
            return Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        public static void Toast(string message) => OnUi(() => SafeGrowl(() => Growl.Success(message)));

        public static void ToastInfo(string message) => OnUi(() => SafeGrowl(() => Growl.Info(message)));

        public static void ToastWarning(string message) => OnUi(() => SafeGrowl(() => Growl.Warning(message)));

        public static void ToastError(string message) => OnUi(() => SafeGrowl(() => Growl.Error(message)));

        private static void SafeGrowl(Action show)
        {
            try
            {
                show();
            }
            catch (Exception)
            {
                // 没有注册 Growl 容器时忽略
            }
        }

        /// <summary>以当前活动窗口为父窗口显示对话框。</summary>
        public static bool? ShowDialog(Window dialog)
        {
            var owner = ActiveWindow;
            if (owner != null && owner != dialog)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return dialog.ShowDialog();
        }
    }
}
