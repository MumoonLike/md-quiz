using System.Windows;
using System.Windows.Controls;

namespace MdQuiz.Wpf.Controls
{
    /// <summary>
    /// 答题选项卡片。选中状态只由 <see cref="IsSelected"/>（单向绑定到 ViewModel）决定，点击时执行 Command，
    /// 单选 / 多选的切换逻辑全部在 ViewModel 中完成，不依赖 RadioButton 分组。
    /// </summary>
    public class ChoiceButton : Button
    {
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(ChoiceButton), new PropertyMetadata(false));

        /// <summary>多选题：选项字母显示为方形（复选）样式。</summary>
        public static readonly DependencyProperty IsMultipleProperty = DependencyProperty.Register(
            nameof(IsMultiple), typeof(bool), typeof(ChoiceButton), new PropertyMetadata(false));

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(ChoiceButton), new PropertyMetadata(""));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public bool IsMultiple
        {
            get => (bool)GetValue(IsMultipleProperty);
            set => SetValue(IsMultipleProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }
    }
}
