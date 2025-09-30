using A2A;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace mcp_server_hub.Tools
{
    /// <summary>
    /// MCP tool that communicates with the Calculator A2A agent.
    /// </summary>
    [McpServerToolType]
    public class CalculatorA2ATools
    {
        private readonly string _calculatorAgentUrl;
        private readonly A2AClient _client;

        public CalculatorA2ATools(IConfiguration configuration)
        {
            // Get calculator agent URL from configuration, default to local endpoint
            _calculatorAgentUrl = configuration["CalculatorAgent:Url"] ?? "https://localhost:7077/";
            _client = new A2AClient(new Uri(_calculatorAgentUrl));
        }

        /// <summary>
        /// Performs arithmetic calculations using the Calculator A2A agent.
        /// </summary>
        /// <param name="expression">Math expression to calculate (e.g., "5 + 3", "10 * 7", "15 / 3")</param>
        /// <returns>The calculated result</returns>
        [McpServerTool, Description("Calculate arithmetic expressions using the Calculator A2A agent. Supports addition (+), subtraction (-), multiplication (*), and division (/).")]
        public async Task<string> Calculate(
            [Description("Math expression to calculate (e.g., '5 + 3', '10.5 - 2.3', '7 * 8', '15 / 3')")] 
            string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new ArgumentException("Expression cannot be empty", nameof(expression));
            }

            try
            {
                // Create a message for the calculator agent
                var message = new AgentMessage
                {
                    Role = MessageRole.User,
                    MessageId = Guid.NewGuid().ToString(),
                    ContextId = Guid.NewGuid().ToString(),
                    Parts = [new TextPart { Text = expression }]
                };

                // Send the message to the calculator agent
                var response = await _client.SendMessageAsync(new MessageSendParams 
                { 
                    Message = message 
                });

                // Extract the text from the response
                if (response is AgentMessage responseMessage)
                {
                    var textPart = responseMessage.Parts.OfType<TextPart>().FirstOrDefault();
                    return textPart?.Text ?? "No response from calculator agent";
                }

                return "Unexpected response format from calculator agent";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to calculate '{expression}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets information about the Calculator A2A agent.
        /// </summary>
        /// <returns>Agent card information</returns>
        [McpServerTool, Description("Get information about the Calculator A2A agent including its capabilities and skills.")]
        public async Task<CalculatorAgentInfo> GetCalculatorAgentInfo()
        {
            try
            {
                var cardResolver = new A2ACardResolver(new Uri(_calculatorAgentUrl));
                var agentCard = await cardResolver.GetAgentCardAsync();

                return new CalculatorAgentInfo(
                    Name: agentCard.Name,
                    Description: agentCard.Description,
                    Version: agentCard.Version,
                    Url: agentCard.Url,
                    SupportsStreaming: agentCard.Capabilities?.Streaming ?? false,
                    InputModes: agentCard.DefaultInputModes ?? new System.Collections.Generic.List<string>(),
                    OutputModes: agentCard.DefaultOutputModes ?? new System.Collections.Generic.List<string>(),
                    SkillCount: agentCard.Skills?.Count ?? 0
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to retrieve calculator agent info: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Information about the Calculator A2A agent.
    /// </summary>
    public record CalculatorAgentInfo(
        [property: Description("Agent name")] string Name,
        [property: Description("Agent description")] string Description,
        [property: Description("Agent version")] string Version,
        [property: Description("Agent URL")] string Url,
        [property: Description("Whether the agent supports streaming")] bool SupportsStreaming,
        [property: Description("Supported input modes")] System.Collections.Generic.List<string> InputModes,
        [property: Description("Supported output modes")] System.Collections.Generic.List<string> OutputModes,
        [property: Description("Number of skills available")] int SkillCount
    );
}
