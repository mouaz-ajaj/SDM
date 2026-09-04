namespace SDM.Desktop.Services;

/// <summary>
/// The windows SDM opens over its own. Behind an interface because the view model has to
/// be able to ask a question without owning a window — and because "does deleting this
/// file ask first?" is a rule worth holding to in a test, which a concrete window cannot
/// answer.
/// </summary>
public interface IAppDialogs
{
    /// <summary>
    /// Asks before something that cannot be undone. <paramref name="confirmLabel"/> names
    /// the act rather than agreeing with the question, so the button reads on its own.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    Task ShowSettingsAsync();
}
