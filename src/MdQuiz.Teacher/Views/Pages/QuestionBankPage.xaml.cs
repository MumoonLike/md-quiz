using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class QuestionBankPage
    {
        public QuestionBankPage()
        {
            InitializeComponent();
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;
            if (ItemsControl.ContainerFromElement(QuestionList, source) is ListBoxItem item && item.DataContext is QuestionItem q)
                (DataContext as QuestionBankViewModel)?.EditCommand.Execute(q);
        }
    }
}
