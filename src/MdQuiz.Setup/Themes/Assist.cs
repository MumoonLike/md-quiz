using System.Windows;

namespace MdQuiz.Setup.Themes
{
    /// <summary>控件模板用到的附加属性：圆角、错误状态、阴影。</summary>
    public static class Assist
    {
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Assist), new FrameworkPropertyMetadata(new CornerRadius(8)));

        public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

        public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

        public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
            "HasError", typeof(bool), typeof(Assist), new FrameworkPropertyMetadata(false));

        public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);

        public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);

        /// <summary>实心按钮下方显示同色阴影。</summary>
        public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
            "Shadow", typeof(bool), typeof(Assist), new FrameworkPropertyMetadata(false));

        public static bool GetShadow(DependencyObject d) => (bool)d.GetValue(ShadowProperty);

        public static void SetShadow(DependencyObject d, bool value) => d.SetValue(ShadowProperty, value);
    }
}
