using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using mcp_server_hub.Tools;
using mcp_server_hub.Utilities;
using Microsoft.AspNetCore.OpenApi;

namespace mcp_server_hub.Routes
{
    public static class ToolRoutes
    {
        public static IEndpointRouteBuilder MapToolRoutes(this IEndpointRouteBuilder app)
        {
            // Time endpoint
            app.MapGet("/api/time", () => Results.Ok(TimeTools.GetCurrentTime()))
               .WithName("GetCurrentTime")
               .WithSummary("Get the current time (UTC and local)")
               .WithDescription("Returns both UTC and local time using server's timezone.")
               .WithTags("Time");

            // Weather endpoint (forecast)
            app.MapGet("/api/weather/forecast", async (double lat, double lon, WeatherTools tools) =>
            {
                var json = await tools.GetForecast(new LocationPoint(lat, lon));
                return Results.Content(json, "application/json");
            })
            .WithName("GetWeatherForecast")
            .WithSummary("Get weather forecast for provided coordinates")
            .WithDescription("Provide latitude and longitude to fetch the forecast from weather.gov.")
            .WithOpenApi(op =>
            {
                op.Parameters[0].Description = "Latitude (e.g., 37.7749)";
                op.Parameters[1].Description = "Longitude (e.g., -122.4194)";
                return op;
            })
            .WithTags("Weather");

            // Transcription endpoint
            app.MapPost("/api/transcribe", async (TranscriptionRequest request, TranscriptionTools tools) =>
            {
                var text = await tools.Transcribe(request);
                return Results.Text(text, "text/plain");
            })
            .WithName("TranscribeAudio")
            .WithSummary("Transcribe audio from YouTube or MP3 using Azure OpenAI Whisper")
            .WithDescription("Submit a transcription request with either a YouTube URL or an MP3 URL in the body.")
            .WithTags("Transcription");

            // Document content extraction endpoint
            app.MapPost("/api/document/extract", async (BlobContentExtractionRequest request, DocumentExtrationTools tools) =>
            {
                var result = await tools.ExtractBlobContent(request);
                return Results.Json(result);
            })
            .WithName("ExtractDocumentContent")
            .WithSummary("Extract key info from PDF document using LLM vision")
            .WithDescription("Downloads PDF document from storage, converts pages to images, and uses Azure OpenAI vision model via Semantic Kernel to extract structured information.")
            .WithTags("Document");

            // Calculator A2A agent - Calculate endpoint
            app.MapPost("/api/calculator/calculate", async (CalculateRequest request, CalculatorA2ATools tools) =>
            {
                var result = await tools.Calculate(request.Expression);
                return Results.Ok(new { expression = request.Expression, result });
            })
            .WithName("CalculateExpression")
            .WithSummary("Calculate arithmetic expressions using Calculator A2A agent")
            .WithDescription("Supports addition (+), subtraction (-), multiplication (*), and division (/). Examples: '5 + 3', '10.5 - 2.3', '7 * 8', '15 / 3'")
            .WithTags("Calculator");

            // Calculator A2A agent - Get agent info endpoint
            app.MapGet("/api/calculator/info", async (CalculatorA2ATools tools) =>
            {
                var info = await tools.GetCalculatorAgentInfo();
                return Results.Ok(info);
            })
            .WithName("GetCalculatorAgentInfo")
            .WithSummary("Get information about the Calculator A2A agent")
            .WithDescription("Returns agent capabilities, skills, supported modes, and other metadata.")
            .WithTags("Calculator");

            return app;
        }
    }

    public record CalculateRequest(string Expression);
}
