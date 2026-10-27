using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MdQuiz.Wpf.Controls
{
    /// <summary>小标签：浅色背景 + 同色文字，用于题型、状态等。</summary>
    public class Chip : Control
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(Chip), new PropertyMetadata(""));

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(Color), typeof(Chip), new PropertyMetadata(Colors.Gray, OnColorChanged));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(Chip), new PropertyMetadata(null));

        static Chip()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Chip), new FrameworkPropertyMetadata(typeof(Chip)));
        }

        public Chip()
        {
            UpdateBrushes();
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        /// <summary>背景色（由 Color 自动生成）。</summary>
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            private set => SetValue(FillProperty, value);
        }

        private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Chip)d).UpdateBrushes();

        private void UpdateBrushes()
        {
            var c = Color;
            var fg = new SolidColorBrush(c);
            fg.Freeze();
            var bg = new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B));
            bg.Freeze();
            Foreground = fg;
            Fill = bg;
        }
    }
}
