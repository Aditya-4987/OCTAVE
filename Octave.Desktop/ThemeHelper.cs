using Microsoft.UI.Xaml;
using Windows.Storage;

namespace Octave_Desktop;

// Enforces the app dark theme across the window.
public static class ThemeHelper
{
    public static ElementTheme GetSavedTheme()
    {
        return ElementTheme.Dark;
    }

    public static void SaveTheme(ElementTheme theme)
    {
        // Theme selection is disabled; app is locked to Dark Mode.
    }

    // Applies Dark theme to the window's root element.
    public static void Apply(FrameworkElement? root, ElementTheme theme)
    {
        if (root != null)
        {
            root.RequestedTheme = ElementTheme.Dark;
        }
    }
}
