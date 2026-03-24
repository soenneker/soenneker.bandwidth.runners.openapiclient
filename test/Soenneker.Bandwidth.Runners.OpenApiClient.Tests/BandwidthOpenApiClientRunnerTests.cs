using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class BandwidthOpenApiClientRunnerTests : FixturedUnitTest
{
    public BandwidthOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
