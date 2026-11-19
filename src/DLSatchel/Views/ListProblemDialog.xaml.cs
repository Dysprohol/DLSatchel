using System.Windows;
using DDV_SaveEditor_G.Data;

namespace DDV_SaveEditor_G
{
    /// <summary>Says in plain words why a list could not be updated and offers the ways out.</summary>
    public partial class ListProblemDialog : Window
    {
        public enum Fix { None, NewLink, File }

        public Fix Choice { get; private set; } = Fix.None;

        public ListProblemDialog(SourceUpdateResult result, bool hasCurrentList)
        {
            InitializeComponent();
            HeadlineText.Text = hasCurrentList
                ? $"The list \"{result.FileName}\" could not be updated"
                : "An item list could not be downloaded";
            ReasonText.Text = result.Problem switch
            {
                ListProblem.LinkBroken =>
                    "Its link no longer gives an item list. The sheet may have been deleted, moved to a new address " +
                    "or made private.",
                ListProblem.ListChanged =>
                    "A list came, but it does not look right, so it was not used. The people who make it may have " +
                    "changed or emptied the sheet.",
                ListProblem.FileLocked =>
                    "The list on this PC could not be replaced. If it is open in Excel, close it and try again.",
                _ => "The list could not be downloaded."
            };
            DetailText.Text = "Details: " + result.Message;
            StillWorksText.Text = hasCurrentList
                ? "Your current list is still used, so the editor keeps working with the items it already knows."
                : "Without this list the editor knows fewer items.";
            OldLinkBox.Text = result.Link ?? "";
        }

        private void NewLink_Click(object sender, RoutedEventArgs e)
        {
            Choice = Fix.NewLink;
            DialogResult = true;
        }

        private void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            Choice = Fix.File;
            DialogResult = true;
        }

        private void Forum_Click(object sender, RoutedEventArgs e) => ListProblems.OpenInBrowser(ListProblems.ForumLink);
    }
}
