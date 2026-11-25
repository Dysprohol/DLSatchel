using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Windows;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DarkTitleBar.RegisterForAllWindows();
            this.DispatcherUnhandledException += (s, args) =>
            {
                Debug.WriteLine($"UNHANDLED: {args.Exception}");
                MessageBox.Show($"Something went wrong: {args.Exception.Message}\n\nDetails:\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
                Shutdown(-1);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Debug.WriteLine($"DOMAIN UNHANDLED: {ex}");
                MessageBox.Show($"Something went wrong: {ex?.Message}\n\nDetails:\n{ex?.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}