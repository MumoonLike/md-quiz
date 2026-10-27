using System.Windows.Controls;
using System.Windows.Input;
using MdQuiz.Student.ViewModels;

namespace MdQuiz.Student.Views
{
    public partial class LoginView
    {
        public LoginView()
        {
            InitializeComponent();
            Loaded += (s, e) => NoBox.Focus();
        }

        private void OnNoKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            (DataContext as LoginViewModel)?.NextCommand.Execute(null);
        }
    }
}
