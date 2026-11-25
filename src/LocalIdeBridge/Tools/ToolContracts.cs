using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalIdeBridge.Tools;

public sealed record ToolParameterSpec(
    string Name,
    string Type,
    string Description,
    bool Required = false,
    object? Default = null,
    string[]? EnumValues = null,
    JsonObject? ItemsSchema = null);

public sealed record ToolDefinition(
    string Name,
    string Category,
    string Description,
    bool IsReadOnly,
    bool IsIdempotent,
    string SideEffects,
    IReadOnlyList<ToolParameterSpec> Parameters,
    Func<JsonObject, ToolExecutionContext, CancellationToken, Task<ToolResult>> Handler)
{
    public JsonObject BuildJsonSchema()
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var p in Parameters)
        {
            var prop = new JsonObject
            {
                ["type"] = p.Type,
                ["description"] = p.Description
            };
            if (p.EnumValues is { Length: > 0 })
            {
                var arr = new JsonArray();
                foreach (var v in p.EnumValues) arr.Add(v);
                prop["enum"] = arr;
            }
            if (p.Default is not null)
            {
                prop["default"] = JsonSerializer.SerializeToNode(p.Default);
            }
            if (p.ItemsSchema is not null)
            {
                prop["items"] = p.ItemsSchema.DeepClone();
            }
            props[p.Name] = prop;
            if (p.Required)
                required.Add(p.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }
}

public sealed record ToolExecutionContext(
    string SessionId,
    string? JobId = null,
    Action<string>? ReportProgress = null,
    bool SkipWriteLock = false,
    string? WorkspaceRoot = null,
    Action<string>? WorkspaceSelection = null);

public sealed record ToolResult(
    bool Success,
    string Summary,
    object? Data = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ExecutionId = null)
{
    public static ToolResult Ok(string summary, object? data = null, string? execId = null)
        => new(true, summary, data, null, null, execId);

    public static ToolResult Fail(string errorCode, string message, object? data = null, string? execId = null)
        => new(false, message, data, errorCode, message, execId);
}

public static class ArgReader
{
    public static string RequireString(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            throw new ArgumentException($"Missing required parameter '{name}'.");
        var s = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Parameter '{name}' must not be empty.");
        return s;
    }

    public static string? OptionalString(this JsonObject args, string name, string? defaultValue = null)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            return defaultValue;
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
    }

    public static string RequireRawString(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            throw new ArgumentException($"Missing required parameter '{name}'.");
        if (node.GetValueKind() != JsonValueKind.String)
            throw new ArgumentException($"Parameter '{name}' must be a string.");
        return node.GetValue<string>();
    }

    public static int OptionalInt(this JsonObject args, string name, int defaultValue)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            return defaultValue;
        if (node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var v))
            return v;
        if (node.GetValueKind() == JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var parsed))
            return parsed;
        throw new ArgumentException($"Parameter '{name}' must be an integer.");
    }

    public static long OptionalLong(this JsonObject args, string name, long defaultValue)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            return defaultValue;
        if (node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<long>(out var v))
            return v;
        if (node.GetValueKind() == JsonValueKind.String && long.TryParse(node.GetValue<string>(), out var parsed))
            return parsed;
        throw new ArgumentException($"Parameter '{name}' must be an integer.");
    }

    public static bool OptionalBool(this JsonObject args, string name, bool defaultValue)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            return defaultValue;
        if (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return node.GetValue<bool>();
        if (node.GetValueKind() == JsonValueKind.String && bool.TryParse(node.GetValue<string>(), out var parsed))
            return parsed;
        throw new ArgumentException($"Parameter '{name}' must be a boolean.");
    }

    public static IReadOnlyList<string> OptionalStringList(this JsonObject args, string name)
    {
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
            return [];
        if (node is JsonArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is not null)
                    list.Add(item.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : item.ToString());
            }
            return list;
        }
        if (node.GetValueKind() == JsonValueKind.String)
            return [node.GetValue<string>()];
        throw new ArgumentException($"Parameter '{name}' must be an array of strings.");
    }
}
