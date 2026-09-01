using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;

namespace SDM.Desktop.Services;

/// <summary>
/// Hands work to the desktop environment. Every call is a request to another program, so
/// every one of them can fail for reasons that are not faults in SDM — the file was moved,
/// no program is registered for the type, the shell refused — and a failure is reported
/// back rather than thrown at a user who only right-clicked a row.
/// </summary>
public sealed class SystemShell : ISystemShell
{
    private readonly ILogger<SystemShell> _logger;

    private TopLevel? _topLevel;

    public SystemShell(ILogger<SystemShell> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>The clipboard belongs to a window, so it arrives once the window is up.</summary>
    public void Attach(TopLevel topLevel) => _topLevel = topLevel;

    public bool Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            // Explorer's own syntax: the path is part of the /select argument, not a
            // separate one, which is why this is a string rather than an argument list.
            return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }

        // Nothing to select: an unfinished transfer is still a `.part` file under another
        // name, so the folder it is heading for is the useful thing to show.
        string? directory = Path.GetDirectoryName(path);

        return directory is { Length: > 0 }
            && Directory.Exists(directory)
            && Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    public async Task CopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_topLevel?.Clipboard is not { } clipboard)
        {
            _logger.LogWarning("Nothing was copied: the window has no clipboard yet.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception exception)
        {
            // The clipboard is shared with every other program on the machine and can be
            // held open by any of them. Losing a copy is not worth a crash.
            _logger.LogWarning(exception, "The clipboard refused the text.");
        }
    }

    private bool Start(ProcessStartInfo start)
    {
        try
        {
            using Process? started = Process.Start(start);
            return true;
        }
        catch (Exception exception)
            when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The shell refused to open {Target}.", start.FileName);
            return false;
        }
    }
}
