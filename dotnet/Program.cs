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
using Microsoft.OpenApi.Models;

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

// Swagger/OpenAPI v3
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MCP Server Hub API",
        Version = "v1",
        Description = "REST endpoints for tools and health checks"
    });

    // API Key header security scheme
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "X-API-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "API Key required for secured endpoints",
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = "ApiKeyAuth"
        }
    };
    options.AddSecurityDefinition("ApiKeyAuth", securityScheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, Array.Empty<string>() }
    });
});

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
builder.Services.AddScoped<mcp_server_hub.Utilities.MediaUtils>();
builder.Services.AddSingleton<mcp_server_hub.Utilities.BlobStorageUtils>();
builder.Services.AddHttpClient();

var app = builder.Build();

// Enable Swagger UI and JSON before API key middleware so docs are accessible
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "MCP Server Hub API v1");
    c.RoutePrefix = "swagger"; // UI at /swagger
});

// Add API key middleware
app.UseMiddleware<mcp_server_hub.ApiKeyMiddleware>();

app.MapGet("/api/healthz", () => Results.Ok("Healthy"));

// Minimal API routes for tools
app.MapToolRoutes();

app.MapMcp();

app.Run();