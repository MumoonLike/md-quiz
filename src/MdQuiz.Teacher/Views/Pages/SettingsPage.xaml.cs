using System.Windows;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class SettingsPage
    {
        public SettingsPage()
        {
            InitializeComponent();
        }

        private void OnChangePassword(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is SettingsViewModel vm)) return;
            if (!vm.ChangePassword(OldPassword.Password, NewPassword.Password, ConfirmPassword.Password)) return;
            OldPassword.Clear();
            NewPassword.Clear();
            ConfirmPassword.Clear();
        }
    }
}
