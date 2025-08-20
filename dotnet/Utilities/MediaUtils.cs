using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFMpegCore;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace mcp_server_hub.Utilities
{
    public record YouTubeToMp3Request(
        [property: Description("Full YouTube video URL")] string Url,
        [property: Description("Output file name or full path for the resulting MP3. Optional. If only a file name is provided, it will be saved to the application's output folder.")] string? OutputPath = null,
        [property: Description("Target audio sample rate in Hz (default: 16000)")] int? SampleRateHz = null,
        [property: Description("Number of channels: 1=mono, 2=stereo (default: 1)")] int? Channels = null,
        [property: Description("Audio bitrate in kbps (default: 64)")] int? BitrateKbps = null
    );

    public record YouTubeToMp3Result(
        [property: Description("Absolute path to the generated MP3 file")] string OutputPath,
        [property: Description("Video title, if available")] string? Title,
        [property: Description("Approximate video duration, if available")] TimeSpan? Duration
    );

    public class MediaUtils
    {
        private readonly ILogger<MediaUtils> _logger;

        // Configuration constants
        private static class Config
        {
            public const int DefaultSampleRate = 16000;
            public const int DefaultChannels = 1;
            public const int DefaultBitrate = 32;
            public const string DefaultFileExtension = ".mp3";
            public const string AudioCodec = "libmp3lame";
            public const string DefaultFileName = "youtube_audio";
            // User-Agent string to mimic a real browser and avoid blocking
            public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        }

        private static readonly Regex YouTubeRegex = new(
            @"^(https?://)?(www\.)?(youtube\.com|youtu\.be)/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FileNameCleanupRegex = new(
            $"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]+",
            RegexOptions.Compiled);

        static MediaUtils()
        {
            // Configure FFmpeg once during static initialization
            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = AppContext.BaseDirectory
            });
        }

        public MediaUtils(ILogger<MediaUtils> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static bool IsYouTubeUrl(string url) => !string.IsNullOrWhiteSpace(url) && YouTubeRegex.IsMatch(url);

        public async Task<YouTubeToMp3Result> DownloadYouTubeToMp3WithAzureCacheAsync(YouTubeToMp3Request request, IConfiguration config)
        {
            _logger.LogInformation("Starting YouTube to MP3 download for URL: {Url}", request.Url);
            
            ValidateRequest(request);

            var audioSettings = ExtractAudioSettings(request);
            _logger.LogInformation("Audio settings - SampleRate: {SampleRate}Hz, Channels: {Channels}, Bitrate: {Bitrate}kbps", 
                audioSettings.SampleRate, audioSettings.Channels, audioSettings.Bitrate);
            
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", Config.UserAgent);
            var youtube = new YoutubeClient(httpClient);

            var container = config["AzureStorage:Container"] ?? "media-cache";
            _logger.LogInformation("Using Azure Storage container: {Container}", container);
            
            BlobStorageUtils? blobUtil = null;
            try 
            { 
                blobUtil = new BlobStorageUtils(config);
                _logger.LogInformation("Azure Blob Storage configured successfully");
            } 
            catch (Exception ex) 
            { 
                _logger.LogWarning(ex, "Azure Blob Storage not available, proceeding without cache");
            }

            try
            {
                _logger.LogInformation("Fetching video metadata and audio streams...");
                var (video, audioStream) = await GetVideoAndAudioStreamAsync(youtube, request.Url).ConfigureAwait(false);
                
                _logger.LogInformation("Video found - Title: '{Title}', Duration: {Duration}", video?.Title, video?.Duration);
                _logger.LogInformation("Audio stream - Bitrate: {Bitrate}, Container: {Container}", audioStream.Bitrate, audioStream.Container);
                
                var fileName = SanitizeFileName((video?.Title ?? Config.DefaultFileName) + "_16khz_mono") + Config.DefaultFileExtension;
                var blobName = BlobStorageUtils.GetStableBlobNameForUrlAndAudioSettings(
                    request.Url,
                    audioSettings.SampleRate,
                    audioSettings.Channels,
                    audioSettings.Bitrate,
                    Config.DefaultFileExtension);

                _logger.LogInformation("Generated blob name: {BlobName}", blobName);

                // Try to fetch from blob if available
                if (blobUtil is not null)
                {
                    _logger.LogInformation("Checking Azure cache for existing file...");
                    var cachedPath = await blobUtil.TryDownloadToTempAsync(container, blobName);
                    if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                    {
                        _logger.LogInformation("Found cached file, copying to final destination: {CachedPath}", cachedPath);
                        var finalPath = DetermineOutputPath(request.OutputPath, video?.Title);
                        Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? AppContext.BaseDirectory);
                        File.Copy(cachedPath, finalPath, overwrite: true);
                        try { File.Delete(cachedPath); } catch { }
                        
                        _logger.LogInformation("Successfully retrieved from cache: {OutputPath}", finalPath);
                        return new YouTubeToMp3Result(finalPath, video?.Title, video?.Duration);
                    }
                    else
                    {
                        _logger.LogInformation("File not found in cache, will download and convert");
                    }
                }

                // Not in cache: download from YouTube, convert, then upload to blob
                var outputPath = DetermineOutputPath(request.OutputPath, video?.Title);
                _logger.LogInformation("Starting download and conversion to: {OutputPath}", outputPath);
                
                await DownloadAndConvertAudioAsync(youtube, audioStream, outputPath, audioSettings).ConfigureAwait(false);
                
                _logger.LogInformation("Download and conversion completed successfully");

                if (blobUtil is not null)
                {
                    try 
                    { 
                        _logger.LogInformation("Uploading converted file to Azure cache...");
                        await blobUtil.UploadFileAsync(container, blobName, outputPath, contentType: "audio/mpeg");
                        _logger.LogInformation("Successfully uploaded to Azure cache");
                    }
                    catch (Exception ex) 
                    { 
                        _logger.LogWarning(ex, "Failed to upload to Azure cache (non-fatal)");
                    }
                }

                _logger.LogInformation("YouTube to MP3 conversion completed successfully: {OutputPath}", outputPath);
                return new YouTubeToMp3Result(outputPath, video?.Title, video?.Duration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download and convert YouTube video: {Url}", request.Url);
                throw new InvalidOperationException($"Failed to download and convert YouTube video: {ex.Message}", ex);
            }
        }

        private void ValidateRequest(YouTubeToMp3Request request)
        {
            _logger.LogDebug("Validating YouTube request...");
            
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url))
                throw new ArgumentException("Url is required", nameof(request.Url));
            if (!IsYouTubeUrl(request.Url))
            {
                _logger.LogError("Invalid YouTube URL format: {Url}", request.Url);
                throw new ArgumentException("Invalid YouTube URL format", nameof(request.Url));
            }
            
            _logger.LogDebug("Request validation passed");
        }

        private static (int SampleRate, int Channels, int Bitrate) ExtractAudioSettings(YouTubeToMp3Request request)
        {
            return (
                SampleRate: request.SampleRateHz ?? Config.DefaultSampleRate,
                Channels: request.Channels ?? Config.DefaultChannels,
                Bitrate: request.BitrateKbps ?? Config.DefaultBitrate
            );
        }

        private async Task<(YoutubeExplode.Videos.Video? Video, IStreamInfo AudioStream)> GetVideoAndAudioStreamAsync(
            YoutubeClient youtube, string url)
        {
            _logger.LogInformation($"Fetching video metadata...URL: {url}");
            var video = await youtube.Videos.GetAsync(url).ConfigureAwait(false);
            
            _logger.LogInformation("Fetching stream manifest...");
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url).ConfigureAwait(false);
            
            _logger.LogInformation("Looking for audio streams...");
            var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (audioStream is null)
            {
                _logger.LogError("No audio-only streams found for URL: {Url}", url);
                throw new InvalidOperationException("No audio-only streams found for the provided URL.");
            }
            
            _logger.LogInformation("Selected audio stream with bitrate: {Bitrate}", audioStream.Bitrate);
            return (video, audioStream);
        }

        private static string DetermineOutputPath(string? requestedPath, string? videoTitle)
        {
            var baseDir = AppContext.BaseDirectory;
            var safeTitle = SanitizeFileName(videoTitle ?? Config.DefaultFileName);
            var defaultFileName = $"{safeTitle}_16khz_mono{Config.DefaultFileExtension}";

            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.Combine(baseDir, defaultFileName);
            }

            // If it's just a filename (no directory separators), combine with base directory
            if (IsSimpleFileName(requestedPath))
            {
                return Path.Combine(baseDir, requestedPath);
            }

            // Otherwise, treat as full or relative path
            return Path.GetFullPath(requestedPath);
        }

        private static bool IsSimpleFileName(string path)
        {
            return !Path.IsPathRooted(path) &&
                   path.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                   path.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private async Task DownloadAndConvertAudioAsync(
            YoutubeClient youtube,
            IStreamInfo audioStream,
            string outputPath,
            (int SampleRate, int Channels, int Bitrate) audioSettings)
        {
            _logger.LogInformation("Starting audio download and conversion...");
            
            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                _logger.LogDebug("Created output directory: {OutputDir}", outputDir);
            }

            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            _logger.LogInformation("Using temporary file: {TempFile}", tempFile);

            try
            {
                // Download to temporary file
                _logger.LogInformation("Downloading audio stream from YouTube...");
                await youtube.Videos.Streams.DownloadAsync(audioStream, tempFile).ConfigureAwait(false);
                
                var tempFileInfo = new FileInfo(tempFile);
                _logger.LogInformation("Audio download completed - Size: {Size} bytes", tempFileInfo.Length);

                // Convert with FFmpeg
                _logger.LogInformation("Starting FFmpeg conversion with settings - SampleRate: {SampleRate}Hz, Channels: {Channels}, Bitrate: {Bitrate}kbps", 
                    audioSettings.SampleRate, audioSettings.Channels, audioSettings.Bitrate);
                
                await FFMpegArguments
                    .FromFileInput(tempFile)
                    .OutputToFile(outputPath, true, options => options
                        .WithAudioCodec(Config.AudioCodec)
                        .WithAudioSamplingRate(audioSettings.SampleRate)
                        .WithCustomArgument($"-ac {audioSettings.Channels}")
                        .WithAudioBitrate(audioSettings.Bitrate))
                    .ProcessAsynchronously()
                    .ConfigureAwait(false);
                
                var outputFileInfo = new FileInfo(outputPath);
                _logger.LogInformation("FFmpeg conversion completed - Output size: {Size} bytes", outputFileInfo.Length);
            }
            finally
            {
                // Clean up temporary file
                if (File.Exists(tempFile))
                {
                    try 
                    { 
                        File.Delete(tempFile);
                        _logger.LogDebug("Cleaned up temporary file: {TempFile}", tempFile);
                    } 
                    catch (Exception ex) 
                    { 
                        _logger.LogWarning(ex, "Failed to clean up temporary file: {TempFile}", tempFile);
                    }
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            // First replace all whitespace with underscores
            var withoutSpaces = Regex.Replace(name, @"\s+", "_");
            // Then replace invalid file name characters with underscores
            var cleaned = FileNameCleanupRegex.Replace(withoutSpaces, "_").Trim('_');
            return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
        }

        public async Task<string> DownloadFileAsync(HttpClient http, string url, string? fileNameHint = null)
        {
            _logger.LogInformation("Starting file download from URL: {Url}", url);
            
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL is required", nameof(url));

            var fileName = !string.IsNullOrWhiteSpace(fileNameHint) 
                ? SanitizeFileName(fileNameHint) 
                : Path.GetRandomFileName();

            if (!fileName.EndsWith(Config.DefaultFileExtension, StringComparison.OrdinalIgnoreCase))
                fileName += Config.DefaultFileExtension;

            var destPath = Path.Combine(Path.GetTempPath(), fileName);
            
            _logger.LogInformation("Download destination: {DestPath}", destPath);

            try
            {
                _logger.LogInformation("Sending HTTP request...");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                
                _logger.LogInformation("HTTP response received - Status: {StatusCode}, Content-Length: {ContentLength}", 
                    response.StatusCode, response.Content.Headers.ContentLength);

                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = File.Create(destPath);
                
                _logger.LogInformation("Starting file copy...");
                await input.CopyToAsync(output).ConfigureAwait(false);
                
                _logger.LogInformation("File download completed successfully: {DestPath}", destPath);
                return destPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download file from {Url}", url);
                
                // Clean up partial file on failure
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { /* ignore cleanup errors */ }
                }
                throw new InvalidOperationException($"Failed to download file from {url}: {ex.Message}", ex);
            }
        }
    }
}
