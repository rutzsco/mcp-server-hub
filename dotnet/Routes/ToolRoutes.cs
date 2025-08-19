using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using mcp_server_hub.Tools;
using mcp_server_hub.Utilities;

namespace mcp_server_hub.Routes
{
    public static class ToolRoutes
    {
        public static IEndpointRouteBuilder MapToolRoutes(this IEndpointRouteBuilder app)
        {
            // Time endpoint
            app.MapGet("/api/time", () => Results.Ok(TimeTools.GetCurrentTime()))
               .WithName("GetCurrentTime")
               .WithSummary("Get the current time (UTC and local)");

            // Weather endpoint (forecast)
            app.MapGet("/api/weather/forecast", async (double lat, double lon, WeatherTools tools) =>
            {
                var json = await tools.GetForecast(new LocationPoint(lat, lon));
                return Results.Content(json, "application/json");
            })
            .WithName("GetWeatherForecast")
            .WithSummary("Get weather forecast for provided coordinates");

            // YouTube to MP3 endpoint removed; superseded by /api/transcribe

            // Transcription endpoint
            app.MapPost("/api/transcribe", async (TranscriptionRequest request, TranscriptionTools tools) =>
            {
                var text = await tools.Transcribe(request);
                return Results.Text(text, "text/plain");
            })
            .WithName("TranscribeAudio")
            .WithSummary("Transcribe audio from YouTube or MP3 using Azure OpenAI Whisper");

            return app;
        }
    }
}
