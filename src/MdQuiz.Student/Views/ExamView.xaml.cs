using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MdQuiz.Student.ViewModels;

namespace MdQuiz.Student.Views
{
    public partial class ExamView
    {
        private ExamViewModel _vm;

        public ExamView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
                _vm = DataContext as ExamViewModel;
                if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            };
            Loaded += (s, e) => Focus();
            Focusable = true;
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ExamViewModel.Current)) QuestionScroll.ScrollToTop();
        }

        /// <summary>不在输入框中时，用左右方向键翻页。</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_vm == null || Keyboard.FocusedElement is TextBoxBase) return;
            if (e.Key == Key.Left || e.Key == Key.PageUp)
            {
                _vm.PreviousCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.PageDown)
            {
                _vm.NextCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
