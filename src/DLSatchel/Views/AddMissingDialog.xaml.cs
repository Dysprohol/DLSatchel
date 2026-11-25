using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DDV_SaveEditor_G.Modules;

namespace DDV_SaveEditor_G
{
    /// <summary>Lets the player choose which kinds of missing items to add.</summary>
    public partial class AddMissingDialog : Window
    {
        public sealed class GroupChoice
        {
            public required MissingGroup Group { get; init; }
            public required int Count { get; init; }
            public bool IsChecked { get; set; }
            public bool HasItems => Count > 0;
            public string Label => MissingItems.Label(Group);
            public string CountText => $"  {Count:N0}";
            public string Description => MissingItems.Description(Group);
        }

        private readonly List<GroupChoice> _groups;

        public AddMissingDialog(string collection, IReadOnlyCollection<MissingItem> missing, bool gameNewerThanLists = false)
        {
            InitializeComponent();
            GameUpdateText.Visibility = gameNewerThanLists ? Visibility.Visible : Visibility.Collapsed;
            _groups = System.Enum.GetValues<MissingGroup>()
                .Select(g => new GroupChoice
                {
                    Group = g,
                    Count = missing.Count(m => m.Group == g),
                    IsChecked = MissingItems.CheckedByDefault(g) && missing.Any(m => m.Group == g)
                })
                .ToList();
            GroupList.ItemsSource = _groups;
            IntroText.Text = $"{collection}: you don't have {missing.Count:N0} of the items your lists know. " +
                             "Choose which kinds to add.";
            UpdateButton();
        }

        public IReadOnlySet<MissingGroup> SelectedGroups =>
            _groups.Where(g => g.IsChecked && g.HasItems).Select(g => g.Group).ToHashSet();

        private void Group_Changed(object sender, RoutedEventArgs e) => UpdateButton();

        private void UpdateButton()
        {
            int count = _groups.Where(g => g.IsChecked).Sum(g => g.Count);
            OkButton.Content = $"Add {count:N0} items";
            OkButton.IsEnabled = count > 0;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
