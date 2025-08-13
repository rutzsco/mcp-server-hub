using System;
using System.ComponentModel;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace mcp_server_hub.Tools
{
    public record LocationPoint(
        [property: Description("Latitude in decimal degrees")] double Latitude,
        [property: Description("Longitude in decimal degrees")] double Longitude);

    [McpServerToolType]
    public class WeatherTools
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public WeatherTools(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        [McpServerTool, Description("Get weather forecast for a specified location point")]
        public async Task<string> GetForecast([Description("Location coordinates")] LocationPoint locationPoint)
        {
            if (locationPoint is null)
            {
                throw new ArgumentNullException(nameof(locationPoint));
            }

            using var httpClient = _httpClientFactory.CreateClient("WeatherAPI");

            // Step 1: Get forecast URL for the provided coordinates
            using var pointsResponse = await httpClient.GetAsync($"points/{locationPoint.Latitude},{locationPoint.Longitude}");
            pointsResponse.EnsureSuccessStatusCode();

            var pointsJson = await pointsResponse.Content.ReadAsStringAsync();

            string? forecastUrl = null;
            using (var doc = JsonDocument.Parse(pointsJson))
            {
                if (doc.RootElement.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("forecast", out var forecastProp) &&
                    forecastProp.ValueKind == JsonValueKind.String)
                {
                    forecastUrl = forecastProp.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(forecastUrl))
            {
                throw new InvalidOperationException("Invalid forecast response format: missing properties.forecast URL");
            }

            // Step 2: Retrieve forecast JSON
            using var forecastResponse = await httpClient.GetAsync(forecastUrl);
            forecastResponse.EnsureSuccessStatusCode();

            var forecastJson = await forecastResponse.Content.ReadAsStringAsync();
            return forecastJson;
        }
    }
}
