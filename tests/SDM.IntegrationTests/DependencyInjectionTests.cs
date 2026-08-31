using Microsoft.Extensions.DependencyInjection;
using SDM.Application.ApplicationInfo;
using SDM.Desktop;
using SDM.Desktop.ViewModels;

namespace SDM.IntegrationTests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void CompositionRoot_BuildsAndResolvesRequiredServices()
    {
        using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();

        IApplicationInfoService applicationInfo = provider.GetRequiredService<IApplicationInfoService>();
        MainWindowViewModel viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Equal("SDM", applicationInfo.Name);
        Assert.Equal("Speed Download Manager", viewModel.FullName);
        Assert.Equal("Project foundation initialized", viewModel.FoundationStatus);
    }

    [Fact]
    public void CompositionRoot_CanBeCreatedWithoutDesktopLifetime()
    {
        Exception? exception = Record.Exception(() =>
        {
            using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();
            _ = provider.GetRequiredService<IApplicationInfoService>();
        });

        Assert.Null(exception);
    }
}
