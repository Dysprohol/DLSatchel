using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// The editor's own message window: its colours, English buttons whatever the Windows language,
    /// and text people can select and copy into a bug report.
    /// </summary>
    public partial class DialogBox : Window
    {
        private MessageBoxResult _result;

        private DialogBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            InitializeComponent();
            Title = string.IsNullOrWhiteSpace(caption) ? "DL Satchel" : caption;
            MessageText.Text = text;

            (string glyph, string brush)? look = icon switch
            {
                MessageBoxImage.Error => ("", "#E58A9A"),
                MessageBoxImage.Warning => ("", "#F2C96D"),
                MessageBoxImage.Question => ("", "#9EE6C8"),
                MessageBoxImage.Information => ("", "#9EE6C8"),
                _ => null
            };
            if (look is { } l)
            {
                Glyph.Text = l.glyph;
                Glyph.Foreground = (Brush)new BrushConverter().ConvertFromString(l.brush)!;
                Glyph.Visibility = Visibility.Visible;
            }

            var choices = buttons switch
            {
                MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
                MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
                MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
                _ => new[] { MessageBoxResult.OK }
            };
            // Closing the window means the safe answer: Cancel, else No, else OK.
            _result = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                    : choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No
                    : MessageBoxResult.OK;

            foreach (var choice in choices)
            {
                var button = new Button
                {
                    Content = choice.ToString(),
                    MinWidth = 86,
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = choice == choices[0],
                    IsCancel = choice == _result
                };
                if (choice == choices[0])
                    button.Style = (Style)FindResource("AccentButton");
                button.Click += (_, _) => { _result = choice; DialogResult = true; };
                Buttons.Children.Add(button);
            }
            Loaded += (_, _) => (Buttons.Children[0] as Button)?.Focus();
        }

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon,
                                            Window? owner = null)
        {
            var app = Application.Current;
            if (app == null || !app.Dispatcher.CheckAccess())
                return System.Windows.MessageBox.Show(text, caption, buttons, icon);

            var dialog = new DialogBox(text, caption, buttons, icon);
            owner = owner is { IsVisible: true } ? owner
                    : app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w != dialog)
                      ?? (app.MainWindow is { IsVisible: true } main ? main : null);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog._result;
        }
    }

    /// <summary>
    /// Every MessageBox.Show in the editor goes through the editor's own window. Being in this
    /// namespace, it is found before System.Windows.MessageBox, so no call site had to change.
    /// </summary>
    internal static class MessageBox
    {
        public static MessageBoxResult Show(string text) =>
            DialogBox.Show(text, "", MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption) =>
            DialogBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) =>
            DialogBox.Show(text, caption, buttons, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
            DialogBox.Show(text, caption, buttons, icon);

        public static MessageBoxResult Show(Window? owner, string text) =>
            DialogBox.Show(text, "", MessageBoxButton.OK, MessageBoxImage.None, owner);

        public static MessageBoxResult Show(Window? owner, string text, string caption) =>
            DialogBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None, owner);

        public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons) =>
            DialogBox.Show(text, caption, buttons, MessageBoxImage.None, owner);

        public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
            DialogBox.Show(text, caption, buttons, icon, owner);
    }
}
