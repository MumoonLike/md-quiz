using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace MdQuiz.Setup
{
    /// <summary>嵌入在安装程序中的程序文件（payload.zip）。</summary>
    internal sealed class Payload : IDisposable
    {
        private readonly ZipArchive _zip;

        private Payload(ZipArchive zip)
        {
            _zip = zip;
            Files = zip.Entries
                .Where(e => !IsDirectory(e))
                .Select(e => new PayloadFile(e, NormalizePath(e.FullName)))
                .ToList();
            TotalBytes = Files.Sum(f => f.Entry.Length);
        }

        /// <summary>相对于安装目录的路径（反斜杠分隔）。</summary>
        public IReadOnlyList<PayloadFile> Files { get; }

        public long TotalBytes { get; }

        /// <summary>读取嵌入的 payload.zip；没有嵌入（开发时未先发布程序）时返回 null。</summary>
        public static Payload Open()
        {
            var stream = typeof(Payload).Assembly.GetManifestResourceStream("payload.zip");
            return stream == null ? null : new Payload(new ZipArchive(stream, ZipArchiveMode.Read));
        }

        public bool Contains(string relativePath) => Files.Any(f => string.Equals(f.Path, relativePath, StringComparison.OrdinalIgnoreCase));

        /// <summary>解压到目标目录，覆盖已有文件。onProgress(已完成字节数, 当前文件)。</summary>
        public void ExtractTo(string targetDir, Action<long, string> onProgress)
        {
            var root = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
            long done = 0;
            foreach (var file in Files)
            {
                var dest = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包中的文件路径无效：" + file.Path);
                onProgress(done, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                WriteWithRetry(file, dest);
                done += file.Entry.Length;
            }
            onProgress(done, null);
        }

        private static void WriteWithRetry(PayloadFile file, string dest)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    using (var input = file.Entry.Open())
                    using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    File.SetLastWriteTime(dest, file.Entry.LastWriteTime.DateTime);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    // 杀毒软件扫描、刚关闭的程序还没释放文件时，稍等再试
                    SetupLog.Warn($"写入 {file.Path} 失败（第 {attempt} 次）：{ex.Message}");
                    Thread.Sleep(400 * attempt);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new SetupException($"无法写入文件 {file.Path}：{ex.Message}\n\n请确认程序已经关闭，没有被杀毒软件拦截，然后重试。", ex);
                }
            }
        }

        private static bool IsDirectory(ZipArchiveEntry e) => e.FullName.EndsWith("/", StringComparison.Ordinal) || e.FullName.EndsWith("\\", StringComparison.Ordinal);

        private static string NormalizePath(string name) => name.Replace('/', '\\').TrimStart('\\');

        public void Dispose() => _zip.Dispose();
    }

    internal sealed class PayloadFile
    {
        public PayloadFile(ZipArchiveEntry entry, string path)
        {
            Entry = entry;
            Path = path;
        }

        public ZipArchiveEntry Entry { get; }
        public string Path { get; }
    }

    /// <summary>可以直接展示给用户的安装错误。</summary>
    internal sealed class SetupException : Exception
    {
        public SetupException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }
}
