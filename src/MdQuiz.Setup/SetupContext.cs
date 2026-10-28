namespace MdQuiz.Setup
{
    /// <summary>界面需要的信息：安装（含升级）或卸载。</summary>
    internal sealed class SetupContext
    {
        private SetupContext()
        {
        }

        public bool IsUninstall { get; private set; }

        /// <summary>安装：嵌入的程序文件。</summary>
        public Payload Payload { get; private set; }

        /// <summary>安装：已安装的旧版本（没有时为 null）。</summary>
        public ExistingInstall Existing { get; private set; }

        /// <summary>卸载：安装目录。</summary>
        public string UninstallDir { get; private set; }

        /// <summary>界面截图测试：只展示界面，不执行安装。</summary>
        public bool IsSmoke { get; private set; }

        public static SetupContext ForInstall(Payload payload, ExistingInstall existing, bool smoke = false) =>
            new SetupContext { Payload = payload, Existing = existing, IsSmoke = smoke };

        public static SetupContext ForUninstall(string dir, bool smoke = false) =>
            new SetupContext { IsUninstall = true, UninstallDir = dir, IsSmoke = smoke };
    }
}
