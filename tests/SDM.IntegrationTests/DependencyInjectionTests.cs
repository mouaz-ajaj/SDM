using Microsoft.Extensions.DependencyInjection;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Core.Downloads;
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
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Version));
    }

    [Fact]
    public void CompositionRoot_WiresTheDownloadPipelineEndToEnd()
    {
        using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDownloadEngine>());
        Assert.NotNull(provider.GetRequiredService<IStartDownloadUseCase>());
        Assert.False(string.IsNullOrWhiteSpace(provider.GetRequiredService<IDownloadFolder>().GetPath()));
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
