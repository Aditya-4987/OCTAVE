# Octave.Desktop

This is the presentation and user interface layer of the OCTAVE media player. It is built as a native Windows 11 desktop app using WinUI 3 and the Windows App SDK, targeting .NET 10.0.

## Directory Structure

- **[Assets/](./Assets)**: Graphic resources, splash screen art, default icon files, and baseline test audio clips.
- **[Properties/](./Properties)**: Standard project build properties and launch profiles.
- **[ViewModels/](./ViewModels)**: Host ViewModels built using the `CommunityToolkit.Mvvm` framework:
  - [ShellViewModel](./ViewModels/ShellViewModel.cs): Global singleton ViewModel tracking core player properties (title, artist, state, position, volume, and repeat/shuffle states), marshaling unmanaged events onto the UI dispatcher thread, and containing the Static Fire Harness diagnostics loop.
  - [LibraryViewModel](./ViewModels/LibraryViewModel.cs) / [AlbumsViewModel](./ViewModels/AlbumsViewModel.cs) / [ArtistsViewModel](./ViewModels/ArtistsViewModel.cs): Transient page ViewModels that query the core database on page load and expose bindable collections.
- **[Views/](./Views)**: XAML pages containing visual element hierarchies and code-behind routing:
  - [LibraryPage](./Views/LibraryPage.xaml): Lists all library songs using virtualized lists with dynamic column spacing, duration mapping (mm:ss), and double-click playback triggers.
  - [AlbumsPage](./Views/AlbumsPage.xaml): Grid of album covers with grid virtualization.
  - [ArtistsPage](./Views/ArtistsPage.xaml): Grid of artist profile cards.
  - [SettingsPage](./Views/SettingsPage.xaml): Diagnostics log window and database initialization/static-fire harness launcher.
- [App.xaml.cs](./App.xaml.cs): Bootstrapper class executing zero-IO synchronous startup sequences and configuring the host DI container (`Microsoft.Extensions.DependencyInjection`).
- [MainWindow.xaml](./MainWindow.xaml) / [MainWindow.xaml.cs](./MainWindow.xaml.cs): Primary shell hosting the WinUI 3 `NavigationView` sidebar chassis, page transition `Frame`, and the bottom sticky Playback Dashboard Bar. Implements custom pointer event capturing to achieve throttled drag seeking and accessibility keyboard step seek listeners.
