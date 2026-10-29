using System.Windows;
using System.Windows.Input;
using MdQuiz.Teacher.Services;

namespace MdQuiz.Teacher.Views
{
    public partial class LoginWindow
    {
        private readonly AppServices _services;
        private readonly bool _firstRun;
        private int _failures;

        public LoginWindow(AppServices services)
        {
            InitializeComponent();
            _services = services;
            _firstRun = !services.Settings.HasPassword;
            VersionText.Text = "版本 " + MdQuiz.Core.Brand.FormatVersion(typeof(LoginWindow).Assembly.GetName().Version);
            if (_firstRun)
            {
                HeaderText.Text = "首次使用";
                SubText.Text = "请设置管理密码。之后每次打开教师端都需要输入，防止学生误操作教师机。";
                ConfirmPanel.Visibility = Visibility.Visible;
                SubmitButton.Content = "设置密码并进入";
            }
            Loaded += (s, e) => PasswordInput.Focus();
        }

        private void OnPasswordKeyDown(object sender, KeyEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void OnSubmit(object sender, RoutedEventArgs e)
        {
            var password = PasswordInput.Password;
            if (_firstRun)
            {
                if (password.Length < 4)
                {
                    ShowError("密码至少 4 位");
                    return;
                }
                if (password != ConfirmInput.Password)
                {
                    ShowError("两次输入的密码不一致");
                    return;
                }
                _services.Settings.SetPassword(password);
                DialogResult = true;
                return;
            }

            if (_services.Settings.VerifyPassword(password))
            {
                DialogResult = true;
                return;
            }
            _failures++;
            PasswordInput.Clear();
            PasswordInput.Focus();
            ShowError(_failures >= 3 ? "密码错误。忘记密码可按下方提示重置。" : "密码错误，请重试");
        }
    }
}
