using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MdQuiz.Core;

namespace MdQuiz.Setup
{
    /// <summary>安装位置的整理与检查（界面和静默安装共用）。</summary>
    internal static class InstallPath
    {
        /// <summary>除程序文件外预留的空间（数据库、日志等）。</summary>
        private const long ReserveBytes = 20L * 1024 * 1024;

        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars().Where(c => c != '\\').ToArray();

        /// <summary>去掉首尾空格、引号和末尾的反斜杠，把 / 换成 \。</summary>
        public static string Normalize(string input)
        {
            var dir = (input ?? "").Trim().Trim('"').Trim().Replace('/', '\\');
            while (dir.Length > 3 && dir.EndsWith("\\", StringComparison.Ordinal)) dir = dir.Substring(0, dir.Length - 1);
            if (dir.Length == 2 && dir[1] == ':') dir += "\\";
            return dir;
        }

        /// <summary>在“选择文件夹”中选了 D:\ 或 D:\Apps 时，自动补上“明点知识考核系统\教师端”。</summary>
        public static string FromPickedFolder(string picked)
        {
            var dir = Normalize(picked);
            var name = Path.GetFileName(dir.TrimEnd('\\'));
            if (string.Equals(name, Product.Current.Edition, StringComparison.OrdinalIgnoreCase)) return dir;
            if (string.Equals(name, Brand.ProductName, StringComparison.OrdinalIgnoreCase)) return Path.Combine(dir, Product.Current.Edition);
            return Path.Combine(dir, Brand.ProductName, Product.Current.Edition);
        }

        /// <summary>检查安装位置。返回错误提示，没有问题时返回 null；spaceInfo 为“需要 … 可用 …”。</summary>
        public static string Validate(string dir, long requiredBytes, out string spaceInfo)
        {
            spaceInfo = null;
            if (string.IsNullOrWhiteSpace(dir)) return "请选择安装位置";
            if (!Regex.IsMatch(dir, @"^[A-Za-z]:\\")) return @"请填写完整的本机路径，例如 D:\" + Brand.ProductName + @"\" + Product.Current.Edition;
            var rest = dir.Substring(3);
            if (rest.Length == 0) return "不能直接安装到磁盘根目录，请选择或新建一个文件夹";
            if (rest.IndexOfAny(InvalidChars) >= 0) return "路径中不能包含这些字符：/ : * ? \" < > |";
            if (rest.Split('\\').Any(s => s.Length == 0 || s.Trim() != s || s.EndsWith(".", StringComparison.Ordinal)))
                return "路径中的文件夹名不能为空，也不能以空格或句点开头、结尾";
            if (dir.Length > 160) return "路径太长，请选择短一些的位置";
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsSameOrUnder(dir, windows)) return "不能安装到 Windows 系统目录";

            DriveInfo drive;
            try
            {
                drive = new DriveInfo(dir.Substring(0, 1));
                if (!drive.IsReady) return $"找不到 {DriveName(dir)}，或磁盘没有就绪";
            }
            catch (Exception)
            {
                return $"找不到 {DriveName(dir)}";
            }
            if (drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.Network) return "请安装到本机硬盘上";
            if (File.Exists(dir)) return "这个位置是一个文件，请选择文件夹";

            var free = drive.AvailableFreeSpace;
            spaceInfo = $"需要 {FormatSize(requiredBytes)}，{DriveName(dir)}可用 {FormatSize(free)}";
            if (free < requiredBytes + ReserveBytes) return $"{DriveName(dir)}空间不足：至少需要 {FormatSize(requiredBytes + ReserveBytes)}，可用 {FormatSize(free)}";
            return null;
        }

        public static bool AreSame(string a, string b) =>
            string.Equals(Normalize(a).TrimEnd('\\'), Normalize(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
            return $"{Math.Max(1, bytes / 1024)} KB";
        }

        private static string DriveName(string dir) => char.ToUpperInvariant(dir[0]) + " 盘";

        private static bool IsSameOrUnder(string dir, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            root = root.TrimEnd('\\');
            return dir.Equals(root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
