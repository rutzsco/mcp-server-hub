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

namespace mcp_server_hub.Tools
{
    public record TranscriptionRequest(
        [property: Description("URL to a YouTube video or a direct .mp3 file")] string Url,
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

        public TranscriptionTools(IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
        }

        [McpServerTool, Description("Transcribe audio from a YouTube URL or an MP3 URL using Azure OpenAI Whisper. Returns transcript text.")]
        public async Task<string> Transcribe(TranscriptionRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("Url is required", nameof(request.Url));

            // 1) Resolve to an MP3 file on disk
            string mp3Path;
            if (MediaUtils.IsYouTubeUrl(request.Url))
            {
                var yt = await MediaUtils.DownloadYouTubeToMp3WithAzureCacheAsync(new YouTubeToMp3Request(request.Url), _config);
                mp3Path = yt.OutputPath;
            }
            else
            {
                using var http = _httpClientFactory.CreateClient();
                mp3Path = await MediaUtils.DownloadFileAsync(http, request.Url);
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
