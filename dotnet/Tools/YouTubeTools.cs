using System;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFMpegCore;
using ModelContextProtocol.Server;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace mcp_server_hub.Tools
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

    [McpServerToolType]
    public class YouTubeTools
    {
        [McpServerTool, Description("Download a YouTube video's audio and convert it to an MP3 optimized for transcription (16kHz mono, 64 kbps by default). Returns the saved file path.")]
        public async Task<YouTubeToMp3Result> DownloadToMp3(YouTubeToMp3Request request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("Url is required", nameof(request.Url));

            var sampleRate = request.SampleRateHz ?? 16000;
            var channels = request.Channels ?? 1;
            var bitrate = request.BitrateKbps ?? 64;

            // Ensure ffmpeg binaries are discoverable. Expect ffmpeg(.exe) to be placed in the app's base directory as per README.
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
                // If only a file name is provided, place it under baseDir; otherwise, respect the full/relative path.
                var provided = request.OutputPath;
                if (!Path.IsPathRooted(provided) && provided.IndexOf(Path.DirectorySeparatorChar) < 0 && provided.IndexOf(Path.AltDirectorySeparatorChar) < 0)
                {
                    outputPath = Path.Combine(baseDir, provided);
                }
                else
                {
                    outputPath = Path.GetFullPath(provided);
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

        private static string SanitizeFileName(string name)
        {
            var invalid = new string(Path.GetInvalidFileNameChars());
            var invalidRe = new Regex($"[{Regex.Escape(invalid)}]+");
            var cleaned = invalidRe.Replace(name, "_").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
        }
    }
}
