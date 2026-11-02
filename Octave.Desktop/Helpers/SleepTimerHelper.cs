using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Octave_Desktop.Helpers;

public static class SleepTimerHelper
{
    /// <summary>
    /// Generates a circular countdown arc geometry with clockwise reduction.
    /// At fraction = 1.0, returns a full 360-degree circle.
    /// As fraction reduces (1.0 -> 0.0), the gap advances clockwise from 12 o'clock,
    /// so the active remaining arc reduces clockwise.
    /// </summary>
    public static Geometry CreateCountdownArcGeometry(double fraction, double size, double radius)
    {
        var calc = Octave.Core.Helpers.SleepTimerCalculations.CalculateArcGeometryPoints(fraction, size, radius);

        if (calc.IsFullCircle)
        {
            return new EllipseGeometry
            {
                Center = new Point(size / 2.0, size / 2.0),
                RadiusX = calc.RadiusX,
                RadiusY = calc.RadiusY
            };
        }

        if (calc.IsEmpty)
        {
            return new PathGeometry();
        }

        var figure = new PathFigure
        {
            StartPoint = new Point(calc.StartX, calc.StartY),
            IsClosed = false
        };

        var arc = new ArcSegment
        {
            Point = new Point(calc.EndX, calc.EndY),
            Size = new Size(calc.RadiusX, calc.RadiusY),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = calc.IsLargeArc
        };

        figure.Segments.Add(arc);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Displays a custom sleep timer duration dialog with inline number input and quick preset chips.
    /// Returns the chosen duration in minutes, or null if cancelled.
    /// </summary>
    public static async Task<int?> ShowCustomSleepTimerDialogAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot == null) return null;

        var numberBox = new NumberBox
        {
            Value = 20,
            Minimum = 1,
            Maximum = 720,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 5,
            LargeChange = 15,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var quickStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0)
        };

        int[] quickPresets = [10, 20, 25, 40, 50, 90];
        foreach (var p in quickPresets)
        {
            var btn = new Button
            {
                Content = $"{p}m",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11
            };
            btn.Click += (_, _) => numberBox.Value = p;
            quickStack.Children.Add(btn);
        }

        var contentPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            MinWidth = 280
        };

        contentPanel.Children.Add(new TextBlock
        {
            Text = "Enter duration in minutes (1 to 720):",
            FontSize = 13
        });
        contentPanel.Children.Add(numberBox);
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Quick presets:",
            FontSize = 11,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0)
        });
        contentPanel.Children.Add(quickStack);

        var dialog = new ContentDialog
        {
            Title = "Custom Sleep Timer",
            Content = contentPanel,
            PrimaryButtonText = "Start Timer",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                int mins = (int)Math.Clamp(numberBox.Value, 1, 720);
                return mins;
            }
        }
        catch
        {
            // Ignore if another dialog is already showing or window is closing
        }

        return null;
    }

    /// <summary>
    /// Displays an explicit warning confirmation dialog before setting a power-related completion action
    /// (Sleep PC, Shut down PC, Sign out), advising the user to save unsaved work.
    /// Returns true if the user confirmed, false otherwise.
    /// </summary>
    public static async Task<bool> ShowPowerActionWarningDialogAsync(XamlRoot xamlRoot, Octave.Core.Models.SleepTimerCompletionAction action)
    {
        if (xamlRoot == null) return false;

        string? warningMessage = Octave.Core.Helpers.SleepTimerCalculations.GetCompletionActionWarningText(action);
        if (string.IsNullOrEmpty(warningMessage))
        {
            return true; // No warning needed for StopPlayback
        }

        string actionTitle = Octave.Core.Helpers.SleepTimerCalculations.FormatCompletionAction(action);

        var contentPanel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 300,
            MaxWidth = 420
        };

        contentPanel.Children.Add(new TextBlock
        {
            Text = warningMessage,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20
        });

        var dialog = new ContentDialog
        {
            Title = $"Confirm '{actionTitle}' on Expiry",
            Content = contentPanel,
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, // Defensive: default to Cancel so accidental Enter doesn't confirm
            XamlRoot = xamlRoot
        };

        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}
