using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DDV_SaveEditor_G
{
    /// <summary>
    /// Paints the native title bar in the app's night palette. Windows 10 gets the dark
    /// title bar; Windows 11 also takes the exact caption, text and border colours.
    /// Older systems ignore the attributes and keep their default frame.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        // COLORREF is 0x00BBGGRR.
        private const int CaptionColor = 0x0030170C; // #0C1730, top of the window gradient
        private const int TextColor = 0x00E6F0F4;    // #F4F0E6, Starlight
        private const int BorderColor = 0x007F5740;  // #40577F, Line

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Applies the colours to every window the app opens, including dialogs made in code.</summary>
        public static void RegisterForAllWindows()
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => Apply((Window)sender)));
        }

        private static void Apply(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            Set(hwnd, DWMWA_CAPTION_COLOR, CaptionColor);
            Set(hwnd, DWMWA_TEXT_COLOR, TextColor);
            Set(hwnd, DWMWA_BORDER_COLOR, BorderColor);
        }

        private static void Set(IntPtr hwnd, int attribute, int value)
            => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
    }
}
