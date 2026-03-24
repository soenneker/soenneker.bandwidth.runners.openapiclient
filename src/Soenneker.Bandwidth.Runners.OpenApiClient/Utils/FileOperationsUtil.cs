using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Soenneker.Bandwidth.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Extensions.ValueTask;
using Soenneker.Git.Util.Abstract;
using Soenneker.OpenApi.Fixer;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.Process.Abstract;
using Soenneker.Utils.Yaml.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Bandwidth.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IProcessUtil _processUtil;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IBandwidthOpenApiCrawler _bandwidthOpenApiCrawler;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IYamlUtil _yamlUtil;
    private readonly IOpenApiFixer _openApiFixer;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IProcessUtil processUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IBandwidthOpenApiCrawler bandwidthOpenApiCrawler,
        IOpenApiMerger openApiMerger, IYamlUtil yamlUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _processUtil = processUtil;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _bandwidthOpenApiCrawler = bandwidthOpenApiCrawler;
        _openApiMerger = openApiMerger;
        _yamlUtil = yamlUtil;
        _openApiFixer = openApiFixer;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string openApiFilePath = Path.Combine(gitDirectory, "openapi.json");
        string downloadedOpenApiDirectory = Path.Combine(gitDirectory, "openapi");
        string downloadedJsonOpenApiDirectory = Path.Combine(gitDirectory, "openapi-json");

        await _fileUtil.DeleteIfExists(openApiFilePath, cancellationToken: cancellationToken);
        await _directoryUtil.Create(downloadedOpenApiDirectory, false, cancellationToken);
        await _directoryUtil.Create(downloadedJsonOpenApiDirectory, false, cancellationToken);

        await _fileUtil.DeleteAll(downloadedOpenApiDirectory, true, cancellationToken);
        await _fileUtil.DeleteAll(downloadedJsonOpenApiDirectory, true, cancellationToken);

        List<string> openApiDocumentUris = await GetOpenApiDocumentUris(cancellationToken).ConfigureAwait(false);
        List<string> downloadedFilePaths = await _fileDownloadUtil.DownloadMultiple(downloadedOpenApiDirectory, openApiDocumentUris, 4, cancellationToken)
                                                                  .ConfigureAwait(false);

        if (downloadedFilePaths.Count == 0)
            throw new InvalidOperationException("No Bandwidth OpenAPI documents were downloaded.");

        List<string> jsonFilePaths = await ConvertDownloadedOpenApiFilesToJson(downloadedOpenApiDirectory, downloadedJsonOpenApiDirectory, downloadedFilePaths, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Downloaded {DownloadCount} Bandwidth OpenAPI documents and converted {JsonCount} documents to JSON. Merging into a single OpenAPI document...",
            downloadedFilePaths.Count, jsonFilePaths.Count);

        OpenApiDocument mergedOpenApiDocument = await _openApiMerger.MergeDirectory(downloadedJsonOpenApiDirectory, cancellationToken).ConfigureAwait(false);
        string mergedOpenApiJson = _openApiMerger.ToJson(mergedOpenApiDocument);

        await _fileUtil.Write(openApiFilePath, mergedOpenApiJson, true, cancellationToken).ConfigureAwait(false);

        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");

        await _openApiFixer.Fix(openApiFilePath, fixedFilePath, cancellationToken);

        await _processUtil.Start("dotnet", null, "tool update --global Microsoft.OpenApi.Kiota", waitForExit: true, cancellationToken: cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _processUtil.Start("kiota", gitDirectory, $"kiota generate -l CSharp -d \"{fixedFilePath}\" -o src/{Constants.Library} -c BandwidthOpenApiClient -n {Constants.Library}",
            waitForExit: true, cancellationToken: cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private async ValueTask<List<string>> GetOpenApiDocumentUris(CancellationToken cancellationToken)
    {
        string? configuredOpenApiDocumentUrl = _configuration["Bandwidth:ClientGenerationUrl"];

        if (!configuredOpenApiDocumentUrl.IsNullOrWhiteSpace())
        {
            _logger.LogInformation("Using configured Bandwidth OpenAPI document URL: {OpenApiDocumentUrl}", configuredOpenApiDocumentUrl);
            return [configuredOpenApiDocumentUrl!];
        }

        List<string> discoveredOpenApiLinks = await _bandwidthOpenApiCrawler.GetOpenApiLinks(cancellationToken).ConfigureAwait(false);

        if (discoveredOpenApiLinks.Count > 0)
        {
            _logger.LogInformation("Discovered {Count} Bandwidth OpenAPI document URLs.", discoveredOpenApiLinks.Count);
            return discoveredOpenApiLinks;
        }

        throw new InvalidOperationException("Could not discover any Bandwidth OpenAPI spec files. The Bandwidth docs crawl may have returned no API/spec links, or Bandwidth:ClientGenerationUrl may need to be configured.");
    }

    private async ValueTask<List<string>> ConvertDownloadedOpenApiFilesToJson(string sourceDirectory, string targetDirectory, List<string> downloadedFilePaths,
        CancellationToken cancellationToken)
    {
        var convertedFilePaths = new List<string>(downloadedFilePaths.Count);

        foreach (string downloadedFilePath in downloadedFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(sourceDirectory, downloadedFilePath);
            string targetJsonPath = Path.Combine(targetDirectory, Path.ChangeExtension(relativePath, ".json"));
            string? targetJsonDirectory = Path.GetDirectoryName(targetJsonPath);

            if (!string.IsNullOrWhiteSpace(targetJsonDirectory))
                await _directoryUtil.Create(targetJsonDirectory, false, cancellationToken).ConfigureAwait(false);

            string extension = Path.GetExtension(downloadedFilePath);

            if (extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                await _yamlUtil.SaveAsJson(downloadedFilePath, targetJsonPath, true, cancellationToken).ConfigureAwait(false);
                convertedFilePaths.Add(targetJsonPath);
                continue;
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                await _fileUtil.Copy(downloadedFilePath, targetJsonPath, true, cancellationToken).ConfigureAwait(false);
                convertedFilePaths.Add(targetJsonPath);
                continue;
            }

            _logger.LogWarning("Skipping unsupported OpenAPI file extension for '{FilePath}'", downloadedFilePath);
        }

        if (convertedFilePaths.Count == 0)
            throw new InvalidOperationException("No Bandwidth OpenAPI documents were converted to JSON.");

        return convertedFilePaths;
    }


    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, "Jake Soenneker", "jake@soenneker.com", cancellationToken);
    }
}
