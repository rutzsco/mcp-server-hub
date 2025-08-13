# mcp-server-hub

Minimal .NET 8 remote MCP server using Streamable HTTP transport.

## Features
- Streamable HTTP + legacy SSE endpoints via `app.MapMcp()`
- Health probe at `/api/healthz`
- OpenTelemetry wiring (traces + metrics + logs) – add an OTLP endpoint via env vars later
- Dockerfile exposing port 8080 (container) / configurable via `ASPNETCORE_URLS`

## Prerequisites
- .NET 8 SDK
- (Optional) Docker

## Run locally
```
dotnet run --project ./mcp-server-hub/mcp-server-hub.csproj
```
Browse: http://localhost:5000/api/healthz (or the port shown in console)

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
- Add authorization / API keys once supported
- Add integration tests for tool methods