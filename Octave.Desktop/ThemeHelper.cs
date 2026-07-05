using Microsoft.UI.Xaml;
using Windows.Storage;

namespace Octave_Desktop;

// Persists and applies the app theme (System / Light / Dark) via LocalSettings.
public static class ThemeHelper
{
    private const string ThemeKey = "AppTheme";

    public static ElementTheme GetSavedTheme()
    {
        var value = ApplicationData.Current.LocalSettings.Values[ThemeKey] as string;
        return value switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public static void SaveTheme(ElementTheme theme)
    {
        ApplicationData.Current.LocalSettings.Values[ThemeKey] = theme.ToString();
    }

    // Applies the theme to the window's root element (themes the whole window).
    public static void Apply(FrameworkElement? root, ElementTheme theme)
    {
        if (root != null)
        {
            root.RequestedTheme = theme;
        }
    }
}
