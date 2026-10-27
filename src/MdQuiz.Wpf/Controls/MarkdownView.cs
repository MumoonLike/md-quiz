using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdQuiz.Core;
using MdQuiz.Core.Exams;
using WpfMath.Controls;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfInline = System.Windows.Documents.Inline;

namespace MdQuiz.Wpf.Controls
{
    /// <summary>
    /// 把 Markdown 渲染成 WPF 元素：段落、加粗/斜体、行内代码、代码块、列表、表格、
    /// 引用、图片（res:哈希）、公式（$…$ / $$…$$），以及填空题空位（___）。
    /// </summary>
    public class MarkdownView : ContentControl
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, e) => ((MarkdownView)d).Render()));

        /// <summary>填空题空位显示为带序号的下划线“（1）”。</summary>
        public static readonly DependencyProperty NumberBlanksProperty = DependencyProperty.Register(
            nameof(NumberBlanks), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, e) => ((MarkdownView)d).Render()));

        /// <summary>段落之间的间距。</summary>
        public static readonly DependencyProperty BlockSpacingProperty = DependencyProperty.Register(
            nameof(BlockSpacing), typeof(double), typeof(MarkdownView), new PropertyMetadata(8d, (d, e) => ((MarkdownView)d).Render()));

        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .UseMathematics()
            .UseTaskLists()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        private static readonly Dictionary<string, BitmapSource> ImageCache = new Dictionary<string, BitmapSource>();
        private static readonly FontFamily MonoFont = new FontFamily("Consolas, Courier New, Microsoft YaHei");
        private static readonly Brush CodeBackground = Frozen(Color.FromRgb(0xF3, 0xF5, 0xF9));
        private static readonly Brush CodeBorder = Frozen(Color.FromRgb(0xE3, 0xE7, 0xEF));
        private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush BlankBrush = Frozen(Color.FromRgb(0x4F, 0x6B, 0xED));

        static MarkdownView()
        {
            FocusableProperty.OverrideMetadata(typeof(MarkdownView), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(MarkdownView), new FrameworkPropertyMetadata(false));
        }

        /// <summary>按资源哈希取图片数据。教师端从数据库读取，学生端从试卷附带的资源中读取。</summary>
        public static Func<string, byte[]> ResourceResolver { get; set; }

        public static void ClearImageCache()
        {
            lock (ImageCache) ImageCache.Clear();
        }

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        public bool NumberBlanks
        {
            get => (bool)GetValue(NumberBlanksProperty);
            set => SetValue(NumberBlanksProperty, value);
        }

        public double BlockSpacing
        {
            get => (double)GetValue(BlockSpacingProperty);
            set => SetValue(BlockSpacingProperty, value);
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void Render()
        {
            var text = Markdown ?? "";
            var panel = new StackPanel();
            if (text.Length > 0)
            {
                try
                {
                    var doc = Markdig.Markdown.Parse(BlankMarker.ToPlaceholders(text), Pipeline);
                    AddBlocks(panel, doc);
                }
                catch (Exception)
                {
                    panel.Children.Clear();
                    panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
                }
            }
            Content = panel;
        }

        private void AddBlocks(Panel panel, IEnumerable<MdBlock> blocks)
        {
            var list = blocks.Where(b => !(b is HtmlBlock) && !(b is LinkReferenceDefinitionGroup)).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var element = RenderBlock(list[i]);
                if (element == null) continue;
                if (element is FrameworkElement fe && i < list.Count - 1)
                    fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom + BlockSpacing);
                panel.Children.Add(element);
            }
        }

        private UIElement RenderBlock(MdBlock block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var tb = NewTextBlock();
                    tb.FontWeight = FontWeights.SemiBold;
                    tb.FontSize = FontSize * Math.Max(1.05, 1.5 - 0.1 * heading.Level);
                    AddInlines(tb.Inlines, heading.Inline);
                    return tb;
                }
                case ParagraphBlock paragraph:
                {
                    // 只有一张图片的段落按块级图片显示，方便缩放
                    var only = paragraph.Inline?.FirstChild;
                    if (only is LinkInline link && link.IsImage && only.NextSibling == null)
                        return CreateImage(link.Url, LinkText(link));
                    var tb = NewTextBlock();
                    AddInlines(tb.Inlines, paragraph.Inline);
                    return tb;
                }
                case MathBlock math:
                    return CreateFormula(math.Lines.ToString(), true);
                case CodeBlock code:
                {
                    var tb = new TextBlock
                    {
                        Text = code.Lines.ToString().TrimEnd('\n', '\r'),
                        FontFamily = MonoFont,
                        FontSize = Math.Max(12, FontSize - 1),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    return new Border
                    {
                        Background = CodeBackground,
                        BorderBrush = CodeBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8),
                        Child = tb,
                    };
                }
                case ListBlock listBlock:
                    return RenderList(listBlock);
                case QuoteBlock quote:
                {
                    var inner = new StackPanel();
                    AddBlocks(inner, quote);
                    return new Border
                    {
                        BorderBrush = CodeBorder,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(12, 2, 0, 2),
                        Child = inner,
                    };
                }
                case MdTable table:
                    return RenderTable(table);
                case ThematicBreakBlock _:
                    return new Border { Height = 1, Background = CodeBorder, Margin = new Thickness(0, 4, 0, 4) };
                case ContainerBlock container:
                {
                    var inner = new StackPanel();
                    AddBlocks(inner, container);
                    return inner;
                }
                case LeafBlock leaf when leaf.Inline != null:
                {
                    var tb = NewTextBlock();
                    AddInlines(tb.Inlines, leaf.Inline);
                    return tb;
                }
                default:
                    return null;
            }
        }

        private TextBlock NewTextBlock()
        {
            return new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = Math.Round(FontSize * 1.6), LineStackingStrategy = LineStackingStrategy.MaxHeight };
        }

        private UIElement RenderList(ListBlock list)
        {
            var panel = new StackPanel();
            var number = 1;
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) number = start;
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bullet = new TextBlock
                {
                    Text = list.IsOrdered ? number++ + "." : "•",
                    Margin = new Thickness(4, 0, 8, 0),
                    MinWidth = list.IsOrdered ? 18 : 10,
                    Foreground = MutedBrush,
                    LineHeight = Math.Round(FontSize * 1.6),
                    LineStackingStrategy = LineStackingStrategy.MaxHeight,
                };
                var content = new StackPanel();
                AddBlocks(content, item);
                Grid.SetColumn(content, 1);
                grid.Children.Add(bullet);
                grid.Children.Add(content);
                panel.Children.Add(grid);
            }
            return panel;
        }

        private UIElement RenderTable(MdTable table)
        {
            var grid = new Grid();
            var columns = Math.Max(1, table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max());
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rowIndex = 0;
            foreach (var row in table.OfType<MdTableRow>())
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var col = 0;
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var inner = new StackPanel();
                    AddBlocks(inner, cell);
                    if (row.IsHeader) inner.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
                    var border = new Border
                    {
                        BorderBrush = CodeBorder,
                        BorderThickness = new Thickness(col == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1),
                        Padding = new Thickness(10, 4, 10, 4),
                        Background = row.IsHeader ? CodeBackground : Brushes.Transparent,
                        Child = inner,
                    };
                    Grid.SetRow(border, rowIndex);
                    Grid.SetColumn(border, Math.Min(col, columns - 1));
                    grid.Children.Add(border);
                    col++;
                }
                rowIndex++;
            }
            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }

        private void AddInlines(InlineCollection target, ContainerInline container)
        {
            if (container == null) return;
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        AddText(target, literal.Content.ToString());
                        break;
                    case EmphasisInline emphasis:
                    {
                        Span span;
                        if (emphasis.DelimiterChar == '~') span = new Span { TextDecorations = TextDecorations.Strikethrough };
                        else if (emphasis.DelimiterChar == '=') span = new Span { Background = Frozen(Color.FromRgb(0xFE, 0xF3, 0xC7)) };
                        else if (emphasis.DelimiterCount >= 2) span = new Bold();
                        else span = new Italic();
                        AddInlines(span.Inlines, emphasis);
                        target.Add(span);
                        break;
                    }
                    case CodeInline code:
                        target.Add(new Run(" " + code.Content + " ") { FontFamily = MonoFont, Background = CodeBackground });
                        break;
                    case LineBreakInline _:
                        target.Add(new LineBreak());
                        break;
                    case MathInline math:
                        target.Add(new InlineUIContainer(CreateFormula(math.Content.ToString(), false)) { BaselineAlignment = BaselineAlignment.Center });
                        break;
                    case LinkInline link when link.IsImage:
                        target.Add(new InlineUIContainer(CreateImage(link.Url, LinkText(link))) { BaselineAlignment = BaselineAlignment.Bottom });
                        break;
                    case LinkInline link:
                    {
                        var span = new Span { TextDecorations = TextDecorations.Underline };
                        AddInlines(span.Inlines, link);
                        target.Add(span);
                        break;
                    }
                    case AutolinkInline auto:
                        target.Add(new Run(auto.Url) { TextDecorations = TextDecorations.Underline });
                        break;
                    case HtmlEntityInline entity:
                        target.Add(new Run(entity.Transcoded.ToString()));
                        break;
                    case HtmlInline html:
                        if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) target.Add(new LineBreak());
                        break;
                    case TaskList task:
                        target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                        break;
                    case ContainerInline nested:
                        AddInlines(target, nested);
                        break;
                }
            }
        }

        private void AddText(InlineCollection target, string text)
        {
            var last = 0;
            foreach (System.Text.RegularExpressions.Match m in BlankMarker.PlaceholderPattern.Matches(text))
            {
                if (m.Index > last) target.Add(new Run(text.Substring(last, m.Index - last)));
                target.Add(CreateBlank(m.Groups[1].Value));
                last = m.Index + m.Length;
            }
            if (last < text.Length) target.Add(new Run(text.Substring(last)));
        }

        private WpfInline CreateBlank(string number)
        {
            var label = NumberBlanks ? $" ({number}) " : "   ";
            return new Run(label)
            {
                TextDecorations = TextDecorations.Underline,
                Foreground = BlankBrush,
                FontWeight = FontWeights.SemiBold,
            };
        }

        private static string LinkText(LinkInline link)
        {
            return string.Concat(link.OfType<LiteralInline>().Select(l => l.Content.ToString()));
        }

        private UIElement CreateFormula(string tex, bool display)
        {
            tex = (tex ?? "").Trim();
            try
            {
                var formula = new FormulaControl
                {
                    Formula = tex,
                    Scale = Math.Round(FontSize * (display ? 1.45 : 1.3)),
                    SystemTextFontName = "Microsoft YaHei",
                    Margin = display ? new Thickness(0, 4, 0, 4) : new Thickness(1, 0, 1, 0),
                    HorizontalAlignment = display ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    Focusable = false,
                    IsTabStop = false,
                };
                if (!formula.HasError) return formula;
            }
            catch (Exception)
            {
                // 公式有误时显示原文
            }
            return new TextBlock { Text = display ? "$$" + tex + "$$" : "$" + tex + "$", FontFamily = MonoFont, Foreground = MutedBrush };
        }

        private UIElement CreateImage(string url, string alt)
        {
            var source = LoadImage(url);
            if (source == null)
            {
                return new TextBlock
                {
                    Text = "［图片无法显示" + (string.IsNullOrEmpty(alt) ? "" : "：" + alt) + "］",
                    Foreground = MutedBrush,
                };
            }
            return new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = source.Width,
                MaxHeight = 480,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = string.IsNullOrEmpty(alt) ? null : alt,
            };
        }

        private static BitmapSource LoadImage(string url)
        {
            var hash = ResourceLinks.HashOf(url);
            var key = hash ?? url;
            if (string.IsNullOrEmpty(key)) return null;
            lock (ImageCache)
            {
                if (ImageCache.TryGetValue(key, out var cached)) return cached;
            }
            byte[] data = null;
            try
            {
                if (hash != null) data = ResourceResolver?.Invoke(hash);
                else if (Path.IsPathRooted(url) && File.Exists(url)) data = File.ReadAllBytes(url);
            }
            catch (Exception)
            {
                data = null;
            }
            if (data == null || data.Length == 0) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                lock (ImageCache) ImageCache[key] = bmp;
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
