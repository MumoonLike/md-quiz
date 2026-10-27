using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MdQuiz.Core.Models;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class StudentsPage
    {
        public StudentsPage()
        {
            InitializeComponent();
        }

        private StudentsViewModel Vm => DataContext as StudentsViewModel;

        private void OnNameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            // 回车直接添加，方便连续录入
            if (sender is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Vm?.AddStudentCommand.Execute(null);
        }

        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;
            var row = ItemsControl.ContainerFromElement(StudentGrid, source) as DataGridRow;
            if (row?.Item is Student s) Vm?.EditStudentCommand.Execute(s);
        }
    }
}
