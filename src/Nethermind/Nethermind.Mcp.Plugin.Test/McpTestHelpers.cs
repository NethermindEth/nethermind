// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Invokes MCP tools through the SDK client.</summary>
internal static class McpToolCalls
{
    public static async Task<CallToolResult> Call(McpClient client, string toolName, (string Name, object? Value)[] args, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> arguments = new(args.Length);
        foreach ((string name, object? value) in args) arguments[name] = value;
        return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
    }

    /// <summary>Asserts that a successful <paramref name="result"/> of <paramref name="toolName"/> conforms to the output schema the server advertises for it.</summary>
    public static async Task AssertConformsToOutputSchema(McpClient client, string toolName, CallToolResult result)
    {
        McpClientTool tool = (await client.ListToolsAsync()).Single(t => t.Name == toolName);
        Assert.That(tool.ProtocolTool.OutputSchema, Is.Not.Null, $"{toolName} must declare an output schema");
        Assert.That(result.StructuredContent, Is.Not.Null, $"{toolName} must return structured content");
        McpSchemaValidator.AssertConforms(tool.ProtocolTool.OutputSchema!.Value, result.StructuredContent!.Value);
    }
}

/// <summary>
/// A minimal JSON Schema checker for tool output schemas: <c>type</c> (single or list), <c>enum</c>, <c>required</c>,
/// <c>properties</c>, <c>items</c> and local <c>$ref</c>s to <c>#/$defs/...</c>.
/// </summary>
internal static class McpSchemaValidator
{
    public static void AssertConforms(JsonElement schema, JsonElement value)
    {
        List<string> errors = [];
        Validate(schema, schema, value, "$", errors);
        Assert.That(errors, Is.Empty, () => $"output does not match its schema:\n{string.Join("\n", errors)}\nvalue: {value}");
    }

    private static void Validate(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string target = reference.GetString()!;
            Assert.That(target, Does.StartWith("#/$defs/"), "only local $defs references are supported");
            schema = root.GetProperty("$defs").GetProperty(target["#/$defs/".Length..]);
        }

        if (schema.TryGetProperty("type", out JsonElement type))
        {
            IEnumerable<string> allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(static t => t.GetString()!)
                : [type.GetString()!];
            if (!allowed.Any(t => Matches(t, value)))
            {
                errors.Add($"{path}: expected {type}, got {value.ValueKind} ({Truncate(value)})");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            errors.Add($"{path}: {value} is not one of {options}");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement name in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(name.GetString()!, out _)) errors.Add($"{path}: missing required property '{name.GetString()}'");
                }
            }

            if (schema.TryGetProperty("properties", out JsonElement properties))
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    {
                        Validate(root, propertySchema, property.Value, $"{path}.{property.Name}", errors);
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int i = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                Validate(root, items, item, $"{path}[{i++}]", errors);
            }
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new NotSupportedException($"schema type '{type}'")
    };

    private static string Truncate(JsonElement value)
    {
        string text = value.ToString();
        return text.Length > 80 ? text[..80] + "..." : text;
    }
}
