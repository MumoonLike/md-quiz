using System;
using System.IO;
using System.Text;

namespace MdQuiz.Setup
{
    /// <summary>安装日志：默认写到 %TEMP%\MdQuiz-产品-Setup.log（卸载为 -Uninstall.log），每次运行覆盖。</summary>
    internal static class SetupLog
    {
        private static readonly object Sync = new object();
        private static StreamWriter _writer;

        public static string FilePath { get; private set; }

        public static void Open(SetupOptions options)
        {
            var name = $"MdQuiz-{Product.Current.Key}-{(options.Uninstall ? "Uninstall" : "Setup")}.log";
            FilePath = string.IsNullOrEmpty(options.LogFile) ? Path.Combine(Path.GetTempPath(), name) : Path.GetFullPath(options.LogFile);
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception)
            {
                _writer = null; // 日志失败不影响安装
            }
            Info($"{Product.Current.DisplayName} {(options.Uninstall ? "卸载" : "安装")}程序 v{Product.VersionText}，" +
                 $"系统 {Environment.OSVersion.VersionString}，{(Environment.Is64BitOperatingSystem ? "64" : "32")} 位，CLR {Environment.Version}");
            Info("命令行：" + Environment.CommandLine);
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }
    }
}
