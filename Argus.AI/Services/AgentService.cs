using System.Text;
using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public class AgentService(
    IGraphService graphService,
    IMemoryService memoryService,
    IToolService? toolService = null,
    ISettingsService? settingsService = null,
    IAiChatService? aiChatService = null,
    ISoulService? soulService = null,
    IConversationService? conversationService = null) : IAgentService
{
    public async Task<string> RunAsync(string instruction, CancellationToken cancellationToken = default)
    {
        var (_, log) = await RunWithDetailsAsync(instruction, null, cancellationToken);
        return log;
    }

    public async Task<(string FinalAnswer, string ExecutionLog)> RunWithDetailsAsync(string instruction, Guid? conversationId = null, CancellationToken cancellationToken = default)
    {
        if (aiChatService is null || toolService is null || settingsService is null)
        {
            var plan = await PlanAsync(instruction, cancellationToken);
            var builder = new StringBuilder();
            builder.AppendLine($"Instruction: {plan.Instruction}");
            builder.AppendLine($"Matching nodes: {plan.MatchingNodes.Count}");
            foreach (var node in plan.MatchingNodes.Take(5))
            {
                builder.AppendLine($"- Node: {node.Title} ({node.Type})");
            }

            builder.AppendLine($"Matching memories: {plan.MatchingMemories.Count}");
            foreach (var memory in plan.MatchingMemories.Take(5))
            {
                builder.AppendLine($"- Memory: {memory.Text}");
            }

            builder.AppendLine("Proposed actions:");
            foreach (var action in plan.ProposedActions)
            {
                builder.AppendLine($"- {action.ActionType}: {action.Title} - {action.Description}");
            }

            var res = builder.ToString().TrimEnd();
            return (res, res);
        }

        var profile = await settingsService.GetDefaultAiProviderProfileAsync(cancellationToken);
        var soulText = soulService is not null ? await soulService.ReadSoulAsync(cancellationToken) : "You are the Argus Agent.";

        var systemPrompt =
            $$"""
            {{soulText}}

            You are the Argus Agent, working in a Windows-native personal knowledge graph app.
            You have access to the following tools to inspect and modify the graph and memories:
            1. `SearchGraph` (arguments: { "query": "text" }) - searches for graph nodes.
            2. `SearchMemories` (arguments: { "query": "text", "take": 10 }) - searches for local memories.
            3. `CreateNode` (arguments: { "title": "title", "type": "Project|Idea|Task|Decision|Note|Person|File|Link|Conversation|Memory|Tool|Agent", "summary": "brief summary", "body": "longer markdown body", "status": "Active|Completed|Archived", "importance": 1-5, "colorKey": "cyan|magenta|violet|blue|green|yellow|amber|orange|rose|pink", "iconKey": "project|idea|task|decision|note|person|file|link|conversation|memory|tool|agent" }) - creates a new node.
            4. `UpdateNode` (arguments: { "nodeId": "UUID", "title": "title", "type": "type", "summary": "summary", "body": "body", "status": "status", "importance": 1-5, "colorKey": "color" }) - updates an existing node.
            5. `DeleteNode` (arguments: { "nodeId": "UUID" }) - deletes a node.
            6. `CreateEdge` (arguments: { "sourceNodeId": "UUID", "targetNodeId": "UUID", "relationshipType": "related_to|depends_on|inspired_by|belongs_to|blocked_by|uses|created_from|discussed_in|decided_in|reminds_me_of", "strength": 0.1-1.0 }) - connects two nodes.
            7. `DeleteEdge` (arguments: { "edgeId": "UUID" }) - deletes an edge.
            8. `SaveMemory` (arguments: { "text": "text", "source": "agent", "importance": 1-5, "linkedNodeId": "UUID|null" }) - saves a fact/note as local memory.
            9. `WebSearch` (arguments: { "query": "text" }) - searches the web using local SearXNG.

            At each step, output a JSON response matching the following schema:
            {
              "thought": "concise private reasoning for the execution log",
              "tool": "ToolName", // or null if you are done and want to return the final answer
              "arguments": { ... }, // json object for the tool arguments, or null
              "answer": "user-facing final answer when tool is null, otherwise null"
            }

            If you are done, set "tool" to null, "arguments" to null, and put the message the user should see in "answer".
            Never put the execution log in "answer".
            Only output valid JSON. Do not wrap in markdown codeblocks. Do not include extra text outside the JSON.
            """;

        var messages = new List<AiChatTurn>();
        messages.Add(new AiChatTurn("system", systemPrompt));

        if (conversationId.HasValue && conversationService is not null)
        {
            var dbMessages = await conversationService.GetMessagesAsync(conversationId.Value, cancellationToken);
            foreach (var msg in dbMessages)
            {
                messages.Add(new AiChatTurn(msg.Role, msg.Content));
            }
        }

        messages.Add(new AiChatTurn("user", $"Instruction: {instruction}"));

        var logs = new StringBuilder();
        logs.AppendLine($"### Argus Agent Execution Log");
        logs.AppendLine($"**Instruction:** {instruction}");
        logs.AppendLine();

        const int maxSteps = 8;
        string finalAnswer = string.Empty;
        var toolResults = new List<string>();

        for (int step = 1; step <= maxSteps; step++)
        {
            logs.AppendLine($"#### Step {step}");
            var result = await aiChatService.SendAsync(profile, messages, cancellationToken);
            if (result.Error is not null)
            {
                logs.AppendLine($"- **Error:** Provider error: {result.Error}");
                finalAnswer = $"Error: {result.Error}";
                break;
            }

            var jsonText = CleanJsonPayload(result.Content);
            string? thought = null;
            string? toolName = null;
            string? answer = null;
            JsonElement arguments = default;

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;
                thought = root.TryGetProperty("thought", out var thoughtProp) ? thoughtProp.GetString() : null;
                toolName = root.TryGetProperty("tool", out var toolProp) ? toolProp.GetString() : null;
                answer = root.TryGetProperty("answer", out var answerProp) ? answerProp.GetString() : null;
                arguments = root.TryGetProperty("arguments", out var argsProp) ? argsProp.Clone() : default;
            }
            catch (JsonException)
            {
                logs.AppendLine($"- **Thought / Raw Response:** {result.Content}");
                logs.AppendLine($"- **Error:** Failed to parse JSON response. Treating as terminal step.");
                finalAnswer = result.Content;
                break;
            }

            if (!string.IsNullOrWhiteSpace(thought))
            {
                logs.AppendLine($"- **Reasoning:** {thought}");
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                logs.AppendLine($"- **Decision:** Task completed.");
                finalAnswer = string.IsNullOrWhiteSpace(answer)
                    ? BuildFallbackAnswer(instruction, toolResults)
                    : answer.Trim();
                break;
            }

            var argsJson = arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null
                ? "{}"
                : arguments.GetRawText();

            logs.AppendLine($"- **Executing Action:** `{toolName}`");
            logs.AppendLine($"- **Arguments:** `{argsJson}`");

            var toolResult = await toolService.ExecuteToolAsync(toolName, argsJson, cancellationToken);
            toolResults.Add($"{toolName}: {toolResult}");
            logs.AppendLine($"- **Result:** {toolResult}");
            logs.AppendLine();

            messages.Add(new AiChatTurn("assistant", result.Content));
            messages.Add(new AiChatTurn("user", $"Tool `{toolName}` executed. Result: {toolResult}"));
        }

        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            finalAnswer = BuildFallbackAnswer(instruction, toolResults);
        }

        return (finalAnswer, logs.ToString().TrimEnd());
    }

    public async Task<AgentPlan> PlanAsync(string instruction, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeInstruction(instruction);
        var nodes = await SearchNodesForInstructionAsync(normalized, cancellationToken);
        var memories = await SearchMemoriesForInstructionAsync(normalized, cancellationToken);

        var proposals = new List<AgentActionProposal>();
        var bestNode = nodes.FirstOrDefault();
        if (bestNode is not null)
        {
            proposals.Add(new AgentActionProposal(
                "ReviewNode",
                bestNode.Title,
                $"Open or inspect the strongest graph match for '{normalized}'.",
                bestNode.Id));
        }
        else
        {
            proposals.Add(new AgentActionProposal(
                "CreateNode",
                normalized,
                "Create a note node for this instruction if it represents a durable idea.",
                Payload: normalized));
        }

        if (memories.Count > 0)
        {
            var memory = memories[0];
            proposals.Add(new AgentActionProposal(
                "ReviewMemory",
                memory.Source,
                "Use the strongest matching memory as context before taking action.",
                memory.LinkedNodeId,
                memory.Text));
        }
        else
        {
            proposals.Add(new AgentActionProposal(
                "SaveMemory",
                "Capture instruction",
                "Save this instruction as memory only if the user wants it remembered.",
                bestNode?.Id,
                normalized));
        }

        if (bestNode is not null && memories.Any(memory => memory.LinkedNodeId is null))
        {
            proposals.Add(new AgentActionProposal(
                "LinkMemoryToNode",
                bestNode.Title,
                "Link relevant unlinked memories to the matching node after user confirmation.",
                bestNode.Id));
        }

        return new AgentPlan(normalized, nodes.Take(10).ToList(), memories, proposals);
    }

    private static string NormalizeInstruction(string instruction)
    {
        var normalized = instruction.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Instruction cannot be empty.", nameof(instruction));
        }

        return normalized;
    }

    private async Task<IReadOnlyList<Node>> SearchNodesForInstructionAsync(string instruction, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, Node>();
        foreach (var query in BuildSearchQueries(instruction))
        {
            foreach (var node in await graphService.SearchNodesAsync(query, cancellationToken))
            {
                results.TryAdd(node.Id, node);
            }

            if (results.Count >= 10)
            {
                break;
            }
        }

        return results.Values
            .OrderByDescending(node => node.Importance)
            .ThenByDescending(node => node.LastTouchedAt)
            .Take(10)
            .ToList();
    }

    private async Task<IReadOnlyList<Memory>> SearchMemoriesForInstructionAsync(string instruction, CancellationToken cancellationToken)
    {
        return await memoryService.RecallAsync(instruction, 10, cancellationToken);
    }

    private static string CleanJsonPayload(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```json".Length..];
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```".Length..];
        }
        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed[..^"```".Length];
        }
        return trimmed.Trim();
    }

    private static string BuildFallbackAnswer(string instruction, IReadOnlyList<string> toolResults)
    {
        if (toolResults.Count == 0)
        {
            return "Done.";
        }

        var summary = string.Join(Environment.NewLine, toolResults.Take(3).Select(result => $"- {result}"));
        return $"Done. I handled: {instruction.Trim()}{Environment.NewLine}{summary}";
    }

    private static IReadOnlyList<string> BuildSearchQueries(string instruction)
    {
        var terms = instruction
            .Split([' ', '\t', '\r', '\n', ',', '.', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        terms.Insert(0, instruction);
        return terms;
    }
}
