using System;
using System.IO;
using System.Text;

namespace MdQuiz.Wpf
{
    /// <summary>
    /// 数据目录：优先使用程序所在目录下的 data 文件夹（便于整体拷贝、放在不受还原卡影响的 D 盘），
    /// 不可写时退回到 %LOCALAPPDATA%\MdQuiz\{应用名}。
    /// </summary>
    public static class AppPaths
    {
        public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory;

        public static string ResolveDataDir(string appName)
        {
            var portable = Path.Combine(ExeDir, "data");
            if (IsWritable(portable)) return portable;
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdQuiz", appName);
            Directory.CreateDirectory(local);
            return local;
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>简单的按天滚动文本日志。</summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _dir;

        public static void Init(string dataDir)
        {
            _dir = Path.Combine(dataDir, "logs");
            try
            {
                Directory.CreateDirectory(_dir);
            }
            catch (Exception)
            {
                _dir = null;
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            if (_dir == null) return;
            try
            {
                lock (Sync)
                {
                    var file = Path.Combine(_dir, DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // 日志失败不影响程序
            }
        }
    }
}
