namespace MdQuiz.Core
{
    /// <summary>产品名称（界面显示用；程序集、目录名仍沿用 MdQuiz）。</summary>
    public static class Brand
    {
        public const string ProductName = "明点智慧考评系统";
        public const string TeacherTitle = ProductName + " · 教师端";
        public const string StudentTitle = ProductName + " · 学生端";

        /// <summary>界面上显示的版本号：没有修订号时显示两段（1.0），否则三段（1.0.1）。</summary>
        public static string FormatVersion(System.Version v) => v == null ? "" : v.ToString(v.Build > 0 ? 3 : 2);
    }
}
