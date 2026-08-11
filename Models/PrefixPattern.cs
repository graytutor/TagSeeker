using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CustomImageViewer.Models;

public sealed class PrefixPattern : INotifyPropertyChanged
{
    private string _opening = string.Empty;
    private string _closing = string.Empty;

    public PrefixPattern()
    {
    }

    public PrefixPattern(string opening, string closing)
    {
        _opening = opening;
        _closing = closing;
    }

    public string Opening
    {
        get => _opening;
        set
        {
            if (_opening == value) return;
            _opening = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Example));
        }
    }

    public string Closing
    {
        get => _closing;
        set
        {
            if (_closing == value) return;
            _closing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Example));
        }
    }

    [JsonIgnore]
    public string Example => $"{Opening}내용{Closing}";

    public PrefixPattern Clone() => new(Opening, Closing);

    public static List<PrefixPattern> CreateDefaults() =>
    [
        new("[", "]"), new("【", "】"),
        new("(", ")"), new("（", "）"),
        new("{", "}"), new("｛", "｝"),
        new("<", ">"), new("〈", "〉"), new("《", "》"),
        new("「", "」"), new("『", "』"),
        new("*", "*"), new("**", "**")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
