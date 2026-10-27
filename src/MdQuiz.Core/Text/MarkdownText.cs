using System.Text;
using System.Text.RegularExpressions;

namespace MdQuiz.Core
{
    /// <summary>填空题空位标记：题干中三个及以上连续下划线表示一个空。</summary>
    public static class BlankMarker
    {
        public static readonly Regex Pattern = new Regex("_{3,}", RegexOptions.Compiled);

        /// <summary>渲染前用私有区字符替换空位，避免下划线被 Markdown 解析成强调。</summary>
        public const char PlaceholderStart = '';
        public const char PlaceholderEnd = '';
        public static readonly Regex PlaceholderPattern = new Regex("(\\d+)", RegexOptions.Compiled);

        public static int Count(string stem) => string.IsNullOrEmpty(stem) ? 0 : Pattern.Matches(stem).Count;

        /// <summary>把空位替换为带序号的占位符（序号从 1 开始）。</summary>
        public static string ToPlaceholders(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return stem ?? "";
            var n = 0;
            return Pattern.Replace(stem, _ => PlaceholderStart + (++n).ToString() + PlaceholderEnd);
        }
    }

    public static class MarkdownText
    {
        private static readonly Regex Image = new Regex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Fence = new Regex(@"^\s*(```|~~~)", RegexOptions.Compiled);
        private static readonly Regex Decoration = new Regex(@"(\*\*|__|\*|`|^#+\s*|^>\s*|\$\$?)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>把 Markdown 粗略转成单行纯文本摘要。</summary>
        public static string ToPlainSummary(string markdown, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(markdown)) return "";
            var sb = new StringBuilder();
            var inFence = false;
            foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                if (Fence.IsMatch(raw))
                {
                    if (!inFence) sb.Append(" [代码] ");
                    inFence = !inFence;
                    continue;
                }
                if (inFence) continue;
                var line = Image.Replace(raw, " [图片] ");
                line = Link.Replace(line, "$1");
                line = BlankMarker.Pattern.Replace(line, "");
                line = Decoration.Replace(line, "");
                sb.Append(line).Append(' ');
                if (sb.Length > maxLength * 2) break;
            }
            var text = Spaces.Replace(sb.ToString(), " ").Trim().Replace("", "____");
            return text.Length > maxLength ? text.Substring(0, maxLength) + "…" : text;
        }
    }
}
