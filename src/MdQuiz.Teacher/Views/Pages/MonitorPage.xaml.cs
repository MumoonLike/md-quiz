using System.Windows;
using System.Windows.Input;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class MonitorPage
    {
        public MonitorPage()
        {
            InitializeComponent();
        }

        /// <summary>双击学生卡片：查看该考生的答题情况。</summary>
        private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            // 点在文字上时原始来源可能是 Run（FrameworkContentElement）
            var context = (e.OriginalSource as FrameworkElement)?.DataContext ?? (e.OriginalSource as FrameworkContentElement)?.DataContext;
            if (context is ParticipantItem item && DataContext is MonitorViewModel vm)
            {
                vm.ViewAnswersCommand.Execute(item);
                e.Handled = true;
            }
        }
    }
}
