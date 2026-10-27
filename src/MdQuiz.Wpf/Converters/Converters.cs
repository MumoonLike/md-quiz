using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MdQuiz.Core.Models;

namespace MdQuiz.Wpf.Converters
{
    /// <summary>true → Visible。参数 "invert" 反转。</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            if (Invert(parameter)) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is Visibility v && v == Visibility.Visible;
            return Invert(parameter) ? !b : b;
        }

        internal static bool Invert(object parameter) => string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    /// <summary>非空（null、空字符串、空集合、0 之外）→ Visible。参数 "invert" 反转。</summary>
    public sealed class HasValueToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool has;
            switch (value)
            {
                case null: has = false; break;
                case string s: has = s.Length > 0; break;
                case int i: has = i != 0; break;
                case long l: has = l != 0; break;
                case double d: has = Math.Abs(d) > double.Epsilon; break;
                case ICollection c: has = c.Count > 0; break;
                default: has = true; break;
            }
            if (BoolToVisibilityConverter.Invert(parameter)) has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>枚举值与参数（枚举名）相等 → true；用于 RadioButton 绑定枚举。</summary>
    public sealed class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is bool b) || !b || parameter == null) return Binding.DoNothing;
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return Enum.Parse(type, parameter.ToString(), true);
        }
    }

    public sealed class QuestionTypeNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is QuestionType t)) return "";
            return string.Equals(parameter as string, "short", StringComparison.OrdinalIgnoreCase) ? QuestionTypes.ShortName(t) : QuestionTypes.DisplayName(t);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>题型 → 标识色（Color）。</summary>
    public sealed class QuestionTypeColorConverter : IValueConverter
    {
        public static Color ColorOf(QuestionType t)
        {
            switch (t)
            {
                case QuestionType.Single: return Palette.Blue;
                case QuestionType.Multiple: return Palette.Violet;
                case QuestionType.TrueFalse: return Palette.Sky;
                case QuestionType.FillBlank: return Palette.Amber;
                case QuestionType.ShortAnswer: return Palette.Emerald;
                default: return Palette.Gray;
            }
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is QuestionType t ? ColorOf(t) : Palette.Gray;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class ExamStatusTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ExamStatus s ? ExamTexts.StatusName(s) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class ExamStatusColorConverter : IValueConverter
    {
        public static Color ColorOf(ExamStatus s)
        {
            switch (s)
            {
                case ExamStatus.Open: return Palette.Sky;
                case ExamStatus.Running: return Palette.Blue;
                case ExamStatus.Ended: return Palette.Emerald;
                default: return Palette.Gray;
            }
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ExamStatus s ? ColorOf(s) : Palette.Gray;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>UTC 时间 → 本地时间字符串；参数为格式（默认 MM-dd HH:mm）。</summary>
    public sealed class LocalTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            DateTime? dt = value as DateTime?;
            if (dt == null) return "";
            var format = parameter as string ?? "MM-dd HH:mm";
            return dt.Value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>分数显示：null → “—”，其余最多两位小数。</summary>
    public sealed class ScoreConverter : IValueConverter
    {
        public static string Format(double? score) => score.HasValue ? score.Value.ToString("0.##", CultureInfo.InvariantCulture) : "—";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case double d: return Format(d);
                case float f: return Format(f);
                case int i: return Format(i);
                default: return "—";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Color → SolidColorBrush；参数为不透明度（0~1），用于浅色背景。</summary>
    public sealed class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is Color c)) return Brushes.Transparent;
            if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
                c = Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>数值相除得到 0~1 的进度，用于进度条宽度等：values[0] / values[1]。</summary>
    public sealed class RatioConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return 0d;
            var a = ToDouble(values[0]);
            var b = ToDouble(values[1]);
            return b <= 0 ? 0d : Math.Max(0, Math.Min(1, a / b)) * 100;
        }

        private static double ToDouble(object o)
        {
            try
            {
                return o == null || o == DependencyProperty.UnsetValue ? 0 : System.Convert.ToDouble(o, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    /// <summary>统一配色（与 Theme.xaml 保持一致）。</summary>
    public static class Palette
    {
        public static readonly Color Blue = Color.FromRgb(0x4F, 0x6B, 0xED);
        public static readonly Color Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
        public static readonly Color Sky = Color.FromRgb(0x0E, 0xA5, 0xE9);
        public static readonly Color Amber = Color.FromRgb(0xF5, 0x9E, 0x0B);
        public static readonly Color Emerald = Color.FromRgb(0x10, 0xB9, 0x81);
        public static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);
        public static readonly Color Gray = Color.FromRgb(0x9C, 0xA3, 0xAF);
        public static readonly Color Green = Color.FromRgb(0x22, 0xA0, 0x6B);
    }
}
