using System;
using System.IO;
using MdQuiz.Core.Net;
using MdQuiz.Server.Data;
using MdQuiz.Server.Hosting;
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
            Host.Stop();
        }
    }
}
