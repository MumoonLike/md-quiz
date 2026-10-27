using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MdQuiz.Core.Models;
using MdQuiz.Server.Data;
using MdQuiz.Teacher.Services;
using MdQuiz.Teacher.ViewModels;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class QuestionPickerWindow
    {
        public partial class PickItem : ObservableObject
        {
            public Question Question { get; set; }
            public QuestionType Type => Question.Type;
            public string Summary => Question.Summary(110);
            public string TagsText => string.Join("  ", Question.Tags.Select(t => "#" + t));
            public bool CanPick { get; set; }

            [ObservableProperty] private bool _isChecked;
        }

        private readonly ISet<long> _exclude;
        private readonly Dictionary<long, PickItem> _items = new Dictionary<long, PickItem>();
        private bool _ready;

        public QuestionPickerWindow(ISet<long> exclude)
        {
            InitializeComponent();
            _exclude = exclude ?? new HashSet<long>();
            TypeBox.ItemsSource = TypeOption.WithAll();
            TypeBox.SelectedIndex = 0;
            var tags = new List<string> { "全部标签" };
            tags.AddRange(AppServices.Current.Questions.AllTags());
            TagBox.ItemsSource = tags;
            TagBox.SelectedIndex = 0;
            _ready = true;
            Refresh();
            PickList.PreviewMouseLeftButtonUp += OnItemClick;
        }

        public List<Question> Picked { get; } = new List<Question>();

        public double ScoreFor(QuestionType type)
        {
            switch (type)
            {
                case QuestionType.Multiple: return ScoreMultiple.Value;
                case QuestionType.TrueFalse: return ScoreTf.Value;
                case QuestionType.FillBlank: return ScoreFill.Value;
                case QuestionType.ShortAnswer: return ScoreShort.Value;
                default: return ScoreSingle.Value;
            }
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (_ready) Refresh();
        }

        private void Refresh()
        {
            var tag = TagBox.SelectedItem as string;
            var list = AppServices.Current.Questions.Query(new QuestionQuery
            {
                Keyword = KeywordBox.Text,
                Type = (TypeBox.SelectedItem as TypeOption)?.Type,
                Tag = tag == "全部标签" ? null : tag,
            });
            var view = new List<PickItem>();
            foreach (var q in list)
            {
                if (!_items.TryGetValue(q.Id, out var item))
                {
                    item = new PickItem { Question = q, CanPick = !_exclude.Contains(q.Id) };
                    item.PropertyChanged += (s, e) => UpdateCount();
                    _items[q.Id] = item;
                }
                view.Add(item);
            }
            PickList.ItemsSource = view;
            UpdateCount();
        }

        private void UpdateCount()
        {
            var n = _items.Values.Count(i => i.IsChecked);
            CountText.Text = $"当前列表 {((IList<PickItem>)PickList.ItemsSource)?.Count ?? 0} 题，已选 {n} 题";
            AddButton.Content = n == 0 ? "添加所选" : $"添加所选（{n}）";
            AddButton.IsEnabled = n > 0;
        }

        /// <summary>点击整行切换勾选（点在复选框上时由复选框自己处理）。</summary>
        private void OnItemClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return;
            if (ItemsControl.ContainerFromElement(PickList, e.OriginalSource as DependencyObject) is ListBoxItem container
                && container.DataContext is PickItem item && item.CanPick)
                item.IsChecked = !item.IsChecked;
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var item in (IEnumerable<PickItem>)PickList.ItemsSource)
                if (item.CanPick) item.IsChecked = true;
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items.Values) item.IsChecked = false;
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            Picked.Clear();
            Picked.AddRange(_items.Values.Where(i => i.IsChecked && i.CanPick).Select(i => i.Question).OrderBy(q => q.Type).ThenBy(q => q.Id));
            DialogResult = Picked.Count > 0;
        }
    }
}
