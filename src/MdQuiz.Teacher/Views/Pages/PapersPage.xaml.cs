using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Pages
{
    public partial class PapersPage
    {
        public PapersPage()
        {
            InitializeComponent();
        }

        private PapersViewModel Vm => DataContext as PapersViewModel;

        private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is PaperNode node) Vm?.SelectFromTree(node);
        }

        /// <summary>分组标题上的“…”按钮：弹出与右键相同的菜单。</summary>
        private void OnGroupMenu(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement button)) return;
            var owner = button.TemplatedParent as ToggleButton;
            var menu = owner?.ContextMenu;
            if (menu == null) return;
            menu.PlacementTarget = owner;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
