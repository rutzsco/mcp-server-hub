# mcp-server-hub

Minimal .NET 8 remote MCP server using Streamable HTTP transport.

## Features
- Streamable HTTP + legacy SSE endpoints via `app.MapMcp()`
- Health probe at `/api/healthz`
- Optional API key authentication
- OpenTelemetry wiring (traces + metrics + logs) – add an OTLP endpoint via env vars later
- Dockerfile exposing port 8080 (container) / configurable via `ASPNETCORE_URLS`
 - Swagger/OpenAPI 3 docs at `/swagger`

## Prerequisites
- .NET 8 SDK
- (Optional) Docker

## API Key Authentication

The server supports optional API key authentication. When an API key is configured, all requests (except health checks) must include the `X-API-Key` header with a valid API key.

### Configuration

Set the API key via configuration:

**appsettings.json:**
```json
{
  "ApiKey": "your-secret-api-key-here"
}
```

**Environment variable:**
```bash
export ApiKey="your-secret-api-key-here"
```

**For Docker:**
```bash
docker run -p 8080:8080 -e ApiKey="your-secret-api-key-here" mcp-server-hub
```

### Usage

If an API key is configured, include it in all requests:

```bash
curl -H "X-API-Key: your-secret-api-key-here" http://localhost:5000/mcp/sse
```

**Note:** If no API key is configured in the settings, authentication is disabled and requests proceed without validation.

## Run locally
```
dotnet run --project ./mcp-server-hub/mcp-server-hub.csproj
```
Browse: http://localhost:5000/api/healthz (or the port shown in console)

### API docs (Swagger)
When running locally, browse to `/swagger` for interactive API documentation.
If an API key is configured, click the "Authorize" button and enter the header value for `X-API-Key` to try secured endpoints.

## Add to VS Code (SSE transport)
When containerized or deployed, use the `/mcp/sse` endpoint produced by `MapMcp("/mcp")` for legacy SSE connections if stateful mode is enabled (default).

Example `mcp.json` snippet:
```
{
	"mcp": {
		"servers": {
			"mcp-server-hub": {
			"url": "http://localhost:5000/mcp/sse",
				"type": "sse"
			}
		}
	}
}
```

## Docker build & run
```
docker build -t mcp-server-hub ./mcp-server-hub
docker run -p 8080:8080 mcp-server-hub
```
Health: http://localhost:8080/api/healthz

## Adding tools
1. Add `using ModelContextProtocol.Server;`
2. Create a static class with `[McpTool("tool-name")]` attributed methods.
3. Ensure project references `ModelContextProtocol.AspNetCore` (already added) and keep `.WithToolsFromAssembly()` in `Program.cs`.

Example:
```csharp
[McpTool("echo", Description = "Echo a message")] 
public static EchoResult Echo(string message) => new(message, DateTimeOffset.UtcNow);
public record EchoResult(string Message, DateTimeOffset Timestamp);
```

## YouTube ➜ MP3 tool (transcription-ready)

This repo includes a tool that downloads a YouTube video's audio and converts it to an MP3 optimized for transcription (16 kHz mono, 64 kbps by default).

### Dependencies
- NuGet packages (already added):
  - YoutubeExplode
  - FFMpegCore
- FFmpeg binary on PATH or placed alongside the app binaries

### FFmpeg setup (Windows)
1. Download a static build from https://ffmpeg.org/download.html (or gyan.dev/ffmpeg/builds/).
2. Extract and locate `ffmpeg.exe` (and optionally `ffprobe.exe`).
3. Place `ffmpeg.exe` in the app's output folder so the server can find it at runtime:
	- Debug: `./dotnet/bin/Debug/net8.0/`
	- Release/publish: the folder with `mcp-server-hub.dll`

The tool looks in `AppContext.BaseDirectory` for ffmpeg.

### Call from MCP
1. List tools to discover the exact tool name:
	- POST `tools/list` (see `requests.http`).
2. Call the tool with `tools/call` using the returned name and arguments below.

Arguments for the method:
- `url` (string, required): YouTube video URL
- `outputPath` (string, optional): File name or full path for the MP3. If only a file name is given, it saves to the app output directory.
- `sampleRateHz` (int, optional, default 16000)
- `channels` (int, optional, default 1)
- `bitrateKbps` (int, optional, default 64)

Returns:
- `outputPath` (string): Absolute path to the saved MP3
- `title` (string): Video title
- `duration` (TimeSpan?): Approximate video duration

Note: Ensure you comply with YouTube's Terms of Service and applicable law when downloading content.

## OpenTelemetry export (optional)
Set environment variables (example for OTLP gRPC):
```
OTEL_EXPORTER_OTLP_ENDPOINT=https://otel-collector.example.com
OTEL_SERVICE_NAME=mcp-server-hub
```

## Deployment notes
- Container listens on 8080
- Configure platform ingress (Azure Container Apps, App Service, etc.) to target port 8080
- Health probe path: `/api/healthz`

## Next steps
- Implement real tools (domain-specific logic)
- Add integration tests for tool methods
- Consider rate limiting for production use