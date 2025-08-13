# Multi-stage build for MCP server (Streamable HTTP)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["mcp-server-hub.csproj", "."]
RUN dotnet restore "./mcp-server-hub.csproj"
COPY . .
RUN dotnet build "./mcp-server-hub.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./mcp-server-hub.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# ASP.NET Core default port variable adjustment
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "mcp-server-hub.dll"]
