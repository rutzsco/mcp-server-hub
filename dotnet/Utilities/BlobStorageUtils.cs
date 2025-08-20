using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Azure.Identity;

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
    }
}
