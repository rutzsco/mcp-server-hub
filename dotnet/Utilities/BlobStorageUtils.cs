using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Azure;

namespace mcp_server_hub.Utilities
{
    public class BlobStorageUtils
    {
        private readonly BlobServiceClient _serviceClient;

        public BlobStorageUtils(IConfiguration config)
        {
            var accountUrl = config["AzureStorage:AccountUrl"] ?? config["AZURE_STORAGE_ACCOUNT_URL"]; // e.g., https://<account>.blob.core.windows.net
            var connectionString = config["AzureStorage:ConnectionString"] ?? config["AZURE_STORAGE_CONNECTION_STRING"];

            if (!string.IsNullOrWhiteSpace(accountUrl))
            {
                // Use Managed Identity / DefaultAzureCredential
                var credential = new DefaultAzureCredential();
                _serviceClient = new BlobServiceClient(new Uri(accountUrl), credential);
                return;
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _serviceClient = new BlobServiceClient(connectionString);
                return;
            }

            throw new InvalidOperationException("Azure Storage is not configured. Set AzureStorage:AccountUrl (Managed Identity) or AzureStorage:ConnectionString.");
        }

        public static string GetStableBlobNameForUrl(string url, string extension = ".mp3")
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return hex + extension; // e.g. <sha>.mp3
        }

        public static string GetStableBlobNameForUrlAndAudioSettings(string url, int sampleRate, int channels, int bitrateKbps, string extension = ".mp3")
        {
            var key = $"{url}|sr:{sampleRate}|ch:{channels}|br:{bitrateKbps}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return $"{hex}-sr{sampleRate}-ch{channels}-br{bitrateKbps}{extension}";
        }

        public async Task<bool> BlobExistsAsync(string container, string blobName)
        {
            var containerClient = _serviceClient.GetBlobContainerClient(container);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = containerClient.GetBlobClient(blobName);
            return await blob.ExistsAsync();
        }

        public async Task<string?> TryDownloadToTempAsync(string container, string blobName)
        {
            var containerClient = _serviceClient.GetBlobContainerClient(container);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = containerClient.GetBlobClient(blobName);
            if (!await blob.ExistsAsync()) return null;

            var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            await blob.DownloadToAsync(tempPath);
            return tempPath;
        }

        public async Task UploadFileAsync(string container, string blobName, string filePath, string? contentType = null)
        {
            var containerClient = _serviceClient.GetBlobContainerClient(container);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = containerClient.GetBlobClient(blobName);
            var options = new BlobUploadOptions();
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                options.HttpHeaders = new BlobHttpHeaders { ContentType = contentType };
            }
            await blob.UploadAsync(filePath, options);
        }

        // NEW HELPERS FOR GENERIC BLOB URI ACCESS (SAS or same-account)
        public BlobClient ResolveBlobClient(Uri blobUri, ILogger? logger = null)
        {
            if (blobUri is null) throw new ArgumentNullException(nameof(blobUri));

            // If SAS token present just return direct client
            if (blobUri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase))
            {
                return new BlobClient(blobUri);
            }

            // If host matches configured service account and we have identity/connection
            var serviceHost = _serviceClient.Uri.Host;
            if (string.Equals(serviceHost, blobUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                // Use same auth approach as service client creation. If service client was created via connection string
                // we can still create a blob client directly from the Uri (anonymous won't work for private containers) so prefer credential if we used MI.
                // We detect by checking if ServiceClient is using token credential (not exposed easily); simplest is attempt identity then fall back.
                try
                {
                    return new BlobClient(blobUri, new DefaultAzureCredential());
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Falling back to anonymous blob client for {BlobUri}", blobUri);
                    return new BlobClient(blobUri);
                }
            }

            logger?.LogWarning("Blob host {BlobHost} does not match configured service host {ServiceHost}. Using anonymous client.", blobUri.Host, serviceHost);
            return new BlobClient(blobUri);
        }

        public async Task<(BlobClient Client, BlobProperties Properties)> GetBlobPropertiesAsync(Uri blobUri, ILogger? logger = null)
        {
            var client = ResolveBlobClient(blobUri, logger);
            var props = await client.GetPropertiesAsync();
            return (client, props.Value);
        }

        public async Task<(string Text, int Length)> DownloadTextAsync(Uri blobUri, int maxBytes, ILogger? logger = null)
        {
            var (client, props) = await GetBlobPropertiesAsync(blobUri, logger);
            if (props.ContentLength == 0) return (string.Empty, 0);

            using var ms = new MemoryStream();
            if (props.ContentLength > maxBytes)
            {
                var download = await client.DownloadStreamingAsync(new BlobDownloadOptions { Range = new Azure.HttpRange(0, maxBytes) });
                await download.Value.Content.CopyToAsync(ms);
            }
            else
            {
                await client.DownloadToAsync(ms);
            }
            var bytes = ms.ToArray();
            var text = Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
            return (text, text.Length);
        }
    }
}
