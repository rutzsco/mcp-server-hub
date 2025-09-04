using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using ModelContextProtocol.Server;

namespace mcp_server_hub.Tools;

public record BlobMetadataRequest(
    [property: Description("HTTPS URI of the blob (can include SAS token)")] string FileUri
);

public record BlobMetadataResult(
    [property: Description("Original blob URI provided")] string BlobUri,
    [property: Description("Storage account name")] string? AccountName,
    [property: Description("Container name")] string? Container,
    [property: Description("Blob name (path within container)")] string? BlobName,
    [property: Description("Content length in bytes")] long? ContentLength,
    [property: Description("Content type (MIME)")] string? ContentType,
    [property: Description("Entity tag") ] string? ETag,
    [property: Description("Last modified timestamp (UTC)")] DateTimeOffset? LastModified,
    [property: Description("Content MD5 hash (base64)")] string? ContentHashBase64,
    [property: Description("User-defined metadata key/value pairs")] System.Collections.Generic.IDictionary<string,string>? Metadata
);

[McpServerToolType]
public class DocumentExtrationTools
{
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly Microsoft.Extensions.Logging.ILogger<DocumentExtrationTools> _logger;

    public DocumentExtrationTools(Microsoft.Extensions.Configuration.IConfiguration config, Microsoft.Extensions.Logging.ILogger<DocumentExtrationTools> logger)
    {
        _config = config;
        _logger = logger;
    }

    [McpServerTool, Description("Load Azure Blob Storage file metadata for a given blob URI (supports SAS or Managed Identity).")]
    public async Task<BlobMetadataResult> GetBlobMetadata(BlobMetadataRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.FileUri)) throw new ArgumentException("FileUri is required", nameof(request.FileUri));

        var uri = new Uri(request.FileUri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS URIs are supported", nameof(request.FileUri));

        BlobClient blobClient;
        try
        {
            // If the URI already contains a SAS (sig=) we can construct directly
            if (uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase))
            {
                blobClient = new BlobClient(uri);
            }
            else
            {
                // Use DefaultAzureCredential; allows Managed Identity / developer auth
                // This requires that the configured storage account matches the URI host
                var configuredAccountUrl = _config["AzureStorage:AccountUrl"] ?? _config["AZURE_STORAGE_ACCOUNT_URL"]; // e.g. https://acct.blob.core.windows.net
                if (string.IsNullOrWhiteSpace(configuredAccountUrl))
                {
                    // Fallback: try anonymous (may work for public containers)
                    blobClient = new BlobClient(uri);
                }
                else
                {
                    var configuredHost = new Uri(configuredAccountUrl).Host;
                    if (!string.Equals(configuredHost, uri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Configured storage account host {ConfiguredHost} does not match blob URI host {BlobHost}. Attempting anonymous access.", configuredHost, uri.Host);
                        blobClient = new BlobClient(uri); // anonymous attempt
                    }
                    else
                    {
                        var credential = new DefaultAzureCredential();
                        blobClient = new BlobClient(uri, credential);
                    }
                }
            }

            // Head request to fetch properties
            var properties = await blobClient.GetPropertiesAsync().ConfigureAwait(false);
            var props = properties.Value;

            // Extract container + blob path
            // URI path starts with /container/segments...
            string? container = null;
            string? blobName = null;
            var segments = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) container = segments[0];
            if (segments.Length > 1) blobName = segments[1];

            var contentHash = props.ContentHash is { Length: > 0 } ? Convert.ToBase64String(props.ContentHash) : null;

            return new BlobMetadataResult(
                BlobUri: request.FileUri,
                AccountName: TryExtractAccountName(uri.Host),
                Container: container,
                BlobName: blobName,
                ContentLength: props.ContentLength,
                ContentType: props.ContentType,
                ETag: props.ETag.ToString(),
                LastModified: props.LastModified,
                ContentHashBase64: contentHash,
                Metadata: props.Metadata
            );
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"Blob not found: {request.FileUri}", ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 403 || ex.Status == 401)
        {
            throw new InvalidOperationException($"Unauthorized to access blob: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve blob metadata for {BlobUri}", request.FileUri);
            throw new InvalidOperationException($"Failed to retrieve blob metadata: {ex.Message}", ex);
        }
    }

    private static string? TryExtractAccountName(string host)
    {
        // typical host: account.blob.core.windows.net
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0) return parts[0];
        return null;
    }
}
