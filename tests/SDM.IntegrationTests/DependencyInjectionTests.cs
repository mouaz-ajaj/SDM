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
    public async Task CompositionRoot_BuildsAndResolvesRequiredServices()
    {
        await using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();

        IApplicationInfoService applicationInfo = provider.GetRequiredService<IApplicationInfoService>();
        MainWindowViewModel viewModel = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Equal("SDM", applicationInfo.Name);
        Assert.Equal("Speed Download Manager", viewModel.FullName);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Version));
    }

    [Fact]
    public async Task CompositionRoot_WiresTheDownloadPipelineEndToEnd()
    {
        await using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDownloadEngine>());
        Assert.NotNull(provider.GetRequiredService<IStartDownloadUseCase>());
        Assert.False(string.IsNullOrWhiteSpace(provider.GetRequiredService<IDownloadFolder>().GetPath()));
    }

    [Fact]
    public async Task CompositionRoot_CanBeCreatedAndDisposedWithoutADesktopLifetime()
    {
        // Disposal is part of the contract, not an afterthought: a container holding an
        // async-disposable service throws if it is torn down the synchronous way, and
        // that tear-down happens on every single exit.
        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            await using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider();
            _ = provider.GetRequiredService<IApplicationInfoService>();
        });

        Assert.Null(exception);
    }
}
