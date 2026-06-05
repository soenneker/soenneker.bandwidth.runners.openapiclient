using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;

/// <summary>
/// Defines the bandwidth open api crawler contract.
/// </summary>
public interface IBandwidthOpenApiCrawler
{
    /// <summary>
    /// Gets open api links.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the result of the operation.</returns>
    ValueTask<List<string>> GetOpenApiLinks(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets open api links.
    /// </summary>
    /// <param name="outputPath">The output path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the result of the operation.</returns>
    ValueTask<List<string>> GetOpenApiLinks(string outputPath, CancellationToken cancellationToken = default);
}
