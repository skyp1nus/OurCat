using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OurCut.Core.Serialization;

namespace OurCut.App.Services;

/// <summary><see cref="IFileDialogs"/> on top of Avalonia's storage provider.</summary>
public sealed class StorageFileDialogs(TopLevel topLevel) : IFileDialogs
{
    private static readonly FilePickerFileType Videos = new("Video files")
    {
        Patterns = ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.m4v", "*.avi", "*.ts", "*.mts", "*.m2ts", "*.mpg", "*.mpeg", "*.flv", "*.wmv"],
        MimeTypes = ["video/*"],
    };

    private static readonly FilePickerFileType Projects = new("OurCut project")
    {
        Patterns = ["*" + ProjectFile.Extension],
        MimeTypes = ["application/json"],
    };

    public async Task<string?> PickMediaToOpenAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = [Videos, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectToOpenAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = [Projects],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProjectSavePathAsync(string suggestedFileName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "ourcut.json",
            FileTypeChoices = [Projects],
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }
}
