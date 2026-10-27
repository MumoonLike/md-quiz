using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MdQuiz.Core.Markdown;
using MdQuiz.Server.Data;

namespace MdQuiz.Server.Services
{
    /// <summary>从 Markdown 文件导入题目：解析题目，并把引用的本地图片存入数据库。</summary>
    public sealed class QuestionImporter
    {
        private static readonly Regex ImageLink = new Regex(@"!\[(?<alt>[^\]]*)\]\(\s*<?(?<url>[^)>\s]+)>?(?<title>\s+""[^""]*"")?\s*\)", RegexOptions.Compiled);
        private readonly ResourceRepository _resources;

        public QuestionImporter(ResourceRepository resources)
        {
            _resources = resources;
        }

        public ParseResult ImportMarkdownFile(string path)
        {
            var text = TextFiles.ReadAllText(path);
            var result = QuestionMarkdown.Parse(text);
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            foreach (var pq in result.Questions)
            {
                var q = pq.Question;
                q.Stem = EmbedImages(q.Stem, baseDir, pq.Line, result.Warnings);
                q.Options = q.Options.Select(o => EmbedImages(o, baseDir, pq.Line, result.Warnings)).ToList();
                q.Analysis = EmbedImages(q.Analysis, baseDir, pq.Line, result.Warnings);
                q.Answer = EmbedImages(q.Answer, baseDir, pq.Line, result.Warnings);
            }
            return result;
        }

        /// <summary>把 ![](相对路径) 中的本地图片存入数据库，链接改为 res:哈希。</summary>
        public string EmbedImages(string markdown, string baseDir, int line, List<string> warnings)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown ?? "";
            return ImageLink.Replace(markdown, m =>
            {
                var url = m.Groups["url"].Value;
                if (url.StartsWith("res:", StringComparison.OrdinalIgnoreCase) || url.Contains("://") || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                string file;
                try
                {
                    file = Path.IsPathRooted(url) ? url : Path.Combine(baseDir, Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar));
                }
                catch (Exception)
                {
                    warnings?.Add($"第 {line} 行的题目：图片路径“{url}”无效");
                    return m.Value;
                }
                if (!File.Exists(file))
                {
                    warnings?.Add($"第 {line} 行的题目：找不到图片 {url}");
                    return m.Value;
                }
                var hash = _resources.Save(File.ReadAllBytes(file), ResourceRepository.MimeFromExtension(Path.GetExtension(file)));
                return $"![{m.Groups["alt"].Value}](res:{hash})";
            });
        }
    }

    public static class TextFiles
    {
        /// <summary>读取文本文件：有 BOM 按 BOM，合法 UTF-8 按 UTF-8，否则按 GBK（Windows 中文默认编码）。</summary>
        public static string ReadAllText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        public static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                var gbk = Gbk();
                return gbk != null ? gbk.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            }
        }

        public static Encoding Gbk()
        {
            try
            {
                return Encoding.GetEncoding(936);
            }
            catch (Exception)
            {
                return null; // .NET Core 未注册代码页时不可用
            }
        }
    }
}
