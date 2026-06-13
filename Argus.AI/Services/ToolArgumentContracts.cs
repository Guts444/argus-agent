using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Core.Services;

namespace Argus.AI.Services;

internal static class ToolArgumentContracts
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<string, ToolContract> Contracts =
        new Dictionary<string, ToolContract>(StringComparer.OrdinalIgnoreCase)
        {
            ["SearchGraph"] = Contract<SearchGraphArguments>(
                ["query"],
                """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string","minLength":1,"maxLength":500}}}"""),
            ["SearchMemories"] = Contract<SearchMemoriesArguments>(
                ["query", "take"],
                """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string","minLength":1,"maxLength":500},"take":{"type":"integer","minimum":1,"maximum":50}}}"""),
            ["CreateNode"] = Contract<CreateNodeArguments>(
                ["title", "type", "summary", "body", "status", "importance", "colorKey", "iconKey"],
                """{"type":"object","additionalProperties":false,"required":["title"],"properties":{"title":{"type":"string","minLength":1,"maxLength":220},"type":{"type":"string","enum":["Project","Idea","Task","Decision","Note","Person","File","Link","Conversation","Memory","Tool","Agent"]},"summary":{"type":"string","maxLength":2000},"body":{"type":"string","maxLength":50000},"status":{"type":"string","enum":["Active","Completed","Archived"]},"importance":{"type":"integer","minimum":1,"maximum":5},"colorKey":{"type":"string","enum":["cyan","magenta","violet","blue","green","yellow","amber","orange","rose","pink"]},"iconKey":{"type":"string","enum":["project","idea","task","decision","note","person","file","link","conversation","memory","tool","agent"]}}}"""),
            ["UpdateNode"] = Contract<UpdateNodeArguments>(
                ["nodeId", "title", "type", "summary", "body", "status", "importance", "colorKey"],
                """{"type":"object","additionalProperties":false,"required":["nodeId"],"properties":{"nodeId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":220},"type":{"type":"string","enum":["Project","Idea","Task","Decision","Note","Person","File","Link","Conversation","Memory","Tool","Agent"]},"summary":{"type":"string","maxLength":2000},"body":{"type":"string","maxLength":50000},"status":{"type":"string","enum":["Active","Completed","Archived"]},"importance":{"type":"integer","minimum":1,"maximum":5},"colorKey":{"type":"string","enum":["cyan","magenta","violet","blue","green","yellow","amber","orange","rose","pink"]}}}"""),
            ["DeleteNode"] = Contract<DeleteNodeArguments>(
                ["nodeId"],
                """{"type":"object","additionalProperties":false,"required":["nodeId"],"properties":{"nodeId":{"type":"string","format":"uuid"}}}"""),
            ["CreateEdge"] = Contract<CreateEdgeArguments>(
                ["sourceNodeId", "targetNodeId", "relationshipType", "strength"],
                """{"type":"object","additionalProperties":false,"required":["sourceNodeId","targetNodeId"],"properties":{"sourceNodeId":{"type":"string","format":"uuid"},"targetNodeId":{"type":"string","format":"uuid"},"relationshipType":{"type":"string","enum":["related_to","depends_on","inspired_by","belongs_to","blocked_by","uses","created_from","discussed_in","decided_in","reminds_me_of"]},"strength":{"type":"number","minimum":0.1,"maximum":1.0}}}"""),
            ["DeleteEdge"] = Contract<DeleteEdgeArguments>(
                ["edgeId"],
                """{"type":"object","additionalProperties":false,"required":["edgeId"],"properties":{"edgeId":{"type":"string","format":"uuid"}}}"""),
            ["SaveMemory"] = Contract<SaveMemoryArguments>(
                ["text", "source", "importance", "linkedNodeId"],
                """{"type":"object","additionalProperties":false,"required":["text"],"properties":{"text":{"type":"string","minLength":1,"maxLength":10000},"source":{"type":"string","maxLength":120},"importance":{"type":"integer","minimum":1,"maximum":5},"linkedNodeId":{"type":["string","null"],"format":"uuid"}}}"""),
            ["WebSearch"] = Contract<WebSearchArguments>(
                ["query"],
                """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string","minLength":1,"maxLength":500}}}""")
        };

    public static string GetSchema(string toolName)
    {
        return Contracts.TryGetValue(toolName, out var contract)
            ? contract.Schema
            : "{}";
    }

    public static ToolArgumentValidationResult Validate(string toolName, string argumentsJson)
    {
        if (!Contracts.TryGetValue(toolName, out var contract))
        {
            return new(false, "{}", [$"Unknown tool: {toolName}"]);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(false, "{}", ["Arguments must be a JSON object."]);
            }

            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    errors.Add($"Duplicate property '{property.Name}' is not allowed.");
                }
                else if (!contract.AllowedProperties.Contains(property.Name))
                {
                    errors.Add($"Unknown property '{property.Name}'.");
                }
            }

            var arguments = JsonSerializer.Deserialize(
                argumentsJson,
                contract.ArgumentType,
                JsonOptions);
            if (arguments is not IValidatedToolArguments validated)
            {
                errors.Add("Arguments could not be parsed.");
            }
            else
            {
                errors.AddRange(validated.Validate());
            }

            return errors.Count == 0 && arguments is not null
                ? new(true, JsonSerializer.Serialize(arguments, contract.ArgumentType, JsonOptions), [])
                : new(false, "{}", errors);
        }
        catch (JsonException ex)
        {
            return new(false, "{}", [$"Invalid JSON arguments: {ex.Message}"]);
        }
    }

    public static T Deserialize<T>(string normalizedArgumentsJson)
        where T : IValidatedToolArguments
    {
        return JsonSerializer.Deserialize<T>(normalizedArgumentsJson, JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
    }

    private static ToolContract Contract<T>(
        IEnumerable<string> allowedProperties,
        string schema)
        where T : IValidatedToolArguments
    {
        return new(
            typeof(T),
            allowedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase),
            schema);
    }

    private sealed record ToolContract(
        Type ArgumentType,
        IReadOnlySet<string> AllowedProperties,
        string Schema);
}

internal interface IValidatedToolArguments
{
    IEnumerable<string> Validate();
}

internal sealed record SearchGraphArguments : IValidatedToolArguments
{
    public string Query { get; init; } = string.Empty;

    public IEnumerable<string> Validate() =>
        ToolArgumentValidation.RequiredText(Query, "query", 500);
}

internal sealed record SearchMemoriesArguments : IValidatedToolArguments
{
    public string Query { get; init; } = string.Empty;
    public int Take { get; init; } = 10;

    public IEnumerable<string> Validate()
    {
        foreach (var error in ToolArgumentValidation.RequiredText(Query, "query", 500))
        {
            yield return error;
        }
        if (Take is < 1 or > 50)
        {
            yield return "'take' must be between 1 and 50.";
        }
    }
}

internal sealed record CreateNodeArguments : IValidatedToolArguments
{
    public string Title { get; init; } = string.Empty;
    public string Type { get; init; } = "Note";
    public string Summary { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Status { get; init; } = "Active";
    public int Importance { get; init; } = 3;
    public string ColorKey { get; init; } = "cyan";
    public string IconKey { get; init; } = "idea";

    public IEnumerable<string> Validate()
    {
        foreach (var error in ToolArgumentValidation.RequiredText(Title, "title", 220))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.OptionalText(Summary, "summary", 2000))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.OptionalText(Body, "body", 50000))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.NodeType(Type))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.NodeStatus(Status))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.Importance(Importance))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.Color(ColorKey))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.Icon(IconKey))
        {
            yield return error;
        }
    }
}

internal sealed record UpdateNodeArguments : IValidatedToolArguments
{
    public Guid NodeId { get; init; }
    public string? Title { get; init; }
    public string? Type { get; init; }
    public string? Summary { get; init; }
    public string? Body { get; init; }
    public string? Status { get; init; }
    public int? Importance { get; init; }
    public string? ColorKey { get; init; }

    public IEnumerable<string> Validate()
    {
        foreach (var error in ToolArgumentValidation.RequiredGuid(NodeId, "nodeId"))
        {
            yield return error;
        }
        if (Title is null &&
            Type is null &&
            Summary is null &&
            Body is null &&
            Status is null &&
            !Importance.HasValue &&
            ColorKey is null)
        {
            yield return "At least one node field must be supplied for update.";
        }
        foreach (var error in ToolArgumentValidation.OptionalNonEmptyText(Title, "title", 220))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.OptionalText(Summary, "summary", 2000))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.OptionalText(Body, "body", 50000))
        {
            yield return error;
        }
        if (Type is not null)
        {
            foreach (var error in ToolArgumentValidation.NodeType(Type))
            {
                yield return error;
            }
        }
        if (Status is not null)
        {
            foreach (var error in ToolArgumentValidation.NodeStatus(Status))
            {
                yield return error;
            }
        }
        if (Importance.HasValue)
        {
            foreach (var error in ToolArgumentValidation.Importance(Importance.Value))
            {
                yield return error;
            }
        }
        if (ColorKey is not null)
        {
            foreach (var error in ToolArgumentValidation.Color(ColorKey))
            {
                yield return error;
            }
        }
    }
}

internal sealed record DeleteNodeArguments : IValidatedToolArguments
{
    public Guid NodeId { get; init; }

    public IEnumerable<string> Validate() =>
        ToolArgumentValidation.RequiredGuid(NodeId, "nodeId");
}

internal sealed record CreateEdgeArguments : IValidatedToolArguments
{
    public Guid SourceNodeId { get; init; }
    public Guid TargetNodeId { get; init; }
    public string RelationshipType { get; init; } = "related_to";
    public double Strength { get; init; } = 0.7;

    public IEnumerable<string> Validate()
    {
        foreach (var error in ToolArgumentValidation.RequiredGuid(SourceNodeId, "sourceNodeId"))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.RequiredGuid(TargetNodeId, "targetNodeId"))
        {
            yield return error;
        }
        if (SourceNodeId != Guid.Empty && SourceNodeId == TargetNodeId)
        {
            yield return "An edge cannot connect a node to itself.";
        }
        if (!ToolArgumentValidation.RelationshipTypes.Contains(RelationshipType))
        {
            yield return $"'relationshipType' must be one of: {string.Join(", ", ToolArgumentValidation.RelationshipTypes)}.";
        }
        if (Strength is < 0.1 or > 1.0)
        {
            yield return "'strength' must be between 0.1 and 1.0.";
        }
    }
}

internal sealed record DeleteEdgeArguments : IValidatedToolArguments
{
    public Guid EdgeId { get; init; }

    public IEnumerable<string> Validate() =>
        ToolArgumentValidation.RequiredGuid(EdgeId, "edgeId");
}

internal sealed record SaveMemoryArguments : IValidatedToolArguments
{
    public string Text { get; init; } = string.Empty;
    public string Source { get; init; } = "agent";
    public int Importance { get; init; } = 3;
    public Guid? LinkedNodeId { get; init; }

    public IEnumerable<string> Validate()
    {
        foreach (var error in ToolArgumentValidation.RequiredText(Text, "text", 10000))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.RequiredText(Source, "source", 120))
        {
            yield return error;
        }
        foreach (var error in ToolArgumentValidation.Importance(Importance))
        {
            yield return error;
        }
        if (LinkedNodeId == Guid.Empty)
        {
            yield return "'linkedNodeId' must be a valid UUID or null.";
        }
    }
}

internal sealed record WebSearchArguments : IValidatedToolArguments
{
    public string Query { get; init; } = string.Empty;

    public IEnumerable<string> Validate() =>
        ToolArgumentValidation.RequiredText(Query, "query", 500);
}

internal static class ToolArgumentValidation
{
    public static readonly IReadOnlySet<string> NodeTypes = new HashSet<string>(
        ["Project", "Idea", "Task", "Decision", "Note", "Person", "File", "Link", "Conversation", "Memory", "Tool", "Agent"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> NodeStatuses = new HashSet<string>(
        ["Active", "Completed", "Archived"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Colors = new HashSet<string>(
        ["cyan", "magenta", "violet", "blue", "green", "yellow", "amber", "orange", "rose", "pink"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Icons = new HashSet<string>(
        ["project", "idea", "task", "decision", "note", "person", "file", "link", "conversation", "memory", "tool", "agent"],
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> RelationshipTypes = new HashSet<string>(
        ["related_to", "depends_on", "inspired_by", "belongs_to", "blocked_by", "uses", "created_from", "discussed_in", "decided_in", "reminds_me_of"],
        StringComparer.Ordinal);

    public static IEnumerable<string> RequiredText(string value, string property, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield return $"'{property}' is required.";
        }
        else if (value.Length > maxLength)
        {
            yield return $"'{property}' must be at most {maxLength} characters.";
        }
    }

    public static IEnumerable<string> OptionalNonEmptyText(string? value, string property, int maxLength)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            yield return $"'{property}' cannot be empty when supplied.";
        }
        foreach (var error in OptionalText(value, property, maxLength))
        {
            yield return error;
        }
    }

    public static IEnumerable<string> OptionalText(string? value, string property, int maxLength)
    {
        if (value?.Length > maxLength)
        {
            yield return $"'{property}' must be at most {maxLength} characters.";
        }
    }

    public static IEnumerable<string> RequiredGuid(Guid value, string property)
    {
        if (value == Guid.Empty)
        {
            yield return $"'{property}' must be a valid non-empty UUID.";
        }
    }

    public static IEnumerable<string> NodeType(string value)
    {
        if (!NodeTypes.Contains(value))
        {
            yield return $"'type' must be one of: {string.Join(", ", NodeTypes)}.";
        }
    }

    public static IEnumerable<string> NodeStatus(string value)
    {
        if (!NodeStatuses.Contains(value))
        {
            yield return $"'status' must be one of: {string.Join(", ", NodeStatuses)}.";
        }
    }

    public static IEnumerable<string> Importance(int value)
    {
        if (value is < 1 or > 5)
        {
            yield return "'importance' must be between 1 and 5.";
        }
    }

    public static IEnumerable<string> Color(string value)
    {
        if (!Colors.Contains(value))
        {
            yield return $"'colorKey' must be one of: {string.Join(", ", Colors)}.";
        }
    }

    public static IEnumerable<string> Icon(string value)
    {
        if (!Icons.Contains(value))
        {
            yield return $"'iconKey' must be one of: {string.Join(", ", Icons)}.";
        }
    }
}
