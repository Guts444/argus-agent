using System.Text.Json;
using Argus.Core.Graph;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public sealed class ToolService(
    IGraphService graphService,
    IMemoryService memoryService,
    System.Net.Http.HttpClient? httpClient = null) : IToolService
{
    private static readonly IReadOnlyList<string> ToolList = new[]
    {
        "SearchGraph",
        "SearchMemories",
        "CreateNode",
        "UpdateNode",
        "DeleteNode",
        "CreateEdge",
        "DeleteEdge",
        "SaveMemory",
        "WebSearch"
    };

    public static event Action<string>? OnToolExecuting;
    public static event Action<string, string>? OnToolExecuted;

    public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ToolList);
    }

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        OnToolExecuting?.Invoke(toolName);
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            var result = await (toolName switch
            {
                "SearchGraph" => HandleSearchGraphAsync(root, cancellationToken),
                "SearchMemories" => HandleSearchMemoriesAsync(root, cancellationToken),
                "CreateNode" => HandleCreateNodeAsync(root, cancellationToken),
                "UpdateNode" => HandleUpdateNodeAsync(root, cancellationToken),
                "DeleteNode" => HandleDeleteNodeAsync(root, cancellationToken),
                "CreateEdge" => HandleCreateEdgeAsync(root, cancellationToken),
                "DeleteEdge" => HandleDeleteEdgeAsync(root, cancellationToken),
                "SaveMemory" => HandleSaveMemoryAsync(root, cancellationToken),
                "WebSearch" => HandleWebSearchAsync(root, cancellationToken),
                _ => Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" }))
            });

            OnToolExecuted?.Invoke(toolName, result);
            return result;
        }
        catch (JsonException ex)
        {
            var err = JsonSerializer.Serialize(new { error = $"Invalid JSON arguments: {ex.Message}" });
            OnToolExecuted?.Invoke(toolName, err);
            return err;
        }
        catch (Exception ex)
        {
            var err = JsonSerializer.Serialize(new { error = $"Execution error: {ex.Message}" });
            OnToolExecuted?.Invoke(toolName, err);
            return err;
        }
    }

    private async Task<string> HandleSearchGraphAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var query = root.GetProperty("query").GetString() ?? string.Empty;
        var nodes = await graphService.SearchNodesAsync(query, cancellationToken);
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

    private async Task<string> HandleSearchMemoriesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var query = root.GetProperty("query").GetString() ?? string.Empty;
        var take = root.TryGetProperty("take", out var takeProp) ? takeProp.GetInt32() : 10;
        var memories = await memoryService.SearchMemoriesAsync(query, take, cancellationToken);
        var simplified = memories.Select(m => new
        {
            m.Id,
            m.Text,
            m.Source,
            m.Importance,
            m.LinkedNodeId
        }).ToList();

        return JsonSerializer.Serialize(new { success = true, memories = simplified });
    }

    private async Task<string> HandleCreateNodeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var node = new Node
        {
            Title = root.GetProperty("title").GetString() ?? "New Node",
            Type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "Note" : "Note",
            Summary = root.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() ?? string.Empty : string.Empty,
            Body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty,
            Status = root.TryGetProperty("status", out var statProp) ? statProp.GetString() ?? "Active" : "Active",
            Importance = root.TryGetProperty("importance", out var impProp) ? impProp.GetInt32() : 3,
            ColorKey = root.TryGetProperty("colorKey", out var colorProp) ? colorProp.GetString() ?? "cyan" : "cyan",
            IconKey = root.TryGetProperty("iconKey", out var iconProp) ? iconProp.GetString() ?? "idea" : "idea"
        };

        var created = await graphService.CreateNodeAsync(node, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, node = new { created.Id, created.Title, created.Type } });
    }

    private async Task<string> HandleUpdateNodeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = root.GetProperty("nodeId").GetGuid();
        // Since we need to update, fetch all nodes first to find this node.
        // Wait, graphService doesn't have a direct GetNodeByIdAsync but we can get the graph.
        var graph = await graphService.GetGraphAsync(cancellationToken);
        var node = graph.Nodes.FirstOrDefault(n => n.Id == id);
        if (node is null)
        {
            return JsonSerializer.Serialize(new { error = $"Node not found: {id}" });
        }

        if (root.TryGetProperty("title", out var titleProp)) node.Title = titleProp.GetString() ?? node.Title;
        if (root.TryGetProperty("type", out var typeProp)) node.Type = typeProp.GetString() ?? node.Type;
        if (root.TryGetProperty("summary", out var sumProp)) node.Summary = sumProp.GetString() ?? node.Summary;
        if (root.TryGetProperty("body", out var bodyProp)) node.Body = bodyProp.GetString() ?? node.Body;
        if (root.TryGetProperty("status", out var statProp)) node.Status = statProp.GetString() ?? node.Status;
        if (root.TryGetProperty("importance", out var impProp)) node.Importance = impProp.GetInt32();
        if (root.TryGetProperty("colorKey", out var colorProp)) node.ColorKey = colorProp.GetString() ?? node.ColorKey;

        var updated = await graphService.UpdateNodeAsync(node, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, node = new { updated.Id, updated.Title } });
    }

    private async Task<string> HandleDeleteNodeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = root.GetProperty("nodeId").GetGuid();
        await graphService.DeleteNodeAsync(id, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, deletedNodeId = id });
    }

    private async Task<string> HandleCreateEdgeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var sourceId = root.GetProperty("sourceNodeId").GetGuid();
        var targetId = root.GetProperty("targetNodeId").GetGuid();
        var relType = root.TryGetProperty("relationshipType", out var relProp) ? relProp.GetString() ?? "related_to" : "related_to";
        var strength = root.TryGetProperty("strength", out var strProp) ? strProp.GetDouble() : 0.7;

        var edge = await graphService.CreateEdgeAsync(sourceId, targetId, relType, strength, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, edge = new { edge.Id, edge.SourceNodeId, edge.TargetNodeId, edge.RelationshipType } });
    }

    private async Task<string> HandleDeleteEdgeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var id = root.GetProperty("edgeId").GetGuid();
        await graphService.DeleteEdgeAsync(id, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, deletedEdgeId = id });
    }

    private async Task<string> HandleSaveMemoryAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var text = root.GetProperty("text").GetString() ?? string.Empty;
        var source = root.TryGetProperty("source", out var srcProp) ? srcProp.GetString() ?? "agent" : "agent";
        var importance = root.TryGetProperty("importance", out var impProp) ? impProp.GetInt32() : 3;
        Guid? linkedNodeId = null;
        if (root.TryGetProperty("linkedNodeId", out var linkProp) && linkProp.ValueKind != JsonValueKind.Null)
        {
            linkedNodeId = linkProp.GetGuid();
        }

        var memory = await memoryService.SaveMemoryAsync(text, source, importance, linkedNodeId, cancellationToken);
        return JsonSerializer.Serialize(new { success = true, memory = new { memory.Id, memory.Text, memory.Source } });
    }

    private async Task<string> HandleWebSearchAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var query = root.GetProperty("query").GetString() ?? string.Empty;
        var client = httpClient ?? new System.Net.Http.HttpClient();
        return await SearchSearxngAsync(client, query, cancellationToken);
    }

    private async Task<string> SearchSearxngAsync(System.Net.Http.HttpClient client, string query, CancellationToken ct)
    {
        var urls = new[] { "http://localhost:8080/search", "http://localhost:4000/search", "http://127.0.0.1:8080/search" };
        foreach (var url in urls)
        {
            try
            {
                var builder = new UriBuilder(url)
                {
                    Query = $"q={Uri.EscapeDataString(query)}&format=json"
                };
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, builder.Uri);
                var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                    {
                        var list = new System.Collections.Generic.List<object>();
                        foreach (var item in resultsProp.EnumerateArray().Take(5))
                        {
                            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                            var link = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                            var snippet = item.TryGetProperty("content", out var c) ? c.GetString() : "";
                            list.Add(new { title, url = link, snippet });
                        }
                        return JsonSerializer.Serialize(new { success = true, results = list });
                    }
                }
            }
            catch
            {
                // Try next URL
            }
        }
        return JsonSerializer.Serialize(new { error = "SearXNG search service is unreachable. Make sure Docker is running SearXNG on localhost:8080." });
    }
}
