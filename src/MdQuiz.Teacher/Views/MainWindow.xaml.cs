using System.ComponentModel;
using MdQuiz.Core.Models;
using MdQuiz.Teacher.Services;
using MdQuiz.Teacher.ViewModels;
using MdQuiz.Wpf;

namespace MdQuiz.Teacher.Views
{
    public partial class MainWindow
    {
        private readonly MainViewModel _vm;

        public MainWindow(AppServices services)
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;
            Loaded += (s, e) => _vm.Initialize();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var exam = AppServices.Current.Host.CurrentExam;
            if (exam != null && exam.Status == ExamStatus.Running)
            {
                if (!Ui.ConfirmDanger("考试正在进行中！关闭教师端后学生将无法继续提交答案（重新打开教师端可以恢复考试）。确定要关闭吗？", "关闭教师端"))
                {
                    e.Cancel = true;
                    return;
                }
            }
            else if (_vm.Papers.IsDirty && !Ui.Confirm("试卷有未保存的修改，确定要关闭吗？"))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
