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
        private static readonly Regex YouTubeRegex = new(
            @"^(https?://)?(www\.)?(youtube\.com|youtu\.be)/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsYouTubeUrl(string url)
            => !string.IsNullOrWhiteSpace(url) && YouTubeRegex.IsMatch(url);

        public static async Task<YouTubeToMp3Result> DownloadYouTubeToMp3Async(YouTubeToMp3Request request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("Url is required", nameof(request.Url));

            var sampleRate = request.SampleRateHz ?? 16000;
            var channels = request.Channels ?? 1;
            var bitrate = request.BitrateKbps ?? 32;

            // Ensure ffmpeg binaries are discoverable. Expect ffmpeg(.exe) to be placed in the app's base directory.
            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = AppContext.BaseDirectory
            });

            var youtube = new YoutubeClient();

            // Get video metadata and best audio-only stream
            var video = await youtube.Videos.GetAsync(request.Url);
            var manifest = await youtube.Videos.Streams.GetManifestAsync(request.Url);
            var audio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            if (audio is null)
            {
                throw new InvalidOperationException("No audio-only streams found for the provided URL.");
            }

            // Build output path
            var baseDir = AppContext.BaseDirectory;
            var safeTitle = SanitizeFileName(video?.Title ?? "youtube_audio");
            var defaultFileName = $"{safeTitle}_16khz_mono.mp3";

            string outputPath;
            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                outputPath = Path.Combine(baseDir, defaultFileName);
            }
            else
            {
                var provided = request.OutputPath;
                if (!Path.IsPathRooted(provided!) && provided!.IndexOf(Path.DirectorySeparatorChar) < 0 && provided!.IndexOf(Path.AltDirectorySeparatorChar) < 0)
                {
                    outputPath = Path.Combine(baseDir, provided!);
                }
                else
                {
                    outputPath = Path.GetFullPath(provided!);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Download to a temp file
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                await youtube.Videos.Streams.DownloadAsync(audio, tempFile);

                // Convert and downsample
                await FFMpegArguments
                    .FromFileInput(tempFile)
                    .OutputToFile(outputPath, true, options => options
                        .WithAudioCodec("libmp3lame")
                        .WithAudioSamplingRate(sampleRate)
                        .WithCustomArgument($"-ac {channels}")
                        .WithAudioBitrate(bitrate))
                    .ProcessAsynchronously();

                return new YouTubeToMp3Result(
                    OutputPath: outputPath,
                    Title: video?.Title,
                    Duration: video?.Duration
                );
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore */ }
                }
            }
        }

        public static async Task<string> DownloadFileAsync(HttpClient http, string url, string? fileNameHint = null)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is required", nameof(url));

            var tempDir = Path.GetTempPath();
            var name = !string.IsNullOrWhiteSpace(fileNameHint) ? SanitizeFileName(fileNameHint!) : Path.GetRandomFileName();
            if (!name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                name += ".mp3";
            }

            var destPath = Path.Combine(tempDir, name);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(destPath);
            await input.CopyToAsync(output);

            return destPath;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = new string(Path.GetInvalidFileNameChars());
            var invalidRe = new Regex($"[{Regex.Escape(invalid)}]+");
            var cleaned = invalidRe.Replace(name, "_").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
        }
    }
}
