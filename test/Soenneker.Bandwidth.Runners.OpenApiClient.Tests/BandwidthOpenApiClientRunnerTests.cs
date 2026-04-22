using Soenneker.Tests.HostedUnit;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class BandwidthOpenApiClientRunnerTests : HostedUnitTest
{
    public BandwidthOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
