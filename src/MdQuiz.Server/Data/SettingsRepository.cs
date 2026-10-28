using System;
using System.Globalization;
using System.Security.Cryptography;
using Dapper;

namespace MdQuiz.Server.Data
{
    public sealed class SettingsRepository
    {
        public const string KeyPassword = "admin.password";
        public const string KeyServerName = "server.name";
        public const string KeyServerPort = "server.port";
        public const string KeyMcpEnabled = "mcp.enabled";
        public const string KeyMcpPort = "mcp.port";
        public const string KeyMcpAllowLan = "mcp.lan";
        public const string KeyMcpToken = "mcp.token";
        public const string KeyMcpPermissions = "mcp.permissions";

        private readonly Database _db;

        public SettingsRepository(Database db)
        {
            _db = db;
        }

        public string Get(string key, string fallback = null)
        {
            return _db.Read(c => c.ExecuteScalar<string>("SELECT value FROM settings WHERE key = @key", new { key })) ?? fallback;
        }

        public void Set(string key, string value)
        {
            _db.Write((c, t) => c.Execute(
                "INSERT INTO settings(key, value) VALUES(@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key, value }, t));
        }

        public void Remove(string key)
        {
            _db.Write((c, t) => c.Execute("DELETE FROM settings WHERE key = @key", new { key }, t));
        }

        public int GetInt(string key, int fallback)
        {
            return int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            return bool.TryParse(Get(key), out var v) ? v : fallback;
        }

        public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

        // ---------------- 管理密码 ----------------

        public bool HasPassword => !string.IsNullOrEmpty(Get(KeyPassword));

        public void SetPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("密码不能为空");
            Set(KeyPassword, PasswordHash.Create(password));
        }

        public bool VerifyPassword(string password)
        {
            var stored = Get(KeyPassword);
            return stored != null && PasswordHash.Verify(password ?? "", stored);
        }

        public void ClearPassword() => Remove(KeyPassword);
    }

    /// <summary>PBKDF2-SHA256 密码哈希，格式：pbkdf2$迭代次数$盐$哈希。</summary>
    public static class PasswordHash
    {
        private const int Iterations = 100000;

        public static string Create(string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            var hash = Derive(password, salt, Iterations);
            return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string stored)
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations)) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Derive(password, salt, iterations);
            if (actual.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < actual.Length; i++) diff |= actual[i] ^ expected[i];
            return diff == 0;
        }

        /// <summary>PBKDF2-HMAC-SHA256，输出 32 字节（netstandard2.0 的 Rfc2898DeriveBytes 不支持 SHA256）。</summary>
        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(password)))
            {
                var block = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, block, 0, salt.Length);
                block[block.Length - 1] = 1; // 第 1 块（INT_32_BE(1)）
                var u = hmac.ComputeHash(block);
                var result = (byte[])u.Clone();
                for (var i = 1; i < iterations; i++)
                {
                    u = hmac.ComputeHash(u);
                    for (var j = 0; j < result.Length; j++) result[j] ^= u[j];
                }
                return result;
            }
        }
    }
}
