using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class ToolService(
    IGraphService graphService,
    IMemoryService memoryService,
    System.Net.Http.HttpClient? httpClient = null,
    IToolExecutionAuditService? auditService = null) : IToolService
{
    private static readonly IReadOnlyDictionary<string, ToolDefinition> ToolDefinitions =
        new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["SearchGraph"] = Definition("SearchGraph", "Search local graph nodes.", ToolRiskLevel.ReadOnly),
            ["SearchMemories"] = Definition("SearchMemories", "Search local durable memories.", ToolRiskLevel.ReadOnly),
            ["CreateNode"] = Definition("CreateNode", "Create a local graph node.", ToolRiskLevel.Mutating),
            ["UpdateNode"] = Definition("UpdateNode", "Update an existing local graph node.", ToolRiskLevel.Mutating),
            ["DeleteNode"] = Definition("DeleteNode", "Permanently delete a graph node and its edges.", ToolRiskLevel.Destructive),
            ["CreateEdge"] = Definition("CreateEdge", "Create a relationship between graph nodes.", ToolRiskLevel.Mutating),
            ["DeleteEdge"] = Definition("DeleteEdge", "Permanently delete a graph relationship.", ToolRiskLevel.Destructive),
            ["SaveMemory"] = Definition("SaveMemory", "Save text as durable local memory.", ToolRiskLevel.Mutating),
            ["WebSearch"] = Definition("WebSearch", "Search the web through the configured local SearXNG service.", ToolRiskLevel.ReadOnly)
        };

    private static readonly HashSet<string> RedactedAuditProperties = new(
        [
            "query",
            "title",
            "summary",
            "body",
            "text",
            "source",
            "content",
            "password",
            "token",
            "secret",
            "apiKey",
            "authorization",
            "error"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly System.Net.Http.HttpClient searchClient =
        httpClient ?? new System.Net.Http.HttpClient();

    public static event Action<string>? OnToolExecuting;
    public static event Action<string, string>? OnToolExecuted;

    public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> tools = ToolDefinitions.Keys.ToList();
        return Task.FromResult(tools);
    }

    public ToolDefinition? GetToolDefinition(string toolName)
    {
        return ToolDefinitions.TryGetValue(toolName, out var definition)
            ? definition
            : null;
    }

    public ToolArgumentValidationResult ValidateArguments(string toolName, string argumentsJson)
    {
        return ToolArgumentContracts.Validate(toolName, argumentsJson);
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteToolAsync(
            new ToolExecutionRequest(toolName, argumentsJson),
            cancellationToken);
        return result.ResultJson;
    }

    public async Task<ToolExecutionResult> ExecuteToolAsync(
        ToolExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var executionId = request.ExecutionId ?? Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var definition = GetToolDefinition(request.ToolName);

        if (definition is not null && definition.RiskLevel != ToolRiskLevel.ReadOnly && request.ExecutionId.HasValue)
        {
            if (auditService is not null)
            {
                var existingAudit = await auditService.GetByExecutionIdAsync(request.ExecutionId.Value, cancellationToken);
                if (existingAudit is not null && existingAudit.Outcome.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    stopwatch.Stop();
                    var idempotentResult = new ToolExecutionResult(
                        request.ExecutionId.Value,
                        Succeeded: true,
                        ValidationFailed: false,
                        ResultJson: "{\"status\": \"idempotent_success\", \"message\": \"This operation was already completed successfully.\"}",
                        Error: null,
                        DurationMilliseconds: 0);
                    OnToolExecuted?.Invoke(definition.Name, idempotentResult.ResultJson);
                    return idempotentResult;
                }
            }
        }

        if (definition is null)
        {
            var unknownResult = JsonSerializer.Serialize(new
            {
                error = $"Unknown tool: {request.ToolName}"
            });
            stopwatch.Stop();
            var result = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: true,
                unknownResult,
                $"Unknown tool: {request.ToolName}",
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request,
                definition,
                result,
                startedAt,
                "unknown_tool",
                cancellationToken);
            return result;
        }

        var validation = ValidateArguments(definition.Name, request.ArgumentsJson);
        if (!validation.IsValid)
        {
            var validationResult = JsonSerializer.Serialize(new
            {
                error = "Tool argument validation failed.",
                validationErrors = validation.Errors
            });
            stopwatch.Stop();
            var result = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: true,
                validationResult,
                string.Join(" ", validation.Errors),
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request with { ArgumentsJson = request.ArgumentsJson },
                definition,
                result,
                startedAt,
                "validation_failed",
                cancellationToken);
            OnToolExecuted?.Invoke(definition.Name, validationResult);
            return result;
        }

        if (definition.RiskLevel != ToolRiskLevel.ReadOnly &&
            !request.ApprovalStatus.Equals("approved", StringComparison.OrdinalIgnoreCase))
        {
            var deniedResult = JsonSerializer.Serialize(new
            {
                error = $"Tool '{definition.Name}' requires explicit approval."
            });
            stopwatch.Stop();
            var result = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: false,
                deniedResult,
                $"Tool '{definition.Name}' requires explicit approval.",
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request with { ArgumentsJson = validation.NormalizedArgumentsJson },
                definition,
                result,
                startedAt,
                "approval_denied",
                cancellationToken);
            OnToolExecuted?.Invoke(definition.Name, deniedResult);
            return result;
        }

        if (definition.RiskLevel != ToolRiskLevel.ReadOnly)
        {
            var startedResult = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: false,
                ResultJson: "{}",
                Error: null,
                DurationMilliseconds: 0);
            var auditStarted = await TryRecordAuditAsync(
                request with { ArgumentsJson = validation.NormalizedArgumentsJson },
                definition,
                startedResult,
                startedAt,
                "started",
                cancellationToken);
            if (!auditStarted)
            {
                stopwatch.Stop();
                var unavailableResultJson = JsonSerializer.Serialize(new
                {
                    error = $"Tool '{definition.Name}' was not executed because the local audit store is unavailable."
                });
                var unavailableResult = new ToolExecutionResult(
                    executionId,
                    Succeeded: false,
                    ValidationFailed: false,
                    unavailableResultJson,
                    $"Tool '{definition.Name}' was not executed because the local audit store is unavailable.",
                    stopwatch.ElapsedMilliseconds);
                OnToolExecuted?.Invoke(definition.Name, unavailableResultJson);
                return unavailableResult;
            }
        }

        OnToolExecuting?.Invoke(definition.Name);
        try
        {
            var resultJson = await ExecuteValidatedAsync(
                definition.Name,
                validation.NormalizedArgumentsJson,
                cancellationToken);
            stopwatch.Stop();
            var succeeded = IsSuccessfulResult(resultJson);
            var error = succeeded ? null : ExtractError(resultJson);
            var result = new ToolExecutionResult(
                executionId,
                succeeded,
                ValidationFailed: false,
                resultJson,
                error,
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request with { ArgumentsJson = validation.NormalizedArgumentsJson },
                definition,
                result,
                startedAt,
                succeeded ? "succeeded" : "failed",
                cancellationToken);
            OnToolExecuted?.Invoke(definition.Name, resultJson);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var result = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: false,
                JsonSerializer.Serialize(new { error = "Tool execution was cancelled." }),
                "Tool execution was cancelled.",
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request with { ArgumentsJson = validation.NormalizedArgumentsJson },
                definition,
                result,
                startedAt,
                "cancelled",
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = $"Execution error: {ex.Message}";
            var resultJson = JsonSerializer.Serialize(new { error });
            var result = new ToolExecutionResult(
                executionId,
                Succeeded: false,
                ValidationFailed: false,
                resultJson,
                error,
                stopwatch.ElapsedMilliseconds);
            _ = await TryRecordAuditAsync(
                request with { ArgumentsJson = validation.NormalizedArgumentsJson },
                definition,
                result,
                startedAt,
                "failed",
                cancellationToken);
            OnToolExecuted?.Invoke(definition.Name, resultJson);
            return result;
        }
    }

    private async Task<string> ExecuteValidatedAsync(
        string toolName,
        string normalizedArgumentsJson,
        CancellationToken cancellationToken)
    {
        return toolName switch
        {
            "SearchGraph" => await HandleSearchGraphAsync(
                ToolArgumentContracts.Deserialize<SearchGraphArguments>(normalizedArgumentsJson),
                cancellationToken),
            "SearchMemories" => await HandleSearchMemoriesAsync(
                ToolArgumentContracts.Deserialize<SearchMemoriesArguments>(normalizedArgumentsJson),
                cancellationToken),
            "CreateNode" => await HandleCreateNodeAsync(
                ToolArgumentContracts.Deserialize<CreateNodeArguments>(normalizedArgumentsJson),
                cancellationToken),
            "UpdateNode" => await HandleUpdateNodeAsync(
                ToolArgumentContracts.Deserialize<UpdateNodeArguments>(normalizedArgumentsJson),
                cancellationToken),
            "DeleteNode" => await HandleDeleteNodeAsync(
                ToolArgumentContracts.Deserialize<DeleteNodeArguments>(normalizedArgumentsJson),
                cancellationToken),
            "CreateEdge" => await HandleCreateEdgeAsync(
                ToolArgumentContracts.Deserialize<CreateEdgeArguments>(normalizedArgumentsJson),
                cancellationToken),
            "DeleteEdge" => await HandleDeleteEdgeAsync(
                ToolArgumentContracts.Deserialize<DeleteEdgeArguments>(normalizedArgumentsJson),
                cancellationToken),
            "SaveMemory" => await HandleSaveMemoryAsync(
                ToolArgumentContracts.Deserialize<SaveMemoryArguments>(normalizedArgumentsJson),
                cancellationToken),
            "WebSearch" => await HandleWebSearchAsync(
                ToolArgumentContracts.Deserialize<WebSearchArguments>(normalizedArgumentsJson),
                cancellationToken),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };
    }

    private async Task<string> HandleSearchGraphAsync(
        SearchGraphArguments arguments,
        CancellationToken cancellationToken)
    {
        var nodes = await graphService.SearchNodesAsync(arguments.Query, cancellationToken);
        var simplified = nodes.Select(node => new
        {
            node.Id,
            node.Title,
            node.Type,
            node.Summary,
            node.Status,
            node.Importance
        }).ToList();

        return JsonSerializer.Serialize(new { success = true, nodes = simplified });
    }

    private async Task<string> HandleSearchMemoriesAsync(
        SearchMemoriesArguments arguments,
        CancellationToken cancellationToken)
    {
        var recalls = await memoryService.RecallWithDetailsAsync(
            arguments.Query,
            arguments.Take,
            cancellationToken);
        var simplified = recalls.Select(recall => new
        {
            recall.Memory.Id,
            recall.Memory.Text,
            recall.Memory.Source,
            recall.Memory.Importance,
            recall.Memory.LinkedNodeId,
            recall.Score,
            Method = recall.Method.ToString(),
            recall.Explanation
        }).ToList();

        return JsonSerializer.Serialize(new { success = true, memories = simplified });
    }

    private async Task<string> HandleCreateNodeAsync(
        CreateNodeArguments arguments,
        CancellationToken cancellationToken)
    {
        var node = new Node
        {
            Title = arguments.Title.Trim(),
            Type = arguments.Type,
            Summary = arguments.Summary,
            Body = arguments.Body,
            Status = arguments.Status,
            Importance = arguments.Importance,
            ColorKey = arguments.ColorKey,
            IconKey = arguments.IconKey
        };

        var created = await graphService.CreateNodeAsync(node, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            node = new { created.Id, created.Title, created.Type }
        });
    }

    private async Task<string> HandleUpdateNodeAsync(
        UpdateNodeArguments arguments,
        CancellationToken cancellationToken)
    {
        var graph = await graphService.GetGraphAsync(cancellationToken);
        var node = graph.Nodes.FirstOrDefault(candidate => candidate.Id == arguments.NodeId);
        if (node is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Node not found: {arguments.NodeId}"
            });
        }

        if (arguments.Title is not null) node.Title = arguments.Title.Trim();
        if (arguments.Type is not null) node.Type = arguments.Type;
        if (arguments.Summary is not null) node.Summary = arguments.Summary;
        if (arguments.Body is not null) node.Body = arguments.Body;
        if (arguments.Status is not null) node.Status = arguments.Status;
        if (arguments.Importance.HasValue) node.Importance = arguments.Importance.Value;
        if (arguments.ColorKey is not null) node.ColorKey = arguments.ColorKey;

        var updated = await graphService.UpdateNodeAsync(node, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            node = new { updated.Id, updated.Title }
        });
    }

    private async Task<string> HandleDeleteNodeAsync(
        DeleteNodeArguments arguments,
        CancellationToken cancellationToken)
    {
        await graphService.DeleteNodeAsync(arguments.NodeId, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            deletedNodeId = arguments.NodeId
        });
    }

    private async Task<string> HandleCreateEdgeAsync(
        CreateEdgeArguments arguments,
        CancellationToken cancellationToken)
    {
        var edge = await graphService.CreateEdgeAsync(
            arguments.SourceNodeId,
            arguments.TargetNodeId,
            arguments.RelationshipType,
            arguments.Strength,
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            edge = new
            {
                edge.Id,
                edge.SourceNodeId,
                edge.TargetNodeId,
                edge.RelationshipType
            }
        });
    }

    private async Task<string> HandleDeleteEdgeAsync(
        DeleteEdgeArguments arguments,
        CancellationToken cancellationToken)
    {
        await graphService.DeleteEdgeAsync(arguments.EdgeId, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            deletedEdgeId = arguments.EdgeId
        });
    }

    private async Task<string> HandleSaveMemoryAsync(
        SaveMemoryArguments arguments,
        CancellationToken cancellationToken)
    {
        var memory = await memoryService.SaveMemoryAsync(
            arguments.Text,
            arguments.Source,
            arguments.Importance,
            arguments.LinkedNodeId,
            cancellationToken);
        return JsonSerializer.Serialize(new
        {
            success = true,
            memory = new { memory.Id, memory.Text, memory.Source }
        });
    }

    private Task<string> HandleWebSearchAsync(
        WebSearchArguments arguments,
        CancellationToken cancellationToken)
    {
        return SearchSearxngAsync(searchClient, arguments.Query, cancellationToken);
    }

    private static async Task<string> SearchSearxngAsync(
        System.Net.Http.HttpClient client,
        string query,
        CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            "http://localhost:8080/search",
            "http://localhost:4000/search",
            "http://127.0.0.1:8080/search"
        };
        foreach (var url in urls)
        {
            try
            {
                var builder = new UriBuilder(url)
                {
                    Query = $"q={Uri.EscapeDataString(query)}&format=json"
                };
                using var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    builder.Uri);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = results.EnumerateArray().Take(5).Select(item => new
                {
                    title = item.TryGetProperty("title", out var title)
                        ? title.GetString()
                        : string.Empty,
                    url = item.TryGetProperty("url", out var link)
                        ? link.GetString()
                        : string.Empty,
                    snippet = item.TryGetProperty("content", out var content)
                        ? content.GetString()
                        : string.Empty
                }).ToList();
                return JsonSerializer.Serialize(new { success = true, results = list });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next configured local endpoint.
            }
        }

        return JsonSerializer.Serialize(new
        {
            error = "SearXNG search service is unreachable. Make sure Docker is running SearXNG on localhost:8080."
        });
    }

    private async Task<bool> TryRecordAuditAsync(
        ToolExecutionRequest request,
        ToolDefinition? definition,
        ToolExecutionResult result,
        DateTimeOffset startedAt,
        string outcome,
        CancellationToken cancellationToken)
    {
        if (auditService is null)
        {
            return false;
        }

        try
        {
            await auditService.RecordAsync(
                new ToolExecutionAudit
                {
                    ExecutionId = result.ExecutionId,
                    AgentRunId = request.AgentRunId,
                    ConversationId = request.ConversationId,
                    ToolName = definition?.Name ?? request.ToolName,
                    RiskLevel = definition?.RiskLevel.ToString() ?? "Unknown",
                    ApprovalStatus = NormalizeApprovalStatus(
                        request.ApprovalStatus,
                        definition?.RiskLevel),
                    Outcome = outcome,
                    ArgumentsSummary = SummarizeJsonForAudit(request.ArgumentsJson),
                    ResultSummary = SummarizeJsonForAudit(result.ResultJson),
                    Error = result.Error is null
                        ? null
                        : RedactDiagnostic(result.Error),
                    StartedAt = startedAt,
                    CompletedAt = startedAt.AddMilliseconds(result.DurationMilliseconds),
                    DurationMilliseconds = result.DurationMilliseconds
                },
                cancellationToken);
            return true;
        }
        catch
        {
            // Mutations require a successful pre-execution write; a failed final
            // update leaves the durable record in the visible "started" state.
            return false;
        }
    }

    private static ToolDefinition Definition(
        string name,
        string description,
        ToolRiskLevel riskLevel)
    {
        return new(
            name,
            description,
            riskLevel,
            ToolArgumentContracts.GetSchema(name));
    }

    private static bool IsSuccessfulResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractError(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : "Tool execution did not return success.";
        }
        catch (JsonException)
        {
            return "Tool execution returned invalid JSON.";
        }
    }

    private static string NormalizeApprovalStatus(
        string approvalStatus,
        ToolRiskLevel? riskLevel)
    {
        if (riskLevel == ToolRiskLevel.ReadOnly)
        {
            return "not_required";
        }

        return string.IsNullOrWhiteSpace(approvalStatus) ||
            approvalStatus.Equals("not_required", StringComparison.OrdinalIgnoreCase)
            ? "unspecified"
            : approvalStatus.Trim().ToLowerInvariant();
    }

    private static string SummarizeJsonForAudit(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Truncate(
                JsonSerializer.Serialize(SummarizeElement(document.RootElement)),
                4000);
        }
        catch (JsonException)
        {
            return "[invalid json omitted]";
        }
    }

    private static object? SummarizeElement(JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && RedactedAuditProperties.Contains(propertyName))
        {
            return element.ValueKind == JsonValueKind.String
                ? $"[redacted:{element.GetString()?.Length ?? 0} chars]"
                : "[redacted]";
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => SummarizeElement(property.Value, property.Name)),
            JsonValueKind.Array => element.EnumerateArray()
                .Take(20)
                .Select(item => SummarizeElement(item))
                .ToList(),
            JsonValueKind.String => Truncate(element.GetString() ?? string.Empty, 300),
            JsonValueKind.Number => element.TryGetInt64(out var integer)
                ? integer
                : element.TryGetDouble(out var number)
                    ? number
                    : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }

    private static string RedactDiagnostic(string value)
    {
        var redacted = value;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            redacted = redacted.Replace(
                userProfile,
                "[user-profile]",
                StringComparison.OrdinalIgnoreCase);
        }

        redacted = Regex.Replace(
            redacted,
            @"https?://[^/\s:@]+:[^@\s/]+@",
            "https://[credentials-redacted]@",
            RegexOptions.IgnoreCase);
        redacted = Regex.Replace(
            redacted,
            @"(?i)(api[_-]?key|token|secret|password)\s*[:=]\s*[^\s,;]+",
            "$1=[redacted]");
        redacted = Regex.Replace(
            redacted,
            @"(?i)\b[A-Z]:\\[^\r\n""']+",
            "[local-path]");
        return Truncate(redacted, 1000);
    }
}
