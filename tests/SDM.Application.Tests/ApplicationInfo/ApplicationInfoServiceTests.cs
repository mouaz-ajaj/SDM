using Microsoft.Extensions.Options;
using SDM.Application.ApplicationInfo;

namespace SDM.Application.Tests.ApplicationInfo;

public sealed class ApplicationInfoServiceTests
{
    [Fact]
    public void Service_ReturnsConfiguredProductIdentity()
    {
        ApplicationInfoOptions options = new()
        {
            Name = "SDM",
            FullName = "Speed Download Manager",
        };
        ApplicationInfoService service = new(Options.Create(options));

        Assert.Equal("SDM", service.Name);
        Assert.Equal("Speed Download Manager", service.FullName);
        Assert.False(string.IsNullOrWhiteSpace(service.Version));
    }
}
