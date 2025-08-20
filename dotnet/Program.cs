using mcp_server_hub.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Net.Http.Headers;
using mcp_server_hub.Routes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource("*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(b => b.AddMeter("*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithLogging();

builder.Services.AddSingleton(_ =>
{
    var client = new HttpClient() { BaseAddress = new Uri("https://api.squiggle.com.au/") };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mcp-afl-server", "1.0"));
    return client;
});

// Add named HttpClient for Weather.gov API
builder.Services.AddHttpClient("WeatherAPI", client =>
{
    client.BaseAddress = new Uri("https://api.weather.gov/");
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mcp-weather-server", "1.0"));
});

// Add named HttpClient for Azure OpenAI with extended timeout
builder.Services.AddHttpClient("AzureOpenAI", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10); // 10 minutes for transcription operations
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mcp-transcription-server", "1.0"));
});

builder.Services.AddScoped<TimeTools>();
builder.Services.AddScoped<WeatherTools>();
builder.Services.AddScoped<TranscriptionTools>();
builder.Services.AddSingleton<mcp_server_hub.Utilities.BlobStorageUtils>();
builder.Services.AddHttpClient();

var app = builder.Build();

// Add API key middleware
app.UseMiddleware<mcp_server_hub.ApiKeyMiddleware>();

app.MapGet("/api/healthz", () => Results.Ok("Healthy"));

// Minimal API routes for tools
app.MapToolRoutes();

app.MapMcp();

app.Run();