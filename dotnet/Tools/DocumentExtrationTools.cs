using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using ModelContextProtocol.Server;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;
using System.Text.Json;
using mcp_server_hub.Utilities;

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

// MaxBytes, Deployment and Raw removed – now provided via configuration (tool config)
public record BlobContentExtractionRequest(
    [property: Description("HTTPS URI of the blob (can include SAS token)")] string FileUri,
    [property: Description("System prompt describing information to extract (default: extract key facts, entities, dates, amounts)")] string? ExtractionPrompt = null
);

public record BlobContentExtractionResult(
    [property: Description("Original blob URI")] string BlobUri,
    [property: Description("Truncated content length processed")] int ContentLength,
    [property: Description("Model response with extracted info (JSON unless RawResponse=true in config)")] string Extraction
);

[McpServerToolType]
public class DocumentExtrationTools
{
    private readonly IConfiguration _config;
    private readonly ILogger<DocumentExtrationTools> _logger;
    private readonly BlobStorageUtils _blobUtils;

    public DocumentExtrationTools(IConfiguration config, ILogger<DocumentExtrationTools> logger, BlobStorageUtils blobUtils)
    {
        _config = config;
        _logger = logger;
        _blobUtils = blobUtils;
    }

    [McpServerTool, Description("Load Azure Blob Storage file metadata for a given blob URI (supports SAS or Managed Identity).")]
    public async Task<BlobMetadataResult> GetBlobMetadata(BlobMetadataRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.FileUri)) throw new ArgumentException("FileUri is required", nameof(request.FileUri));

        var uri = new Uri(request.FileUri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS URIs are supported", nameof(request.FileUri));

        try
        {
            var (blobClient, props) = await _blobUtils.GetBlobPropertiesAsync(uri, _logger);

            // Extract container + blob path
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

    [McpServerTool, Description("Download blob text content and extract key information using an LLM (Azure OpenAI chat completion via Semantic Kernel). Large blobs truncated.")]
    public async Task<BlobContentExtractionResult> ExtractBlobContent(BlobContentExtractionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.FileUri)) throw new ArgumentException("FileUri is required", nameof(request.FileUri));

        var uri = new Uri(request.FileUri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS URIs are supported", nameof(request.FileUri));

        try
        {
            var maxBytes = ResolveMaxBytes();
            var (text, length) = await _blobUtils.DownloadTextAsync(uri, maxBytes, _logger);
            if (length == 0) throw new InvalidOperationException("Blob is empty");

            var systemPrompt = request.ExtractionPrompt ?? ResolveDefaultPrompt();
            var extraction = await RunChatExtractionAsync(systemPrompt, text);
            return new BlobContentExtractionResult(request.FileUri, length, extraction);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"Blob not found: {request.FileUri}", ex);
        }
    }

    private string ResolveDefaultPrompt() => _config["DocumentExtraction:Prompt"] ?? "You are a system that extracts structured key information from the provided document content. Return concise JSON with fields: entities (array of {type,name}), dates (ISO8601 strings), amounts (array of {currency?, amount, context}), summary (short), keyFacts (array of strings). If content appears binary or non-text, indicate that in a JSON error field.";

    private int ResolveMaxBytes()
    {
        var val = _config["DocumentExtraction:MaxBytes"];
        if (int.TryParse(val, out var parsed) && parsed > 0) return parsed;
        return 200_000;
    }

    private bool UseRawResponse()
    {
        var flag = _config["DocumentExtraction:RawResponse"];
        return bool.TryParse(flag, out var b) && b;
    }

    private async Task<string> RunChatExtractionAsync(string systemPrompt, string content)
    {
        var endpoint = _config["AzureOpenAI:Endpoint"] ?? _config["AZURE_OPENAI_ENDPOINT"];
        var apiKey = _config["AzureOpenAI:ApiKey"] ?? _config["AZURE_OPENAI_API_KEY"];
        var deployment = _config["DocumentExtraction:ChatDeployment"] ?? _config["AzureOpenAI:ChatDeployment"] ?? _config["AzureOpenAI:CompletionsDeployment"] ?? "gpt-4o";
        var apiVersion = _config["AzureOpenAI:ApiVersion"] ?? "2024-10-01-preview";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Azure OpenAI endpoint/api key not configured");

        // Build kernel with Azure OpenAI chat completion service
        var builder = Kernel.CreateBuilder();
        builder.AddAzureOpenAIChatCompletion(
            deploymentName: deployment,
            endpoint: endpoint,
            apiKey: apiKey,
            apiVersion: apiVersion);
        var kernel = builder.Build();
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"Document content (may be truncated):\n```\n{TruncateForPrompt(content, 12000)}\n```\nProvide the extracted information now.");

        var response = await chat.GetChatMessageContentAsync(history);
        var message = response.Content?.Trim() ?? string.Empty;

        if (!UseRawResponse())
        {
            var json = TryExtractJson(message) ?? message;
            return json;
        }
        return message;
    }

    private static string TruncateForPrompt(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) + "\n...[TRUNCATED]";
    }

    private static string? TryExtractJson(string input)
    {
        // Find first '{' and last '}' and attempt parse
        var first = input.IndexOf('{');
        var last = input.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            var candidate = input.Substring(first, last - first + 1);
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                return doc.RootElement.GetRawText();
            }
            catch { }
        }
        return null;
    }

    private static string? TryExtractAccountName(string host)
    {
        // typical host: account.blob.core.windows.net
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0) return parts[0];
        return null;
    }
}
