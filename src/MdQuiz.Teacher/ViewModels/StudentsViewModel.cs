using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdQuiz.Core.Models;
using MdQuiz.Server.Services;
using MdQuiz.Teacher.Views.Dialogs;
using MdQuiz.Wpf;
using Microsoft.Win32;

namespace MdQuiz.Teacher.ViewModels
{
    public partial class StudentsViewModel : PageViewModel
    {
        public ObservableCollection<SchoolClass> Classes { get; } = new ObservableCollection<SchoolClass>();
        public ObservableCollection<Student> Students { get; } = new ObservableCollection<Student>();

        [ObservableProperty] private SchoolClass _selectedClass;
        [ObservableProperty] private string _search = "";
        [ObservableProperty] private string _newNo = "";
        [ObservableProperty] private string _newName = "";
        [ObservableProperty] private string _summary = "";
        [ObservableProperty] private bool _hasClass;

        partial void OnSelectedClassChanged(SchoolClass value) => LoadStudents();
        partial void OnSearchChanged(string value) => LoadStudents();

        public override void OnActivated() => RefreshClasses(SelectedClass?.Id);

        private void RefreshClasses(long? keepId)
        {
            Classes.Clear();
            foreach (var c in S.Roster.ListClasses()) Classes.Add(c);
            SelectedClass = Classes.FirstOrDefault(c => c.Id == keepId) ?? Classes.FirstOrDefault();
            if (SelectedClass == null) LoadStudents();
            Summary = $"{Classes.Count} 个班级 · {Classes.Sum(c => c.StudentCount)} 名学生";
        }

        private void LoadStudents()
        {
            Students.Clear();
            HasClass = SelectedClass != null;
            if (SelectedClass == null) return;
            var keyword = (Search ?? "").Trim();
            foreach (var s in S.Roster.ListStudents(SelectedClass.Id))
            {
                if (keyword.Length == 0 || s.StudentNo.Contains(keyword) || s.Name.Contains(keyword)) Students.Add(s);
            }
        }

        [RelayCommand]
        private void AddClass()
        {
            var name = TextInputWindow.Ask("新建班级", "班级名称", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var id = S.Roster.AddClass(name);
                RefreshClasses(id);
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        [RelayCommand]
        private void RenameClass()
        {
            if (SelectedClass == null) return;
            var name = TextInputWindow.Ask("重命名班级", "班级名称", SelectedClass.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                S.Roster.RenameClass(SelectedClass.Id, name);
                RefreshClasses(SelectedClass.Id);
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        [RelayCommand]
        private void DeleteClass()
        {
            if (SelectedClass == null) return;
            if (!Ui.ConfirmDanger($"确定删除班级“{SelectedClass.Name}”及其 {SelectedClass.StudentCount} 名学生吗？\n已结束考试的成绩记录不受影响。", "删除班级")) return;
            S.Roster.DeleteClass(SelectedClass.Id);
            RefreshClasses(null);
        }

        [RelayCommand]
        private void AddStudent()
        {
            if (SelectedClass == null)
            {
                Ui.Info("请先新建或选择一个班级");
                return;
            }
            try
            {
                S.Roster.AddStudent(SelectedClass.Id, NewNo, NewName);
                NewNo = "";
                NewName = "";
                RefreshClasses(SelectedClass.Id);
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        [RelayCommand]
        private void EditStudent(Student student)
        {
            if (student == null) return;
            var dialog = new StudentEditWindow(student.StudentNo, student.Name);
            if (Ui.ShowDialog(dialog) != true) return;
            try
            {
                S.Roster.UpdateStudent(student.Id, dialog.StudentNo, dialog.StudentName);
                LoadStudents();
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
            }
        }

        [RelayCommand]
        private void DeleteStudents(IList selection)
        {
            var list = selection?.OfType<Student>().ToList();
            if (list == null || list.Count == 0) return;
            if (!Ui.ConfirmDanger(list.Count == 1 ? $"确定删除学生 {list[0].Name}（{list[0].StudentNo}）吗？" : $"确定删除选中的 {list.Count} 名学生吗？", "删除学生")) return;
            S.Roster.DeleteStudents(list.Select(s => s.Id));
            RefreshClasses(SelectedClass?.Id);
        }

        [RelayCommand]
        private void Import()
        {
            var dlg = new OpenFileDialog { Filter = "花名册|*.xlsx;*.csv", Title = "导入学生花名册" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var rows = ExcelService.ReadRoster(dlg.FileName);
                if (rows.Count == 0)
                {
                    Ui.Info("文件中没有学生数据");
                    return;
                }
                var withoutClass = rows.Count(r => string.IsNullOrWhiteSpace(r.ClassName));
                if (withoutClass > 0 && SelectedClass == null)
                {
                    Ui.Info("文件中有学生没有填写班级。请先选择一个班级（这些学生将导入到该班级），或在文件中补充“班级”列。");
                    return;
                }
                if (withoutClass > 0 && !Ui.Confirm($"有 {withoutClass} 名学生没有填写班级，将导入到当前选中的班级“{SelectedClass.Name}”。继续吗？")) return;
                var report = S.Roster.Import(rows, SelectedClass?.Id);
                RefreshClasses(SelectedClass?.Id);
                var message = $"新增 {report.Added} 人，更新 {report.Updated} 人。";
                if (report.Errors.Count > 0) message += $"\n\n有 {report.Errors.Count} 行未导入：\n" + string.Join("\n", report.Errors.Take(15)) + (report.Errors.Count > 15 ? "\n……" : "");
                Ui.Info(message, "导入完成");
            }
            catch (Exception ex)
            {
                Ui.Error("导入失败：" + ex.Message + "\n\n请确认文件没有被 Excel 占用，并参考模板格式（学号、姓名、班级）。");
            }
        }

        [RelayCommand]
        private void DownloadTemplate()
        {
            var dlg = new SaveFileDialog { Filter = "Excel 文件|*.xlsx", FileName = "花名册模板.xlsx" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ExcelService.WriteRosterTemplate(dlg.FileName);
                Ui.ToastFile("花名册模板已保存", dlg.FileName);
            }
            catch (Exception ex)
            {
                Ui.Error("保存失败：" + ex.Message);
            }
        }
    }
}
