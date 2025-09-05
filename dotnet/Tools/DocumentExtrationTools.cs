using System.ComponentModel;
using Azure;
using ModelContextProtocol.Server;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text;
using System.Text.Json;
using mcp_server_hub.Utilities;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using SkiaSharp;
using System.Reflection;

namespace mcp_server_hub.Tools;

public record BlobContentExtractionRequest(
    [property: Description("HTTPS URI of the PDF blob (can include SAS token)")] string FileUri,
    [property: Description("User prompt describing specific information to extract or analysis to perform on the document")] string? ExtractionPrompt = null
);

public record BlobContentExtractionResult(
    [property: Description("Original blob URI")] string BlobUri,
    [property: Description("Number of pages processed")] int PagesProcessed,
    [property: Description("Model response with extracted info from PDF images")] string Extraction
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

    [McpServerTool, Description("Download PDF blob, convert pages to PNG images, and extract key information using an LLM with vision capabilities (Azure OpenAI GPT-4V via Semantic Kernel).")]
    public async Task<BlobContentExtractionResult> ExtractBlobContent(BlobContentExtractionRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.FileUri)) throw new ArgumentException("FileUri is required", nameof(request.FileUri));

        var uri = new Uri(request.FileUri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS URIs are supported", nameof(request.FileUri));

        string? tempPdfPath = null;
        try
        {
            var (client, props) = await _blobUtils.GetBlobPropertiesAsync(uri, _logger);
            if (props.ContentLength == 0) throw new InvalidOperationException("PDF blob is empty");

            // Validate content type is PDF
            if (!string.Equals(props.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Content type is {ContentType}, expected application/pdf", props.ContentType);
            }

            // Download PDF to temp file
            tempPdfPath = Path.GetTempFileName();
            await client.DownloadToAsync(tempPdfPath);
            _logger.LogInformation("Downloaded PDF to {TempPath}, size: {Size} bytes", tempPdfPath, new FileInfo(tempPdfPath).Length);

            // Convert PDF pages to PNG images
            var pageImages = await ConvertPdfToPngImagesAsync(tempPdfPath);
            _logger.LogInformation("Converted PDF to {PageCount} PNG images", pageImages.Count);

            // Extract information using LLM vision
            var systemPrompt = LoadSystemPrompt();
            var userPrompt = request.ExtractionPrompt ?? ResolveDefaultUserPrompt();
            var extraction = await RunVisionExtractionAsync(systemPrompt, userPrompt, pageImages);
            
            return new BlobContentExtractionResult(request.FileUri, pageImages.Count, extraction);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"PDF blob not found: {request.FileUri}", ex);
        }
        finally
        {
            if (tempPdfPath != null && File.Exists(tempPdfPath))
            {
                try { File.Delete(tempPdfPath); } catch { }
            }
        }
    }

    private string LoadSystemPrompt()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "mcp_server_hub.Resources.DocumentAnalysisSystemPrompt.txt";
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger.LogWarning("Could not load embedded system prompt resource: {ResourceName}", resourceName);
            return ResolveDefaultSystemPrompt();
        }
        
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string ResolveDefaultSystemPrompt() => _config["DocumentExtraction:SystemPrompt"] ?? "You are a system that extracts structured key information from the provided PDF document images. Analyze each page image and return concise JSON with fields: entities (array of {type,name}), dates (ISO8601 strings), amounts (array of {currency?, amount, context}), summary (short), keyFacts (array of strings). Focus on text content visible in the images.";

    private string ResolveDefaultUserPrompt() => _config["DocumentExtraction:UserPrompt"] ?? "Please analyze this PDF document and extract all key information in the standard JSON format.";

    private int ResolveMaxPages()
    {
        var val = _config["DocumentExtraction:MaxPages"];
        if (int.TryParse(val, out var parsed) && parsed > 0) return parsed;
        return 5; // Default to first 5 pages
    }

    private int ResolveDpi()
    {
        var val = _config["DocumentExtraction:RenderDpi"];
        if (int.TryParse(val, out var parsed) && parsed > 0) return parsed;
        return 150; // Default DPI for rendering
    }

    private bool UseRawResponse()
    {
        var flag = _config["DocumentExtraction:RawResponse"];
        return bool.TryParse(flag, out var b) && b;
    }

    private async Task<List<string>> ConvertPdfToPngImagesAsync(string pdfPath)
    {
        var base64Images = new List<string>();
        var maxPages = ResolveMaxPages();

        using var document = PdfDocument.Open(pdfPath);
        var pagesToProcess = Math.Min(document.NumberOfPages, maxPages);
        
        _logger.LogInformation("Processing {PagesToProcess} pages out of {TotalPages}", pagesToProcess, document.NumberOfPages);

        for (int pageNum = 1; pageNum <= pagesToProcess; pageNum++)
        {
            try
            {
                var page = document.GetPage(pageNum);
                var base64Image = await ConvertPageToPngBase64Async(page);
                base64Images.Add(base64Image);
                _logger.LogDebug("Converted page {PageNum} to PNG", pageNum);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert page {PageNum} to PNG", pageNum);
            }
        }

        return base64Images;
    }

    private async Task<string> ConvertPageToPngBase64Async(Page page)
    {
        return await Task.Run(() =>
        {
            var pageWidth = (int)page.Width;
            var pageHeight = (int)page.Height;
            var dpi = ResolveDpi();
            var scale = dpi / 72f; // PDF is 72 DPI by default
            
            var scaledWidth = (int)(pageWidth * scale);
            var scaledHeight = (int)(pageHeight * scale);

            using var surface = SKSurface.Create(new SKImageInfo(scaledWidth, scaledHeight));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);

            // Simple text extraction and rendering (basic implementation)
            // Note: This is a simplified approach. For production, consider using a more robust PDF-to-image library
            var paint = new SKPaint
            {
                Color = SKColors.Black,
                TextSize = 12 * scale,
                IsAntialias = true
            };

            var yPos = 20 * scale;
            foreach (var word in page.GetWords())
            {
                var text = word.Text;
                var bbox = word.BoundingBox;
                var x = (float)(bbox.Left * scale);
                var y = (float)(bbox.Bottom * scale);
                
                canvas.DrawText(text, x, y, paint);
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return Convert.ToBase64String(data.ToArray());
        });
    }

    private async Task<string> RunVisionExtractionAsync(string systemPrompt, string userPrompt, List<string> pageImages)
    {
        var endpoint = _config["AzureOpenAI:Endpoint"] ?? _config["AZURE_OPENAI_ENDPOINT"];
        var apiKey = _config["AzureOpenAI:ApiKey"] ?? _config["AZURE_OPENAI_API_KEY"];
        var deployment = _config["DocumentExtraction:VisionDeployment"] ?? _config["AzureOpenAI:VisionDeployment"] ?? "gpt-4o";
        var apiVersion = _config["AzureOpenAI:ApiVersion"] ?? "2024-10-01-preview";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Azure OpenAI endpoint/api key not configured");

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

        // Create user message with images
        var fullUserMessage = new StringBuilder();
        fullUserMessage.AppendLine(userPrompt);
        fullUserMessage.AppendLine();
        fullUserMessage.AppendLine($"PDF Document Analysis - {pageImages.Count} pages:");
        
        for (int i = 0; i < pageImages.Count; i++)
        {
            fullUserMessage.AppendLine($"Page {i + 1}:");
        }

        var chatMessage = new ChatMessageContent(AuthorRole.User, fullUserMessage.ToString());
        
        // Add images as content items
        for (int i = 0; i < pageImages.Count; i++)
        {
            chatMessage.Items.Add(new ImageContent($"data:image/png;base64,{pageImages[i]}"));
        }
        
        history.Add(chatMessage);

        var response = await chat.GetChatMessageContentAsync(history);
        var message = response.Content?.Trim() ?? string.Empty;

        if (!UseRawResponse())
        {
            var json = TryExtractJson(message) ?? message;
            return json;
        }
        return message;
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
}
