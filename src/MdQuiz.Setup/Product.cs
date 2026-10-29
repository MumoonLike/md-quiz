using System;
using System.IO;
using MdQuiz.Core;

namespace MdQuiz.Setup
{
    /// <summary>安装的是哪个程序：编译时由 SetupProduct（Teacher / Student）决定。</summary>
    internal sealed class Product
    {
#if SETUP_STUDENT
        public static readonly Product Current = new Product
        {
            Key = "Student",
            Edition = "学生端",
            ExeName = "MdQuiz.Student.exe",
            Tagline = "局域网考试 · 答题客户端",
            OffersFirewall = false,
            DataDescription = "作答缓存、连接设置和日志",
            KeptDataNotice = "作答缓存和连接设置会保留。",
            Features = new[]
            {
                new Feature("Icon.LanConnect", "自动连接", "局域网内找到教师机"),
                new Feature("Icon.CloudCheckOutline", "实时保存", "断线也不丢答案"),
                new Feature("Icon.ClockOutline", "限时作答", "时间到自动交卷"),
            },
            Tips = new[]
            {
                "打开后自动在局域网中寻找教师机，输入学号、核对姓名即可登录",
                "每改一题就自动保存到教师机，同时写入本机缓存，断线自动重连",
                "左侧答题卡可以跳题、标记不确定的题目，时间到会自动交卷",
                "登录后学号与这台电脑绑定，电脑重启后打开程序即可继续答题",
            },
        };
#else
        public static readonly Product Current = new Product
        {
            Key = "Teacher",
            Edition = "教师端",
            ExeName = "MdQuiz.Teacher.exe",
            Tagline = "局域网考试 · 题库组卷 · 监考阅卷",
            OffersFirewall = true,
            DataDescription = "题库、试卷、学生名单、考试记录和成绩",
            KeptDataNotice = "题库、成绩等数据会保留。",
            Features = new[]
            {
                new Feature("Icon.LanguageMarkdownOutline", "题库组卷", "Markdown / Excel 导入"),
                new Feature("Icon.MonitorEye", "实时监考", "进度、断线、切屏"),
                new Feature("Icon.ChartBoxOutline", "成绩分析", "批改改分、导出 Excel"),
            },
            Tips = new[]
            {
                "题库支持 Markdown、Excel 批量导入，图片、公式、代码和表格都能显示",
                "组卷可以从题库手动挑题，也可以按题型和标签随机抽题",
                "监考时实时查看每名考生的登录、答题进度、断线和切屏情况",
                "考试中可以随时加减时间，学生端倒计时同步更新",
                "成绩统计、按题批改改分、导出 Excel 成绩表和答题明细一站完成",
            },
        };
#endif

        public string Key { get; private set; }

        /// <summary>“教师端” / “学生端”。</summary>
        public string Edition { get; private set; }

        public string ExeName { get; private set; }
        public string Tagline { get; private set; }

        /// <summary>是否提供“添加防火墙例外”（学生机只主动连接教师机，不需要放行）。</summary>
        public bool OffersFirewall { get; private set; }

        /// <summary>卸载时“同时删除数据”说明中列出的内容。</summary>
        public string DataDescription { get; private set; }

        /// <summary>升级提示中“数据会保留”的说法。</summary>
        public string KeptDataNotice { get; private set; }

        public Feature[] Features { get; private set; }

        /// <summary>安装过程中轮流显示的功能介绍。</summary>
        public string[] Tips { get; private set; }

        /// <summary>“明点智慧考评系统 教师端”，用于快捷方式、控制面板“程序和功能”。</summary>
        public string DisplayName => Brand.ProductName + " " + Edition;

        public string ProcessName => Path.GetFileNameWithoutExtension(ExeName);

        public string UninstallRegistryKey => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MdQuiz." + Key;

        /// <summary>默认安装位置：C:\Program Files\明点智慧考评系统\教师端。</summary>
        public string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Brand.ProductName, Edition);

        /// <summary>本安装程序的版本（三段，与注册表中的 DisplayVersion 比较）。</summary>
        public static Version Version
        {
            get
            {
                var v = typeof(Product).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string VersionText => FormatVersion(Version);

        /// <summary>显示为 1.0（有修订号时为 1.0.1）。</summary>
        public static string FormatVersion(Version v) => Brand.FormatVersion(v);
    }

    internal sealed class Feature
    {
        public Feature(string icon, string title, string detail)
        {
            Icon = icon;
            Title = title;
            Detail = detail;
        }

        /// <summary>Themes/Icons.xaml 中的图标键。</summary>
        public string Icon { get; }

        public string Title { get; }
        public string Detail { get; }
    }
}
