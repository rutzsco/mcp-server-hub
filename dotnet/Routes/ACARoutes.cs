using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using mcp_server_hub.Agents;

namespace mcp_server_hub.Routes
{
    public static class ACARoutes
    {
        public static IEndpointRouteBuilder MapACARoutes(this IEndpointRouteBuilder app)
        {
            // Create the task manager - this handles A2A protocol operations
            var taskManager = new TaskManager();

            // Create and attach the calculator agent (instantiate directly, not from DI)
            var calculatorAgent = new CalculatorAgent();
            calculatorAgent.Attach(taskManager);

            // Map the A2A endpoints - JSON-RPC endpoint at /a2a
            app.MapA2A(taskManager, "/a2a");
            
            // Map the well-known agent card endpoint
            app.MapWellKnownAgentCard(taskManager, "/a2a");

            // Add a health check for the calculator agent
            app.MapGet("/a2a/health", () => Results.Ok(new
            {
                Status = "Healthy",
                Agent = "Calculator Agent",
                Timestamp = DateTimeOffset.UtcNow
            }))
            .WithName("ACAHealth")
            .WithSummary("Health check for Calculator Agent")
            .WithTags("A2A", "Health");

            // Add a welcome/info endpoint
            app.MapGet("/a2a", () => Results.Ok(new
            {
                Message = "Calculator Agent is running!",
                Examples = new[] {
                    "5 + 3",
                    "10 - 4",
                    "7 * 8",
                    "15 / 3"
                },
                Endpoints = new
                {
                    AgentCard = "/.well-known/agent-card.json",
                    A2A = "/a2a (POST for JSON-RPC)",
                    Health = "/a2a/health"
                }
            }))
            .WithName("ACAInfo")
            .WithSummary("Calculator Agent information")
            .WithDescription("Provides information about the Calculator Agent, including example expressions and available endpoints.")
            .WithTags("A2A");

            return app;
        }
    }
}
