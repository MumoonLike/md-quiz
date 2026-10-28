using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using MdQuiz.Core.Models;
using MdQuiz.Core.Protocol;

namespace MdQuiz.Server.Data
{
    public sealed class PaperRepository
    {
        private readonly Database _db;

        public PaperRepository(Database db)
        {
            _db = db;
        }

        private sealed class Row
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string ItemsJson { get; set; }
            public long? GroupId { get; set; }
            public long CreatedAt { get; set; }
            public long UpdatedAt { get; set; }

            public Paper ToModel() => new Paper
            {
                Id = Id,
                Name = Name ?? "",
                Description = Description ?? "",
                GroupId = GroupId,
                Items = Json.Deserialize<List<PaperItem>>(ItemsJson) ?? new List<PaperItem>(),
                CreatedAt = Time.FromUnixMs(CreatedAt),
                UpdatedAt = Time.FromUnixMs(UpdatedAt),
            };
        }

        public List<Paper> List()
        {
            return _db.Read(c => c.Query<Row>("SELECT * FROM papers ORDER BY updated_at DESC").Select(r => r.ToModel()).ToList());
        }

        public Paper Get(long id)
        {
            return _db.Read(c => c.QueryFirstOrDefault<Row>("SELECT * FROM papers WHERE id = @id", new { id }))?.ToModel();
        }

        public long Save(Paper paper)
        {
            var now = Time.ToUnixMs(DateTime.UtcNow);
            var args = new { paper.Id, Name = paper.Name ?? "", Description = paper.Description ?? "", ItemsJson = Json.Serialize(paper.Items), paper.GroupId, Now = now };
            return _db.Write((c, t) =>
            {
                if (paper.GroupId.HasValue && c.ExecuteScalar<long>("SELECT COUNT(*) FROM paper_groups WHERE id = @GroupId", args, t) == 0)
                    throw new InvalidOperationException("试卷分组不存在");
                if (paper.Id > 0)
                {
                    c.Execute("UPDATE papers SET name = @Name, description = @Description, items_json = @ItemsJson, group_id = @GroupId, updated_at = @Now WHERE id = @Id", args, t);
                }
                else
                {
                    c.Execute("INSERT INTO papers(name, description, items_json, group_id, created_at, updated_at) VALUES(@Name, @Description, @ItemsJson, @GroupId, @Now, @Now)", args, t);
                    paper.Id = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
                    paper.CreatedAt = Time.FromUnixMs(now);
                }
                paper.UpdatedAt = Time.FromUnixMs(now);
                return paper.Id;
            });
        }

        public void Delete(long id)
        {
            _db.Write((c, t) => c.Execute("DELETE FROM papers WHERE id = @id", new { id }, t));
        }

        // ---------------- 分组 ----------------

        public List<PaperGroup> ListGroups()
        {
            return _db.Read(c => c.Query<PaperGroup>(
                "SELECT g.id, g.name, g.sort_order, (SELECT COUNT(*) FROM papers p WHERE p.group_id = g.id) AS paper_count FROM paper_groups g ORDER BY g.sort_order, g.name").ToList());
        }

        public long AddGroup(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("分组名称不能为空");
            return _db.Write((c, t) =>
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM paper_groups WHERE name = @name", new { name }, t) > 0)
                    throw new InvalidOperationException($"分组“{name}”已存在");
                var order = c.ExecuteScalar<long>("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM paper_groups", transaction: t);
                c.Execute("INSERT INTO paper_groups(name, sort_order) VALUES(@name, @order)", new { name, order }, t);
                return c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
            });
        }

        public void RenameGroup(long id, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("分组名称不能为空");
            _db.Write((c, t) =>
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM paper_groups WHERE name = @name AND id <> @id", new { name, id }, t) > 0)
                    throw new InvalidOperationException($"分组“{name}”已存在");
                c.Execute("UPDATE paper_groups SET name = @name WHERE id = @id", new { name, id }, t);
            });
        }

        /// <summary>删除分组；组内试卷不删除，变为未分组。</summary>
        public void DeleteGroup(long id)
        {
            _db.Write((c, t) =>
            {
                c.Execute("UPDATE papers SET group_id = NULL WHERE group_id = @id", new { id }, t);
                c.Execute("DELETE FROM paper_groups WHERE id = @id", new { id }, t);
            });
        }

        /// <summary>把试卷移动到分组；<paramref name="groupId"/> 为 null 表示移出分组。</summary>
        public int MoveToGroup(IEnumerable<long> paperIds, long? groupId)
        {
            var list = paperIds.Distinct().ToList();
            if (list.Count == 0) return 0;
            return _db.Write((c, t) =>
            {
                if (groupId.HasValue && c.ExecuteScalar<long>("SELECT COUNT(*) FROM paper_groups WHERE id = @groupId", new { groupId }, t) == 0)
                    throw new InvalidOperationException("试卷分组不存在");
                return c.Execute("UPDATE papers SET group_id = @groupId WHERE id IN @list", new { groupId, list }, t);
            });
        }
    }

    public class ImportReport
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    public class RosterRow
    {
        public string StudentNo { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
        public int Line { get; set; }
    }

    public sealed class RosterRepository
    {
        private readonly Database _db;

        public RosterRepository(Database db)
        {
            _db = db;
        }

        public List<SchoolClass> ListClasses()
        {
            return _db.Read(c => c.Query<SchoolClass>(
                "SELECT c.id, c.name, (SELECT COUNT(*) FROM students s WHERE s.class_id = c.id) AS student_count FROM classes c ORDER BY c.name").ToList());
        }

        public long AddClass(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("班级名称不能为空");
            return _db.Write((c, t) =>
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM classes WHERE name = @name", new { name }, t) > 0)
                    throw new InvalidOperationException($"班级“{name}”已存在");
                c.Execute("INSERT INTO classes(name) VALUES(@name)", new { name }, t);
                return c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
            });
        }

        public void RenameClass(long id, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("班级名称不能为空");
            _db.Write((c, t) =>
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM classes WHERE name = @name AND id <> @id", new { name, id }, t) > 0)
                    throw new InvalidOperationException($"班级“{name}”已存在");
                c.Execute("UPDATE classes SET name = @name WHERE id = @id", new { name, id }, t);
            });
        }

        public void DeleteClass(long id)
        {
            _db.Write((c, t) => c.Execute("DELETE FROM classes WHERE id = @id", new { id }, t));
        }

        public List<Student> ListStudents(long? classId = null)
        {
            const string sql = "SELECT s.id, s.class_id, s.student_no, s.name, c.name AS class_name FROM students s JOIN classes c ON c.id = s.class_id";
            return _db.Read(c => classId.HasValue
                ? c.Query<Student>(sql + " WHERE s.class_id = @classId ORDER BY s.student_no", new { classId }).ToList()
                : c.Query<Student>(sql + " ORDER BY s.student_no").ToList());
        }

        public List<Student> StudentsInClasses(IEnumerable<long> classIds)
        {
            var list = classIds.Distinct().ToList();
            if (list.Count == 0) return new List<Student>();
            return _db.Read(c => c.Query<Student>(
                "SELECT s.id, s.class_id, s.student_no, s.name, c.name AS class_name FROM students s JOIN classes c ON c.id = s.class_id WHERE s.class_id IN @list ORDER BY c.name, s.student_no",
                new { list }).ToList());
        }

        public void AddStudent(long classId, string studentNo, string name)
        {
            studentNo = (studentNo ?? "").Trim();
            name = (name ?? "").Trim();
            if (studentNo.Length == 0 || name.Length == 0) throw new ArgumentException("学号和姓名都不能为空");
            _db.Write((c, t) =>
            {
                var existing = c.QueryFirstOrDefault<Student>(
                    "SELECT s.id, s.class_id, s.student_no, s.name, c.name AS class_name FROM students s JOIN classes c ON c.id = s.class_id WHERE s.student_no = @studentNo",
                    new { studentNo }, t);
                if (existing != null)
                    throw new InvalidOperationException($"学号 {studentNo} 已存在（{existing.ClassName} {existing.Name}）");
                c.Execute("INSERT INTO students(class_id, student_no, name) VALUES(@classId, @studentNo, @name)", new { classId, studentNo, name }, t);
            });
        }

        public void UpdateStudent(long id, string studentNo, string name)
        {
            studentNo = (studentNo ?? "").Trim();
            name = (name ?? "").Trim();
            if (studentNo.Length == 0 || name.Length == 0) throw new ArgumentException("学号和姓名都不能为空");
            _db.Write((c, t) =>
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM students WHERE student_no = @studentNo AND id <> @id", new { studentNo, id }, t) > 0)
                    throw new InvalidOperationException($"学号 {studentNo} 已存在");
                c.Execute("UPDATE students SET student_no = @studentNo, name = @name WHERE id = @id", new { studentNo, name, id }, t);
            });
        }

        public int DeleteStudents(IEnumerable<long> ids)
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return 0;
            return _db.Write((c, t) => c.Execute("DELETE FROM students WHERE id IN @list", new { list }, t));
        }

        /// <summary>
        /// 导入花名册。学号已存在时更新姓名和班级；行内没有班级时放入 <paramref name="defaultClassId"/>；
        /// 不存在的班级会自动创建。
        /// </summary>
        public ImportReport Import(IEnumerable<RosterRow> rows, long? defaultClassId)
        {
            var report = new ImportReport();
            _db.Write((c, t) =>
            {
                var classes = c.Query<SchoolClass>("SELECT id, name FROM classes", transaction: t).ToDictionary(x => x.Name, x => x.Id);
                var seen = new HashSet<string>();
                foreach (var row in rows)
                {
                    var no = (row.StudentNo ?? "").Trim();
                    var name = (row.Name ?? "").Trim();
                    var className = (row.ClassName ?? "").Trim();
                    if (no.Length == 0 && name.Length == 0) continue;
                    if (no.Length == 0 || name.Length == 0)
                    {
                        report.Errors.Add($"第 {row.Line} 行：学号或姓名为空");
                        continue;
                    }
                    if (!seen.Add(no))
                    {
                        report.Errors.Add($"第 {row.Line} 行：学号 {no} 在文件中重复，已跳过");
                        continue;
                    }
                    long classId;
                    if (className.Length > 0)
                    {
                        if (!classes.TryGetValue(className, out classId))
                        {
                            c.Execute("INSERT INTO classes(name) VALUES(@className)", new { className }, t);
                            classId = c.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: t);
                            classes[className] = classId;
                        }
                    }
                    else if (defaultClassId.HasValue)
                    {
                        classId = defaultClassId.Value;
                    }
                    else
                    {
                        report.Errors.Add($"第 {row.Line} 行：没有填写班级");
                        continue;
                    }

                    var existingId = c.ExecuteScalar<long?>("SELECT id FROM students WHERE student_no = @no", new { no }, t);
                    if (existingId.HasValue)
                    {
                        c.Execute("UPDATE students SET name = @name, class_id = @classId WHERE id = @id", new { name, classId, id = existingId.Value }, t);
                        report.Updated++;
                    }
                    else
                    {
                        c.Execute("INSERT INTO students(class_id, student_no, name) VALUES(@classId, @no, @name)", new { classId, no, name }, t);
                        report.Added++;
                    }
                }
            });
            return report;
        }
    }
}
