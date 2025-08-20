using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using mcp_server_hub.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace mcp_server_hub.Tools
{
    public record TranscriptionRequest(
        [property: Description("URL to a YouTube video or a direct .mp3 file")] string Url,
        [property: Description("Force use of Whisper transcription instead of YouTube Transcript API (default: false)")] bool UseWhisper = false,
        [property: Description("Azure OpenAI deployment name for Whisper (audio-transcriptions)")] string? Deployment = null,
        [property: Description("Optional prompt or system hint for Whisper")] string? Prompt = null,
        [property: Description("Temperature for sampling (default 0)")] float? Temperature = null,
        [property: Description("Response format: text/json/vtt/srt/verbose_json/subtitles_json (default text)")] string? ResponseFormat = null,
        [property: Description("Language code, e.g., en")] string? Language = null
    );

    [McpServerToolType]
    public class TranscriptionTools
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly MediaUtils _mediaUtils;
        private readonly ILogger<TranscriptionTools> _logger;

        public TranscriptionTools(IHttpClientFactory httpClientFactory, IConfiguration config, MediaUtils mediaUtils, ILogger<TranscriptionTools> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _mediaUtils = mediaUtils;
            _logger = logger;
        }

        [McpServerTool, Description("Transcribe audio from a YouTube URL or an MP3 URL. For YouTube videos, uses YouTube Transcript API by default (faster, no audio processing), falls back to Whisper if needed.")]
        public async Task<string> Transcribe(TranscriptionRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("Url is required", nameof(request.Url));

            // For YouTube URLs, try the YouTube Transcript API first (unless explicitly forced to use Whisper)
            if (MediaUtils.IsYouTubeUrl(request.Url) && !request.UseWhisper)
            {
                try
                {
                    _logger.LogInformation("Attempting to get transcript using YouTube Transcript API for URL: {Url}", request.Url);
                    return await GetYouTubeTranscriptWithCacheAsync(request.Url);
                }
                catch (Exception ex)
                {
                    // Log the YouTube Transcript API failure and fall back to Whisper
                    // Note: This is a common scenario as not all videos have transcripts available
                    var isConfigError = ex.Message.Contains("API token is not configured");
                    var logLevel = isConfigError ? LogLevel.Warning : LogLevel.Information;
                    
                    _logger.Log(logLevel, ex, "YouTube Transcript API failed, falling back to Whisper transcription. Reason: {Reason}", ex.Message);
                    
                    // Continue to Whisper transcription below
                }
            }

            // Use Whisper transcription (either forced or as fallback)
            _logger.LogInformation("Using Whisper transcription for URL: {Url}", request.Url);
            return await TranscribeWithWhisper(request);
        }

        private async Task<string> GetYouTubeTranscriptWithCacheAsync(string youtubeUrl)
        {
            var container = _config["AzureStorage:Container"] ?? "media-cache";
            var blobName = GetTranscriptBlobName(youtubeUrl);
            
            _logger.LogInformation("Checking transcript cache for URL: {Url}, BlobName: {BlobName}", youtubeUrl, blobName);

            BlobStorageUtils? blobUtil = null;
            try
            {
                blobUtil = new BlobStorageUtils(_config);
                _logger.LogDebug("Azure Blob Storage configured for transcript cache");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure Blob Storage not available for transcript cache, proceeding without cache");
            }

            // Try to get cached transcript first
            if (blobUtil is not null)
            {
                try
                {
                    var cachedTranscriptPath = await blobUtil.TryDownloadToTempAsync(container, blobName);
                    if (!string.IsNullOrEmpty(cachedTranscriptPath) && File.Exists(cachedTranscriptPath))
                    {
                        _logger.LogInformation("Found cached transcript for URL: {Url}", youtubeUrl);
                        var cachedTranscript = await File.ReadAllTextAsync(cachedTranscriptPath, Encoding.UTF8);
                        
                        // Clean up temp file
                        try { File.Delete(cachedTranscriptPath); } catch { }
                        
                        if (!string.IsNullOrWhiteSpace(cachedTranscript))
                        {
                            _logger.LogInformation("Successfully retrieved transcript from cache, length: {Length} characters", cachedTranscript.Length);
                            return cachedTranscript;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve transcript from cache (non-fatal), will fetch fresh transcript");
                }
            }

            // Cache miss or no cache available, get fresh transcript
            _logger.LogInformation("Transcript not found in cache, fetching from YouTube Transcript API");
            var transcript = await _mediaUtils.GetYouTubeTranscriptAsync(youtubeUrl, _config);

            // Cache the transcript for future use
            if (blobUtil is not null && !string.IsNullOrWhiteSpace(transcript))
            {
                try
                {
                    var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
                    await File.WriteAllTextAsync(tempFile, transcript, Encoding.UTF8);
                    
                    _logger.LogInformation("Caching transcript to blob storage: {BlobName}", blobName);
                    await blobUtil.UploadFileAsync(container, blobName, tempFile, contentType: "text/plain; charset=utf-8");
                    
                    // Clean up temp file
                    try { File.Delete(tempFile); } catch { }
                    
                    _logger.LogInformation("Successfully cached transcript for future use");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache transcript (non-fatal)");
                }
            }

            return transcript;
        }

        private static string GetTranscriptBlobName(string youtubeUrl)
        {
            // Create a stable blob name based on base64 encoded URL
            var urlBytes = Encoding.UTF8.GetBytes(youtubeUrl);
            var base64Url = Convert.ToBase64String(urlBytes);
            
            // Make the base64 string blob-safe by replacing problematic characters
            var safeBlobName = base64Url
                .Replace("/", "_")
                .Replace("+", "-")
                .Replace("=", "");
            
            return $"transcripts/{safeBlobName}.txt";
        }

        private async Task<string> TranscribeWithWhisper(TranscriptionRequest request)
        {
            // 1) Resolve to an MP3 file on disk
            string mp3Path;
            if (MediaUtils.IsYouTubeUrl(request.Url))
            {
                var yt = await _mediaUtils.DownloadYouTubeToMp3WithAzureCacheAsync(new YouTubeToMp3Request(request.Url), _config);
                mp3Path = yt.OutputPath;
            }
            else
            {
                using var http = _httpClientFactory.CreateClient();
                mp3Path = await _mediaUtils.DownloadFileAsync(http, request.Url);
            }

            try
            {
                // 2) Call Azure OpenAI Whisper transcription endpoint
                var endpoint = _config["AzureOpenAI:Endpoint"] ?? _config["AZURE_OPENAI_ENDPOINT"];
                var apiKey = _config["AzureOpenAI:ApiKey"] ?? _config["AZURE_OPENAI_API_KEY"];
                var deployment = request.Deployment ?? _config["AzureOpenAI:WhisperDeployment"] ?? "whisper";

                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("Azure OpenAI endpoint or API key is not configured. Set AzureOpenAI:Endpoint and AzureOpenAI:ApiKey in appsettings or environment variables.");
                }

                // Check file size and warn if it's too large
                var fileInfo = new FileInfo(mp3Path);
                if (fileInfo.Length > 25 * 1024 * 1024) // 25MB limit for Azure OpenAI
                {
                    throw new InvalidOperationException($"Audio file is too large ({fileInfo.Length / (1024 * 1024)}MB). Azure OpenAI Whisper has a 25MB limit.");
                }

                // Azure OpenAI Whisper compatible endpoint (OpenAI-style): POST {endpoint}/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-10-01-preview
                var apiVersion = _config["AzureOpenAI:ApiVersion"] ?? "2024-10-01-preview";
                var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/audio/transcriptions?api-version={apiVersion}";

                // Use the named HttpClient with extended timeout for transcription
                using var client = _httpClientFactory.CreateClient("AzureOpenAI");
                
                if (client.DefaultRequestHeaders.Contains("api-key")) client.DefaultRequestHeaders.Remove("api-key");
                client.DefaultRequestHeaders.Add("api-key", apiKey);

                using var form = new MultipartFormDataContent();
                // model field is required by OpenAI schema, Azure uses deployment in path but still accepts a model name
                form.Add(new StringContent(deployment), "model");

                if (!string.IsNullOrWhiteSpace(request.Prompt)) form.Add(new StringContent(request.Prompt), "prompt");
                if (request.Temperature.HasValue) form.Add(new StringContent(request.Temperature.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature");
                if (!string.IsNullOrWhiteSpace(request.ResponseFormat)) form.Add(new StringContent(request.ResponseFormat), "response_format");
                if (!string.IsNullOrWhiteSpace(request.Language)) form.Add(new StringContent(request.Language), "language");

                await using var fs = File.OpenRead(mp3Path);
                var fileContent = new StreamContent(fs);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                form.Add(fileContent, "file", Path.GetFileName(mp3Path));

                using var resp = await client.PostAsync(url, form);
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Transcription failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
                }

                // If JSON, try to parse text field; else return as-is
                if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            return textEl.GetString() ?? string.Empty;
                        }
                    }
                    catch { /* fall back to body */ }
                }

                return body;
            }
            finally
            {
                try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* ignore */ }
            }
        }
    }
}
