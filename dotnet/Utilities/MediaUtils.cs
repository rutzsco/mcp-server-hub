using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFMpegCore;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

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

    public static class MediaUtils
    {
        // Configuration constants
        private static class Config
        {
            public const int DefaultSampleRate = 16000;
            public const int DefaultChannels = 1;
            public const int DefaultBitrate = 32;
            public const string DefaultFileExtension = ".mp3";
            public const string AudioCodec = "libmp3lame";
            public const string DefaultFileName = "youtube_audio";
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

        public static bool IsYouTubeUrl(string url)
            => !string.IsNullOrWhiteSpace(url) && YouTubeRegex.IsMatch(url);

        public static async Task<YouTubeToMp3Result> DownloadYouTubeToMp3Async(YouTubeToMp3Request request)
        {
            ValidateRequest(request);

            var audioSettings = ExtractAudioSettings(request);
            var youtube = new YoutubeClient();

            try
            {
                var (video, audioStream) = await GetVideoAndAudioStreamAsync(youtube, request.Url).ConfigureAwait(false);
                var outputPath = DetermineOutputPath(request.OutputPath, video?.Title);
                
                await DownloadAndConvertAudioAsync(youtube, audioStream, outputPath, audioSettings).ConfigureAwait(false);

                return new YouTubeToMp3Result(
                    OutputPath: outputPath,
                    Title: video?.Title,
                    Duration: video?.Duration
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to download and convert YouTube video: {ex.Message}", ex);
            }
        }

        public static async Task<string> DownloadFileAsync(HttpClient http, string url, string? fileNameHint = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL is required", nameof(url));

            var fileName = BuildDownloadFileName(fileNameHint);
            var destPath = Path.Combine(Path.GetTempPath(), fileName);

            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var output = File.Create(destPath);
                await input.CopyToAsync(output).ConfigureAwait(false);

                return destPath;
            }
            catch (Exception ex)
            {
                // Clean up partial file on failure
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { /* ignore cleanup errors */ }
                }
                throw new InvalidOperationException($"Failed to download file from {url}: {ex.Message}", ex);
            }
        }

        private static void ValidateRequest(YouTubeToMp3Request request)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url))
                throw new ArgumentException("Url is required", nameof(request.Url));
            if (!IsYouTubeUrl(request.Url))
                throw new ArgumentException("Invalid YouTube URL format", nameof(request.Url));
        }

        private static (int SampleRate, int Channels, int Bitrate) ExtractAudioSettings(YouTubeToMp3Request request)
        {
            return (
                SampleRate: request.SampleRateHz ?? Config.DefaultSampleRate,
                Channels: request.Channels ?? Config.DefaultChannels,
                Bitrate: request.BitrateKbps ?? Config.DefaultBitrate
            );
        }

        private static async Task<(YoutubeExplode.Videos.Video? Video, IStreamInfo AudioStream)> GetVideoAndAudioStreamAsync(
            YoutubeClient youtube, string url)
        {
            var video = await youtube.Videos.GetAsync(url).ConfigureAwait(false);
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url).ConfigureAwait(false);
            var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (audioStream is null)
                throw new InvalidOperationException("No audio-only streams found for the provided URL.");

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

        private static async Task DownloadAndConvertAudioAsync(
            YoutubeClient youtube,
            IStreamInfo audioStream,
            string outputPath,
            (int SampleRate, int Channels, int Bitrate) audioSettings)
        {
            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                // Download to temporary file
                await youtube.Videos.Streams.DownloadAsync(audioStream, tempFile).ConfigureAwait(false);

                // Convert with FFmpeg
                await FFMpegArguments
                    .FromFileInput(tempFile)
                    .OutputToFile(outputPath, true, options => options
                        .WithAudioCodec(Config.AudioCodec)
                        .WithAudioSamplingRate(audioSettings.SampleRate)
                        .WithCustomArgument($"-ac {audioSettings.Channels}")
                        .WithAudioBitrate(audioSettings.Bitrate))
                    .ProcessAsynchronously()
                    .ConfigureAwait(false);
            }
            finally
            {
                // Clean up temporary file
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore cleanup errors */ }
                }
            }
        }

        private static string BuildDownloadFileName(string? fileNameHint)
        {
            var name = !string.IsNullOrWhiteSpace(fileNameHint) 
                ? SanitizeFileName(fileNameHint) 
                : Path.GetRandomFileName();

            return name.EndsWith(Config.DefaultFileExtension, StringComparison.OrdinalIgnoreCase)
                ? name
                : name + Config.DefaultFileExtension;
        }

        private static string SanitizeFileName(string name)
        {
            var cleaned = FileNameCleanupRegex.Replace(name, "_").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
        }
    }
}
