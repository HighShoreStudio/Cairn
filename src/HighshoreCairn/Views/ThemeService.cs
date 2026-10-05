using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HighshoreCairn.ViewModels;

namespace HighshoreCairn.Views;

/// <summary>
/// Light / dark theme switch. The colors live in Themes/Light.xaml and Themes/Dark.xaml:
/// swapping the first merged dictionary of the application updates every {DynamicResource Brush.*}.
/// </summary>
public class ThemeService : IThemeService
{
    public static bool IsDarkTheme { get; private set; }

    /// <summary>Raised after the theme changed (for things drawn in code, like the whiteboard).</summary>
    public static event Action? ThemeChanged;

    public bool IsDark => IsDarkTheme;

    public void Apply(bool dark)
    {
        var name = dark ? "Dark.xaml" : "Light.xaml";
        var colors = new ResourceDictionary
        {
            Source = new Uri($"/Cairn;component/Themes/{name}", UriKind.Relative)
        };
        Application.Current.Resources.MergedDictionaries[0] = colors;
        IsDarkTheme = dark;

        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        ThemeChanged?.Invoke();
    }

    /// <summary>Makes a window follow the theme with its title bar too (Windows 10 1809+ / Windows 11).</summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var value = IsDarkTheme ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 on the first Windows 10 builds that supported it)
            if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
            // Ask Windows to redraw the frame, otherwise the title bar changes only on the next activation.
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        catch
        {
            // Older Windows: the title bar simply keeps the system color.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
