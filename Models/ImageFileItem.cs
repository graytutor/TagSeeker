using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace CustomImageViewer.Models;

public sealed class ImageFileItem(string fullPath, bool isDirectory, bool isImage) : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _hasDecodeError;
    private string _tagsText = string.Empty;
    private DateTime? _dateModified;
    private DateTime? _dateCreated;
    private long? _sizeBytes;
    private bool _sizeLoaded;
    private string? _displayName;

    public string FullPath { get; } = fullPath;
    public string FileName => Path.GetFileName(FullPath);
    public string DisplayName
    {
        get => _displayName ?? FileName;
        set { _displayName = value; OnPropertyChanged(); }
    }
    public bool IsDirectory { get; } = isDirectory;
    public bool IsImage { get; } = isImage;
    public DateTime DateModified => _dateModified ??= GetDateModified(FullPath);
    public DateTime DateCreated => _dateCreated ??= GetDateCreated(FullPath);
    public long? SizeBytes
    {
        get
        {
            if (!_sizeLoaded)
            {
                _sizeBytes = GetSize(FullPath, IsDirectory);
                _sizeLoaded = true;
            }
            return _sizeBytes;
        }
    }
    public string SortType => IsDirectory ? "폴더" : Path.GetExtension(FullPath).TrimStart('.');
    public string PlaceholderGlyph => IsDirectory ? "📁" : IsImage ? "🖼" : "📄";
    public string BadgeGlyph => IsDirectory && Thumbnail is not null ? "📁" : string.Empty;
    public string TypeLabel => IsDirectory
        ? "폴더"
        : IsImage
            ? Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant()
            : string.IsNullOrWhiteSpace(Path.GetExtension(FullPath))
                ? "파일"
                : $"{Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant()} 파일";

    public string TagsText
    {
        get => _tagsText;
        set { _tagsText = value; OnPropertyChanged(); OnPropertyChanged(nameof(TagsDisplay)); }
    }

    public string TagsDisplay => string.IsNullOrWhiteSpace(TagsText) ? string.Empty : $"# {TagsText.Replace(", ", "   # ")}";

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BadgeGlyph));
        }
    }

    public bool HasDecodeError
    {
        get => _hasDecodeError;
        set { _hasDecodeError = value; OnPropertyChanged(); }
    }

    public bool ThumbnailLoadStarted { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static DateTime GetDateModified(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    private static DateTime GetDateCreated(string path)
    {
        try { return File.GetCreationTime(path); }
        catch { return DateTime.MinValue; }
    }

    private static long? GetSize(string path, bool isDirectory)
    {
        if (isDirectory) return null;
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }
}
