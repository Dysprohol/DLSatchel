using System;
using System.Windows;
using DDV_SaveEditor_G.Data;

namespace DDV_SaveEditor_G
{
    /// <summary>Asks for the link of an item list to download.</summary>
    public partial class LinkDialog : Window
    {
        public LinkDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => LinkBox.Focus();
        }

        public string Link => LinkBox.Text.Trim();

        private void Rubyelf_Click(object sender, RoutedEventArgs e) => LinkBox.Text = SourceDownloader.RubyelfLink;

        private void Kosddv_Click(object sender, RoutedEventArgs e) => LinkBox.Text = SourceDownloader.KosddvLink;

        private void LinkBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string? problem = null;
            if (Link.Length > 0)
            {
                try { SourceDownloader.ToDownloadUri(Link); }
                catch (ArgumentException ex) { problem = ex.Message; }
            }
            ProblemText.Text = problem ?? "";
            OkButton.IsEnabled = Link.Length > 0 && problem == null;
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
