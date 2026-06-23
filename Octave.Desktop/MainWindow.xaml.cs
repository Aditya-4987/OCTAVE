using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;

namespace Octave_Desktop;

public sealed partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }
}