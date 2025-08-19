using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using mcp_server_hub.Tools;

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

            // YouTube to MP3 endpoint
            app.MapPost("/api/youtube/mp3", async (YouTubeToMp3Request request, YouTubeTools tools) =>
            {
                var result = await tools.DownloadToMp3(request);
                return Results.Ok(result);
            })
            .WithName("DownloadYouTubeToMp3")
            .WithSummary("Download YouTube audio and convert to MP3");

            return app;
        }
    }
}
