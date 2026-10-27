using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using Dapper;
using MdQuiz.Core.Models;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Server.Data
{
    public class QuestionQuery
    {
        public QuestionType? Type { get; set; }
        public string Tag { get; set; }
        public string Keyword { get; set; }
    }

    public sealed class QuestionRepository
    {
        private const string Columns = "id, type, stem, options_json, answer, blanks_json, analysis, tags, created_at, updated_at";
        private readonly Database _db;

        public QuestionRepository(Database db)
        {
            _db = db;
        }

        private sealed class Row
        {
            public long Id { get; set; }
            public long Type { get; set; }
            public string Stem { get; set; }
            public string OptionsJson { get; set; }
            public string Answer { get; set; }
            public string BlanksJson { get; set; }
            public string Analysis { get; set; }
            public string Tags { get; set; }
            public long CreatedAt { get; set; }
            public long UpdatedAt { get; set; }

            public Question ToModel() => new Question
            {
                Id = Id,
                Type = (QuestionType)Type,
                Stem = Stem ?? "",
                Options = Json.Deserialize<List<string>>(OptionsJson) ?? new List<string>(),
                Answer = Answer ?? "",
                Blanks = Json.Deserialize<List<string>>(BlanksJson) ?? new List<string>(),
                Analysis = Analysis ?? "",
                Tags = SplitTags(Tags),
                CreatedAt = Time.FromUnixMs(CreatedAt),
                UpdatedAt = Time.FromUnixMs(UpdatedAt),
            };
        }

        /// <summary>标签以 ",a,b," 形式保存，便于用 LIKE '%,a,%' 精确匹配。</summary>
        private static string JoinTags(IEnumerable<string> tags)
        {
            var list = (tags ?? Enumerable.Empty<string>()).Select(t => t.Trim().Replace(",", "，")).Where(t => t.Length > 0).Distinct().ToList();
            return list.Count == 0 ? "" : "," + string.Join(",", list) + ",";
        }

        private static List<string> SplitTags(string tags) => (tags ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static object Params(Question q, long now) => new
        {
            q.Id,
            Type = (int)q.Type,
            Stem = q.Stem ?? "",
            OptionsJson = Json.Serialize(q.Options ?? new List<string>()),
            Answer = q.Answer ?? "",
            BlanksJson = Json.Serialize(q.Blanks ?? new List<string>()),
            Analysis = q.Analysis ?? "",
            Tags = JoinTags(q.Tags),
            Now = now,
        };

        private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        public List<Question> Query(QuestionQuery query = null)
        {
            query = query ?? new QuestionQuery();
            var where = new List<string>();
            var p = new DynamicParameters();
            if (query.Type.HasValue)
            {
                where.Add("type = @type");
                p.Add("type", (int)query.Type.Value);
            }
            if (!string.IsNullOrWhiteSpace(query.Tag))
            {
                where.Add("tags LIKE @tag ESCAPE '\\'");
                p.Add("tag", "%," + EscapeLike(query.Tag.Trim()) + ",%");
            }
            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                where.Add("(stem LIKE @kw ESCAPE '\\' OR options_json LIKE @kw ESCAPE '\\' OR tags LIKE @kw ESCAPE '\\')");
                p.Add("kw", "%" + EscapeLike(query.Keyword.Trim()) + "%");
            }
            var sql = $"SELECT {Columns} FROM questions" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC";
            return _db.Read(c => c.Query<Row>(sql, p).Select(r => r.ToModel()).ToList());
        }

        public Question Get(long id)
        {
            return _db.Read(c => c.QueryFirstOrDefault<Row>($"SELECT {Columns} FROM questions WHERE id = @id", new { id }))?.ToModel();
        }

        public Dictionary<long, Question> GetMany(IEnumerable<long> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return new Dictionary<long, Question>();
            return _db.Read(c => c.Query<Row>($"SELECT {Columns} FROM questions WHERE id IN @list", new { list }))
                .Select(r => r.ToModel()).ToDictionary(q => q.Id);
        }

        public int Count() => _db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM questions"));

        public long Insert(Question q)
        {
            return _db.Write((c, t) => Insert(c, t, q));
        }

        private static long Insert(IDbConnection c, IDbTransaction t, Question q)
        {
            var now = Time.ToUnixMs(DateTime.UtcNow);
            c.Execute(@"INSERT INTO questions(type, stem, options_json, answer, blanks_json, analysis, tags, created_at, updated_at)
                        VALUES(@Type, @Stem, @OptionsJson, @Answer, @BlanksJson, @Analysis, @Tags, @Now, @Now)", Params(q, now), t);
            q.Id = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
            q.CreatedAt = q.UpdatedAt = Time.FromUnixMs(now);
            return q.Id;
        }

        public int InsertMany(IEnumerable<Question> questions)
        {
            return _db.Write((c, t) =>
            {
                var n = 0;
                foreach (var q in questions)
                {
                    Insert(c, t, q);
                    n++;
                }
                return n;
            });
        }

        public void Update(Question q)
        {
            var now = Time.ToUnixMs(DateTime.UtcNow);
            _db.Write((c, t) => c.Execute(@"UPDATE questions SET type = @Type, stem = @Stem, options_json = @OptionsJson, answer = @Answer,
                        blanks_json = @BlanksJson, analysis = @Analysis, tags = @Tags, updated_at = @Now WHERE id = @Id", Params(q, now), t));
            q.UpdatedAt = Time.FromUnixMs(now);
        }

        public int Delete(IEnumerable<long> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return 0;
            return _db.Write((c, t) => c.Execute("DELETE FROM questions WHERE id IN @list", new { list }, t));
        }

        public List<string> AllTags()
        {
            return _db.Read(c => c.Query<string>("SELECT DISTINCT tags FROM questions WHERE tags <> ''"))
                .SelectMany(SplitTags).Distinct().OrderBy(t => t, StringComparer.CurrentCulture).ToList();
        }

        /// <summary>符合条件的题目 id（用于随机抽题）。</summary>
        public List<long> FindIds(QuestionType type, string tag)
        {
            var sql = "SELECT id FROM questions WHERE type = @type";
            if (!string.IsNullOrWhiteSpace(tag)) sql += " AND tags LIKE @tag ESCAPE '\\'";
            return _db.Read(c => c.Query<long>(sql, new { type = (int)type, tag = "%," + EscapeLike((tag ?? "").Trim()) + ",%" }).ToList());
        }
    }

    public sealed class ResourceRepository
    {
        private readonly Database _db;

        public ResourceRepository(Database db)
        {
            _db = db;
        }

        /// <summary>保存图片并返回资源哈希（内容相同的图片只存一份）。</summary>
        public string Save(byte[] data, string mime)
        {
            string hash;
            using (var sha = SHA256.Create())
                hash = string.Concat(sha.ComputeHash(data).Take(16).Select(b => b.ToString("x2")));
            var now = Time.ToUnixMs(DateTime.UtcNow);
            _db.Write((c, t) => c.Execute("INSERT OR IGNORE INTO resources(hash, mime, data, created_at) VALUES(@hash, @mime, @data, @now)",
                new { hash, mime = mime ?? "application/octet-stream", data, now }, t));
            return hash;
        }

        public byte[] Get(string hash)
        {
            return _db.Read(c => c.QueryFirstOrDefault<byte[]>("SELECT data FROM resources WHERE hash = @hash", new { hash = hash?.ToLowerInvariant() }));
        }

        public Dictionary<string, string> GetBase64(IEnumerable<string> hashes)
        {
            var list = hashes.Select(h => h.ToLowerInvariant()).Distinct().ToList();
            var result = new Dictionary<string, string>();
            if (list.Count == 0) return result;
            foreach (var row in _db.Read(c => c.Query<(string Hash, byte[] Data)>("SELECT hash, data FROM resources WHERE hash IN @list", new { list }).ToList()))
                result[row.Hash] = Convert.ToBase64String(row.Data);
            return result;
        }

        public static string MimeFromExtension(string ext)
        {
            switch ((ext ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "png": return "image/png";
                case "jpg": case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "bmp": return "image/bmp";
                default: return "application/octet-stream";
            }
        }
    }
}
