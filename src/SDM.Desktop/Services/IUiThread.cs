using Avalonia.Threading;

namespace SDM.Desktop.Services;

/// <summary>
/// Runs work where the interface can be touched.
///
/// Behind an interface because the download engine calls back from wherever it is
/// standing, and what those callbacks do — set observable properties, add to bound
/// collections — has to happen on one particular thread. Calling the dispatcher directly
/// made that untestable in the worst way: a test has no dispatcher pumping, so posted
/// work never ran, and every assertion about what a callback sets passed by never being
/// reached. The tests were green because the code under test had not run.
/// </summary>
public interface IUiThread
{
    /// <summary>
    /// Runs <paramref name="action"/> where the interface lives — at once when already
    /// there, so a callback is not reordered behind work the dispatcher has yet to reach.
    /// </summary>
    void Invoke(Action action);
}

/// <summary>The real one: Avalonia's dispatcher.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
