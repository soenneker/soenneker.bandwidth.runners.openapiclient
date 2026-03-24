using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;

public interface IBandwidthOpenApiCrawler
{
    ValueTask<List<string>> GetOpenApiLinks(CancellationToken cancellationToken = default);

    ValueTask<List<string>> GetOpenApiLinks(string outputPath, CancellationToken cancellationToken = default);
}
