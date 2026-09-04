using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SDM.Desktop.ViewModels;

/// <summary>
/// A question with two answers, where one of them cannot be taken back.
///
/// The destructive button is named after what it does — "Delete file", never "OK" — so
/// the answer is readable without the sentence above it, and it is not the default: a
/// return key pressed out of habit must not destroy anything.
/// </summary>
public sealed partial class ConfirmViewModel : ObservableObject
{
    public ConfirmViewModel(string title, string message, string confirmLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);

        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
    }

    public string Title { get; }

    public string Message { get; }

    public string ConfirmLabel { get; }

    /// <summary>Set when the dialog closes. False for every way of dismissing it.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Raised when the dialog should close, either way.</summary>
    public event EventHandler? Closed;

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
