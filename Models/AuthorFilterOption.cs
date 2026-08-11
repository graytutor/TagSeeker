using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CustomImageViewer.Models;

public sealed class AuthorFilterOption(string key, string name, int itemCount) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public int ItemCount { get; } = itemCount;
    public string DisplayText => $"{Name} ({ItemCount:N0})";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
