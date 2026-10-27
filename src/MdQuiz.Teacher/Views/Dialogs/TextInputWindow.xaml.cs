using System.Windows;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class TextInputWindow
    {
        public TextInputWindow(string title, string label, string value)
        {
            InitializeComponent();
            Title = title;
            LabelText.Text = label;
            Input.Text = value ?? "";
            Loaded += (s, e) =>
            {
                Input.Focus();
                Input.SelectAll();
            };
        }

        public string Value => Input.Text;

        /// <summary>弹出输入框；取消时返回 null。</summary>
        public static string Ask(string title, string label, string value)
        {
            var dialog = new TextInputWindow(title, label, value);
            return Ui.ShowDialog(dialog) == true ? dialog.Value : null;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
