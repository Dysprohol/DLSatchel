using System;
using System.Collections.Generic;
using System.Windows;

namespace DDV_SaveEditor_G
{
    public partial class SaveSelectorWindow : Window
    {
        public SaveEntry? SelectedSave { get; private set; }

        public SaveSelectorWindow(List<SaveEntry> saves)
        {
            InitializeComponent();
            SaveGrid.ItemsSource = saves;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (SaveGrid.SelectedItem is SaveEntry selected)
            {
                SelectedSave = selected;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Please select a save file first.",
                                "No selection",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }
        }
    }
}
