using Microsoft.Extensions.DependencyInjection;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.Bandwidth.Runners.OpenApiClient.Utils;
using Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.OpenApi.Merger.Registrars;
using Soenneker.Playwright.Installation.Registrars;
using Soenneker.Utils.File.Download.Registrars;
using Soenneker.Utils.Yaml.Registrars;

namespace Soenneker.Bandwidth.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddScoped<IBandwidthOpenApiCrawler, BandwidthOpenApiCrawler>()
                .AddScoped<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsScoped()
                .AddFileDownloadUtilAsScoped()
                .AddOpenApiMergerAsScoped()
                .AddPlaywrightInstallationUtilAsScoped()
                .AddYamlUtilAsScoped()
                .AddOpenApiFixerAsScoped();

        return services;
    }
}