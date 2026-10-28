using System;
using System.IO;
using MdQuiz.Core.Net;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
using MdQuiz.Server.Mcp;
using MdQuiz.Server.Services;
using MdQuiz.Wpf;
using MdQuiz.Wpf.Controls;

namespace MdQuiz.Teacher.Services
{
    /// <summary>教师端全局服务（数据库、仓储、考试服务）。</summary>
    public sealed class AppServices : IDisposable
    {
        private AppServices(string dataDir)
        {
            DataDir = dataDir;
            Db = new Database(Path.Combine(dataDir, "mdquiz.db"));
            Db.Initialize();
            Settings = new SettingsRepository(Db);
            Questions = new QuestionRepository(Db);
            Resources = new ResourceRepository(Db);
            Papers = new PaperRepository(Db);
            Roster = new RosterRepository(Db);
            Exams = new ExamRepository(Db);
            Results = new ResultService(Exams);
            PaperService = new PaperService(Questions, Exams);
            Importer = new QuestionImporter(Resources);
            Host = new ExamHost(Db) { ServerName = ServerName };
            Mcp = new McpServer(new McpToolset(Questions, Papers, Roster, Exams, Results, PaperService, Host));
            MarkdownView.ResourceResolver = hash => Resources.Get(hash);
        }

        public static AppServices Current { get; private set; }

        public string DataDir { get; }
        public Database Db { get; }
        public SettingsRepository Settings { get; }
        public QuestionRepository Questions { get; }
        public ResourceRepository Resources { get; }
        public PaperRepository Papers { get; }
        public RosterRepository Roster { get; }
        public ExamRepository Exams { get; }
        public ResultService Results { get; }
        public PaperService PaperService { get; }
        public QuestionImporter Importer { get; }
        public ExamHost Host { get; }
        public McpServer Mcp { get; }

        public string ServerName
        {
            get => Settings.Get(SettingsRepository.KeyServerName, Environment.MachineName);
            set => Settings.Set(SettingsRepository.KeyServerName, value);
        }

        public int ServerPort
        {
            get => Settings.GetInt(SettingsRepository.KeyServerPort, Discovery.DefaultTcpPort);
            set => Settings.Set(SettingsRepository.KeyServerPort, value.ToString());
        }

        // ---------------- MCP 服务 ----------------

        public bool McpEnabled
        {
            get => Settings.GetBool(SettingsRepository.KeyMcpEnabled, false);
            set => Settings.SetBool(SettingsRepository.KeyMcpEnabled, value);
        }

        public McpOptions LoadMcpOptions()
        {
            return new McpOptions
            {
                Port = Settings.GetInt(SettingsRepository.KeyMcpPort, McpOptions.DefaultPort),
                AllowLan = Settings.GetBool(SettingsRepository.KeyMcpAllowLan, false),
                Token = Settings.Get(SettingsRepository.KeyMcpToken),
                Permissions = (McpPermissions)Settings.GetInt(SettingsRepository.KeyMcpPermissions, (int)McpPermissions.Read),
            };
        }

        public void SaveMcpOptions(McpOptions options)
        {
            Settings.Set(SettingsRepository.KeyMcpPort, options.Port.ToString());
            Settings.SetBool(SettingsRepository.KeyMcpAllowLan, options.AllowLan);
            Settings.Set(SettingsRepository.KeyMcpToken, options.Token ?? "");
            Settings.Set(SettingsRepository.KeyMcpPermissions, ((int)options.Permissions).ToString());
        }

        /// <summary>按设置启动或停止 MCP 服务，失败时返回错误信息。</summary>
        public string ApplyMcp()
        {
            if (!McpEnabled)
            {
                Mcp.Stop();
                return null;
            }
            var options = LoadMcpOptions();
            try
            {
                if (options.AllowLan && string.IsNullOrWhiteSpace(options.Token))
                {
                    options.Token = McpOptions.NewToken();
                    SaveMcpOptions(options);
                }
                Mcp.Start(options);
                Log.Info($"MCP 服务已启动：{(options.AllowLan ? "局域网" : "仅本机")}，端口 {Mcp.Port}，权限 {options.Permissions}");
                return null;
            }
            catch (Exception ex)
            {
                Mcp.Stop();
                Log.Error("启动 MCP 服务失败", ex);
                return $"MCP 服务启动失败：端口 {options.Port} 可能已被占用（{ex.Message}）。请在“设置 → MCP 服务”中更换端口。";
            }
        }

        public static AppServices Create(string dataDirOverride = null)
        {
            var dir = dataDirOverride ?? AppPaths.ResolveDataDir("Teacher");
            Log.Init(dir);
            Current = new AppServices(dir);
            return Current;
        }

        /// <summary>启动考试服务，失败时返回错误信息。</summary>
        public string StartHost()
        {
            try
            {
                Host.ServerName = ServerName;
                Host.Start(ServerPort);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动考试服务失败", ex);
                return $"考试服务启动失败：端口 {ServerPort} 可能已被占用（{ex.Message}）。请在“设置”中更换端口，或关闭重复打开的教师端。";
            }
        }

        public void Dispose()
        {
            Mcp.Stop();
            Host.Stop();
        }
    }
}
