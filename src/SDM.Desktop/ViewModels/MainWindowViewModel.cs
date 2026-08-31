using CommunityToolkit.Mvvm.ComponentModel;
using SDM.Application.ApplicationInfo;

namespace SDM.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationInfoService _applicationInfo;

    public MainWindowViewModel(IApplicationInfoService applicationInfo)
    {
        _applicationInfo = applicationInfo ?? throw new ArgumentNullException(nameof(applicationInfo));
    }

    public string Name => _applicationInfo.Name;

    public string FullName => _applicationInfo.FullName;

    public string Version => $"Version {_applicationInfo.Version}";

    public string FoundationStatus => "Project foundation initialized";

    public string NextStageMessage => "The basic HTTP download engine will be implemented in Stage 2.";
}
