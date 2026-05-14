using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LEPTA.Models;

public sealed class LeptaPanelState : INotifyPropertyChanged
{
    private string name = "Panel";
    private string customInstruction = string.Empty;
    private string response = string.Empty;
    private string status = "Idle";

    public Guid Id { get; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public string CustomInstruction
    {
        get => customInstruction;
        set => SetField(ref customInstruction, value);
    }

    public string Response
    {
        get => response;
        set => SetField(ref response, value);
    }

    public string Status
    {
        get => status;
        set => SetField(ref status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
