using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using MdQuiz.Core.Models;
using MdQuiz.Server.Services;
using MdQuiz.Teacher.Services;
using MdQuiz.Teacher.ViewModels;
using MdQuiz.Wpf.Converters;

namespace MdQuiz.Teacher.Views.Dialogs
{
    public partial class RandomDrawWindow
    {
        private const string AnyTag = "不限标签";

        public partial class RuleRow : ObservableObject
        {
            private readonly Action<RuleRow> _changed;

            public RuleRow(List<TypeOption> types, List<string> tags, TypeOption type, double score, Action<RuleRow> changed)
            {
                TypeOptions = types;
                Tags = tags;
                _type = type;
                _tag = AnyTag;
                _count = 5;
                _scoreEach = score;
                _changed = changed;
            }

            public List<TypeOption> TypeOptions { get; }
            public List<string> Tags { get; }

            [ObservableProperty] private TypeOption _type;
            [ObservableProperty] private string _tag;
            [ObservableProperty] private double _count;
            [ObservableProperty] private double _scoreEach;
            [ObservableProperty] private string _availableText = "";
            [ObservableProperty] private bool _notEnough;

            partial void OnTypeChanged(TypeOption value) => _changed(this);
            partial void OnTagChanged(string value) => _changed(this);
            partial void OnCountChanged(double value) => _changed(this);
            partial void OnScoreEachChanged(double value) => _changed(this);

            public RandomDrawRule ToRule() => new RandomDrawRule
            {
                Type = Type?.Type ?? QuestionType.Single,
                Tag = Tag == AnyTag ? "" : Tag,
                Count = (int)Math.Round(Count),
                ScoreEach = ScoreEach,
            };
        }

        private readonly ISet<long> _exclude;
        private readonly List<TypeOption> _types = TypeOption.Only();
        private readonly List<string> _tags;
        private readonly ObservableCollection<RuleRow> _rules = new ObservableCollection<RuleRow>();

        public RandomDrawWindow(ISet<long> exclude)
        {
            InitializeComponent();
            _exclude = exclude ?? new HashSet<long>();
            _tags = new List<string> { AnyTag };
            _tags.AddRange(AppServices.Current.Questions.AllTags());
            RuleList.ItemsSource = _rules;
            // 默认规则：题库里有的题型各一条
            foreach (var t in _types)
            {
                if (AppServices.Current.PaperService.CountAvailable(t.Type.Value, null, _exclude) > 0) AddRule(t);
            }
            if (_rules.Count == 0) AddRule(_types[0]);
            UpdateSummary();
        }

        public DrawResult Result { get; private set; }

        private void AddRule(TypeOption type)
        {
            var row = new RuleRow(_types, _tags, type, ImportPreviewWindow.DefaultScore(type.Type.Value), OnRuleChanged);
            // 默认抽取数量不超过题库中可用的题目数
            var available = AppServices.Current.PaperService.CountAvailable(type.Type.Value, null, _exclude);
            row.Count = Math.Min(5, available);
            _rules.Add(row);
            Recount(row);
        }

        private void OnRuleChanged(RuleRow row)
        {
            Recount(row);
            UpdateSummary();
        }

        private void Recount(RuleRow row)
        {
            var rule = row.ToRule();
            var available = AppServices.Current.PaperService.CountAvailable(rule.Type, rule.Tag, _exclude);
            row.AvailableText = $"{available} 题";
            row.NotEnough = available < rule.Count;
        }

        private void UpdateSummary()
        {
            var rules = _rules.Select(r => r.ToRule()).ToList();
            SummaryText.Text = $"计划抽取 {rules.Sum(r => r.Count)} 题，共 {ScoreConverter.Format(rules.Sum(r => r.Count * r.ScoreEach))} 分";
        }

        private void OnAddRule(object sender, RoutedEventArgs e)
        {
            AddRule(_types[0]);
            UpdateSummary();
        }

        private void OnRemoveRule(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleRow row) _rules.Remove(row);
            UpdateSummary();
        }

        private void OnDraw(object sender, RoutedEventArgs e)
        {
            var rules = _rules.Select(r => r.ToRule()).Where(r => r.Count > 0).ToList();
            if (rules.Count == 0) return;
            Result = AppServices.Current.PaperService.Draw(rules, _exclude);
            DialogResult = Result.Items.Count > 0 || Result.Warnings.Count > 0;
        }
    }
}
