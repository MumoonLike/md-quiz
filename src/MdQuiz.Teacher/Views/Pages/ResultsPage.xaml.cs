using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class ResultsPage
    {
        public ResultsPage()
        {
            InitializeComponent();
        }

        /// <summary>双击成绩表中的一行：查看该考生的答卷。</summary>
        private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !(e.OriginalSource is DependencyObject source)) return;
            var row = ItemsControl.ContainerFromElement(ResultGrid, source) as DataGridRow;
            if (row?.Item is ResultRowItem item && DataContext is ResultsViewModel vm)
            {
                vm.ViewAnswersCommand.Execute(item);
                e.Handled = true;
            }
        }
    }
}
