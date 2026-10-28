using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MdQuiz.Teacher.Services;
using MdQuiz.Teacher.ViewModels;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.Views.Dialogs
{
    /// <summary>查看考生答卷（非模态，可以一边监考一边查看）。同一场考试只打开一个窗口。</summary>
    public partial class StudentAnswersWindow
    {
        private static StudentAnswersWindow _open;
        private readonly StudentAnswersViewModel _vm;

        private StudentAnswersWindow(StudentAnswersViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Title = "考生答卷 - " + vm.Title;
            vm.ScrollToTopRequested += () => CardScroll.ScrollToTop();
            Closed += (s, e) =>
            {
                vm.Stop();
                if (_open == this) _open = null;
            };
        }

        /// <summary>打开（或切换到）某场考试的答卷窗口并选中考生。<paramref name="order"/> 为列表中考生的顺序（学号）。</summary>
        public static void Open(long examId, string studentNo, IEnumerable<string> order)
        {
            if (_open != null && _open._vm.Exam.Id == examId)
            {
                _open._vm.Select(studentNo);
                if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
                _open.Activate();
                return;
            }
            _open?.Close();
            var exam = AppServices.Current.Exams.Get(examId);
            if (exam == null)
            {
                Ui.Error("考试不存在或已被删除");
                return;
            }
            var window = new StudentAnswersWindow(new StudentAnswersViewModel(exam, order?.ToList()));
            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            _open = window;
            window.Show();
            window._vm.Select(studentNo);
            window.StudentList.Focus();
        }

        /// <summary>PageUp / PageDown 切换考生（在任何位置都可用，搜索框除外的方向键交给列表）。</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.PageUp || (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt))
            {
                _vm.PreviousCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown || (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt))
            {
                _vm.NextCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !(Keyboard.FocusedElement is TextBoxBase))
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
