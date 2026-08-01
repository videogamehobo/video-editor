using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HighlightForge.Core.Domain;
using HighlightForge.Core.Persistence;

namespace HighlightForge.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void CreateProject_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the HighlightForge project",
            AllowMultiple = false
        });
        if (folder.Count == 0) return;

        var directory = Path.Combine(folder[0].Path.LocalPath, "Untitled.gheproj");
        var store = new ProjectStore(new ProjectPaths(directory));
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(ProjectDocument.Create("Untitled", now));
        StatusText.Text = $"Created a non-destructive project at {directory}";
    }
}
