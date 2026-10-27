using System.Windows;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class StudentEditWindow
    {
        public StudentEditWindow(string studentNo, string name)
        {
            InitializeComponent();
            NoInput.Text = studentNo ?? "";
            NameInput.Text = name ?? "";
            Loaded += (s, e) => NameInput.Focus();
        }

        public string StudentNo => NoInput.Text.Trim();

        public string StudentName => NameInput.Text.Trim();

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (StudentNo.Length == 0 || StudentName.Length == 0) return;
            DialogResult = true;
        }
    }
}
