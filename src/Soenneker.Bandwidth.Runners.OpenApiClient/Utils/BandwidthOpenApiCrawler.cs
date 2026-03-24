using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Playwright.Installation.Abstract;
using Soenneker.Playwrights.Extensions.Stealth;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Utils;

/// <summary>
/// Crawls Bandwidth API docs and extracts OpenAPI spec download links from each API page.
/// </summary>
/// <inheritdoc cref="IBandwidthOpenApiCrawler"/>
public sealed class BandwidthOpenApiCrawler : IBandwidthOpenApiCrawler
{
    private const string _startUrl = "https://dev.bandwidth.com/apis/";
    private const int _navigationTimeoutMs = 30_000;

    private static readonly Uri _baseUri = new(_startUrl);

    private readonly ILogger<BandwidthOpenApiCrawler> _logger;
    private readonly IPlaywrightInstallationUtil _playwrightInstallationUtil;

    public BandwidthOpenApiCrawler(ILogger<BandwidthOpenApiCrawler> logger, IPlaywrightInstallationUtil playwrightInstallationUtil)
    {
        _logger = logger;
        _playwrightInstallationUtil = playwrightInstallationUtil;
    }

    public async ValueTask<List<string>> GetOpenApiLinks(CancellationToken cancellationToken = default)
    {
        await _playwrightInstallationUtil.EnsureInstalled(cancellationToken)
                                         .ConfigureAwait(false);

        using IPlaywright playwright = await Microsoft.Playwright.Playwright.CreateAsync()
                                                      .ConfigureAwait(false);

        await using IBrowser browser = await playwright.LaunchStealthChromium(new BrowserTypeLaunchOptions
                                                       {
                                                           Headless = true
                                                       })
                                                       .ConfigureAwait(false);

        await using IBrowserContext context = await browser.CreateStealthContext(new BrowserNewContextOptions())
                                                           .ConfigureAwait(false);

        await ConfigureRequestBlocking(context)
            .ConfigureAwait(false);

        HashSet<string> links = await CrawlAsync(context, cancellationToken)
            .ConfigureAwait(false);

        return links.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    public async ValueTask<List<string>> GetOpenApiLinks(string outputPath, CancellationToken cancellationToken = default)
    {
        List<string> links = await GetOpenApiLinks(cancellationToken)
            .ConfigureAwait(false);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllLinesAsync(outputPath, links, cancellationToken)
                  .ConfigureAwait(false);

        return links;
    }

    private async Task<HashSet<string>> CrawlAsync(IBrowserContext context, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        queue.Enqueue(_startUrl);
        queued.Add(_startUrl);

        IPage page = await context.NewPageAsync()
                                  .ConfigureAwait(false);

        try
        {
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentUrl = queue.Dequeue();

                if (!visited.Add(currentUrl))
                    continue;

                _logger.LogInformation("Visiting {Url}", currentUrl);

                if (!await TryNavigate(page, currentUrl).ConfigureAwait(false))
                    continue;

                IReadOnlyList<string> hrefs = await GetAllAnchorHrefs(page).ConfigureAwait(false);

                foreach (string href in hrefs)
                {
                    string? normalized = NormalizeUrl(href);

                    if (normalized is null)
                        continue;

                    if (IsBandwidthApisPage(normalized) && queued.Add(normalized))
                    {
                        queue.Enqueue(normalized);
                        _logger.LogInformation("Queued child API page: {Url}", normalized);
                        continue;
                    }

                    if (IsOpenApiSpecLink(normalized) && discoveredSpecs.Add(normalized))
                    {
                        _logger.LogInformation("Found OpenAPI spec directly in anchors: {SpecLink} (from {PageUrl})", normalized, currentUrl);
                    }
                }

                if (!IsLikelyLeafApiPage(currentUrl))
                {
                    _logger.LogDebug("Skipping spec extraction for non-leaf page: {PageUrl}", currentUrl);
                    continue;
                }

                string? specLink = await TryExtractDownloadSpecLink(page).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(specLink))
                {
                    if (discoveredSpecs.Add(specLink))
                    {
                        _logger.LogInformation("✅ Found OpenAPI spec: {SpecLink} (from {PageUrl})", specLink, currentUrl);
                    }
                    else
                    {
                        _logger.LogDebug("Duplicate OpenAPI spec ignored: {SpecLink} (from {PageUrl})", specLink, currentUrl);
                    }
                }
                else
                {
                    _logger.LogWarning("❌ No OpenAPI spec found on leaf page: {PageUrl}", currentUrl);
                }
            }
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
        }

        return discoveredSpecs;
    }

    private async Task<bool> TryNavigate(IPage page, string url)
    {
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
                      {
                          WaitUntil = WaitUntilState.DOMContentLoaded,
                          Timeout = _navigationTimeoutMs
                      })
                      .ConfigureAwait(false);

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

            return true;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Timed out navigating to {Url}", url);
            return false;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Failed navigating to {Url}", url);
            return false;
        }
    }

    private static bool IsLikelyLeafApiPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        string path = uri.AbsolutePath.Trim('/');

        if (!path.StartsWith("apis/", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /apis/numbers-apis/ => 2 segments after trimming => category/index page
        // /apis/numbers-apis/line-features/ => 3 segments => likely leaf doc page
        return segments.Length >= 3;
    }

    private static async Task<string?> TryExtractDownloadSpecLink(IPage page)
    {
        // Primary: direct selector for the explicit OpenAPI download link on Redoc pages
        ILocator explicitDownloadLink = page.Locator("a[href*='/spec/'][download]")
                                            .First;

        if (await explicitDownloadLink.CountAsync()
                                      .ConfigureAwait(false) > 0)
        {
            string? href = await explicitDownloadLink.GetAttributeAsync("href")
                                                     .ConfigureAwait(false);
            string? normalized = NormalizeUrl(href);

            if (IsOpenApiSpecLink(normalized))
                return normalized;
        }

        // Fallback: any /spec/ link whose visible text is Download
        ILocator downloadTextLink = page.Locator("a[href*='/spec/']")
                                        .Filter(new LocatorFilterOptions
                                        {
                                            HasTextString = "Download"
                                        })
                                        .First;

        if (await downloadTextLink.CountAsync()
                                  .ConfigureAwait(false) > 0)
        {
            string? href = await downloadTextLink.GetAttributeAsync("href")
                                                 .ConfigureAwait(false);
            string? normalized = NormalizeUrl(href);

            if (IsOpenApiSpecLink(normalized))
                return normalized;
        }

        // Last fallback: scan anchors in-page
        IReadOnlyList<string> hrefs = await GetAllAnchorHrefs(page)
            .ConfigureAwait(false);

        foreach (string href in hrefs)
        {
            string? normalized = NormalizeUrl(href);

            if (IsOpenApiSpecLink(normalized))
                return normalized;
        }

        return null;
    }

    private static async Task ConfigureRequestBlocking(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
                     {
                         IRequest request = route.Request;

                         if (ShouldBlockRequest(request))
                         {
                             await route.AbortAsync()
                                        .ConfigureAwait(false);
                             return;
                         }

                         await route.ContinueAsync()
                                    .ConfigureAwait(false);
                     })
                     .ConfigureAwait(false);
    }

    private static bool ShouldBlockRequest(IRequest request)
    {
        string resourceType = request.ResourceType;

        if (resourceType.Equals("image", StringComparison.OrdinalIgnoreCase) || resourceType.Equals("media", StringComparison.OrdinalIgnoreCase) ||
            resourceType.Equals("font", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string url = request.Url;

        return url.Contains("google-analytics", StringComparison.OrdinalIgnoreCase) || url.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("doubleclick", StringComparison.OrdinalIgnoreCase) || url.Contains("segment", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("amplitude", StringComparison.OrdinalIgnoreCase) || url.Contains("mixpanel", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("hotjar", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>> GetAllAnchorHrefs(IPage page)
    {
        IReadOnlyList<string?> hrefs = await page.Locator("a[href]")
                                                 .EvaluateAllAsync<string?[]>("""
                                                                              elements => elements.map(e => e.getAttribute("href"))
                                                                              """)
                                                 .ConfigureAwait(false);

        if (hrefs.Count == 0)
            return Array.Empty<string>();

        var results = new List<string>(hrefs.Count);

        foreach (string? href in hrefs)
        {
            if (!string.IsNullOrWhiteSpace(href))
                results.Add(href);
        }

        return results;
    }

    private static string? NormalizeUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = href.Trim();

        if (href.Length == 0)
            return null;

        if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute))
            return absolute.ToString();

        if (Uri.TryCreate(_baseUri, href, out Uri? combined))
            return combined.ToString();

        return null;
    }

    private static bool IsBandwidthApisPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (!string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.AbsolutePath.StartsWith("/apis/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenApiSpecLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (!string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        string path = uri.AbsolutePath;

        if (!path.StartsWith("/spec/", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }
}