// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Declares other argument names accepted for a tool parameter, such as <c>hash</c> for <c>txHash</c>.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class McpParameterAliasAttribute(params string[] aliases) : Attribute
{
    /// <summary>Gets the accepted alternative names.</summary>
    public IReadOnlyList<string> Aliases { get; } = aliases;
}

/// <summary>
/// Normalizes the JSON arguments of a tool call to the parameter types of its method before the SDK binds them, and turns
/// arguments that still do not fit into an <c>invalid_input</c> result naming the parameter.
/// </summary>
/// <remarks>
/// <para>
/// LLM clients often send a number where a string is declared (<c>"block": 19553778</c>), a single value where an array is
/// declared, an array or object serialized into a string, or an explicit <c>null</c> for an omitted parameter. The SDK would
/// fail such calls with a generic message and log an error with a stack trace; instead they are coerced when the intent is
/// unambiguous:
/// </para>
/// <list type="bullet">
/// <item>JSON numbers and booleans become strings for <c>string</c> parameters (and inside <c>string[]</c> ones).</item>
/// <item>A single value is wrapped into an array for array parameters; a string holding a JSON array is parsed.</item>
/// <item>A string holding a JSON object or array is parsed for <see cref="JsonElement"/> parameters.</item>
/// <item><c>"true"</c>/<c>"false"</c> strings become booleans.</item>
/// <item>An explicit <c>null</c> for an optional parameter is treated as omitted.</item>
/// <item>Names declared with <see cref="McpParameterAliasAttribute"/> are accepted for their parameter.</item>
/// </list>
/// </remarks>
internal static class McpArgumentBinder
{
    /// <summary>Returns the binding metadata of the tool parameters of <paramref name="method"/>.</summary>
    public static Parameter[] Describe(MethodInfo method)
    {
        List<Parameter> parameters = [];
        foreach (ParameterInfo info in method.GetParameters())
        {
            if (info.ParameterType == typeof(CancellationToken) || info.Name is null)
            {
                continue;
            }

            Type type = Nullable.GetUnderlyingType(info.ParameterType) ?? info.ParameterType;
            ParameterKind kind = type == typeof(string) ? ParameterKind.String
                : type == typeof(string[]) ? ParameterKind.StringArray
                : type == typeof(JsonElement[]) ? ParameterKind.JsonArray
                : type == typeof(JsonElement) ? ParameterKind.Json
                : type == typeof(double[]) ? ParameterKind.NumberArray
                : type == typeof(bool) ? ParameterKind.Boolean
                : type == typeof(int) || type == typeof(long) ? ParameterKind.Integer
                : type == typeof(double) ? ParameterKind.Number
                : ParameterKind.Other;
            IReadOnlyList<string> aliases = info.GetCustomAttribute<McpParameterAliasAttribute>()?.Aliases ?? [];
            parameters.Add(new Parameter(info.Name, info.ParameterType, kind, info.HasDefaultValue, aliases));
        }

        return [.. parameters];
    }

    /// <summary>Normalizes <paramref name="arguments"/> for <paramref name="parameters"/>.</summary>
    /// <returns>The arguments to bind, or <see langword="null"/> with a client-safe <paramref name="error"/> when one does not fit.</returns>
    public static Dictionary<string, JsonElement>? Normalize(IReadOnlyList<Parameter> parameters, IDictionary<string, JsonElement>? arguments, out string? error)
    {
        Dictionary<string, JsonElement> result = arguments is null ? new(StringComparer.Ordinal) : new(arguments, StringComparer.Ordinal);
        foreach (Parameter parameter in parameters)
        {
            if (!result.TryGetValue(parameter.Name, out JsonElement value))
            {
                foreach (string alias in parameter.Aliases)
                {
                    if (result.Remove(alias, out value))
                    {
                        result[parameter.Name] = value;
                        break;
                    }
                }
            }

            if (!result.TryGetValue(parameter.Name, out value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (parameter.Optional)
                {
                    result.Remove(parameter.Name);
                    continue;
                }

                error = $"'{parameter.Name}' is required: pass {Expected(parameter.Kind)}.";
                return null;
            }

            JsonElement coerced = Coerce(parameter.Kind, value);
            if (!Fits(parameter.Type, coerced))
            {
                error = $"'{parameter.Name}' must be {Expected(parameter.Kind)}; got {Describe(value.ValueKind)}.";
                return null;
            }

            result[parameter.Name] = coerced;
        }

        error = null;
        return result;
    }

    private static JsonElement Coerce(ParameterKind kind, JsonElement value) => kind switch
    {
        ParameterKind.String => value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? AsString(value) : value,
        ParameterKind.StringArray => CoerceStringArray(value),
        ParameterKind.JsonArray or ParameterKind.NumberArray => value.ValueKind switch
        {
            JsonValueKind.Array => value,
            JsonValueKind.String when TryParseJson(value.GetString(), out JsonElement parsed) => parsed.ValueKind == JsonValueKind.Array ? parsed : Wrap(parsed),
            _ => Wrap(value)
        },
        ParameterKind.Json => value.ValueKind == JsonValueKind.String && TryParseJson(value.GetString(), out JsonElement json) ? json : value,
        ParameterKind.Boolean when value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool flag) =>
            flag ? JsonSerializer.SerializeToElement(true) : JsonSerializer.SerializeToElement(false),
        _ => value
    };

    private static JsonElement CoerceStringArray(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                List<JsonElement> items = new(value.GetArrayLength());
                foreach (JsonElement item in value.EnumerateArray())
                {
                    items.Add(item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? AsString(item) : item);
                }

                return JsonSerializer.SerializeToElement(items);
            case JsonValueKind.String when TryParseJson(value.GetString(), out JsonElement parsed) && parsed.ValueKind == JsonValueKind.Array:
                return CoerceStringArray(parsed);
            case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return CoerceStringArray(Wrap(value));
            default:
                return value;
        }
    }

    private static JsonElement AsString(JsonElement value) => JsonSerializer.SerializeToElement(value.GetRawText());

    private static JsonElement Wrap(JsonElement value) => JsonSerializer.SerializeToElement(new[] { value });

    // Only text that looks like a JSON array or object is parsed, so plain strings (addresses, hashes) are never reinterpreted.
    private static bool TryParseJson(string? text, out JsonElement parsed)
    {
        parsed = default;
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || !((trimmed[0] == '[' && trimmed[^1] == ']') || (trimmed[0] == '{' && trimmed[^1] == '}')))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text!);
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Fits(Type type, JsonElement value)
    {
        try
        {
            JsonSerializer.Deserialize(value, type, McpJsonUtilities.DefaultOptions);
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string Expected(ParameterKind kind) => kind switch
    {
        ParameterKind.String => "a string",
        ParameterKind.StringArray => "an array of strings (or a single string)",
        ParameterKind.JsonArray => "a JSON array",
        ParameterKind.Json => "a JSON object",
        ParameterKind.NumberArray => "an array of numbers",
        ParameterKind.Boolean => "true or false",
        ParameterKind.Integer => "an integer",
        ParameterKind.Number => "a number",
        _ => "a value matching the input schema"
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null"
    };

    /// <summary>A bindable tool parameter.</summary>
    internal sealed record Parameter(string Name, Type Type, ParameterKind Kind, bool Optional, IReadOnlyList<string> Aliases);

    /// <summary>How a parameter's JSON argument is normalized.</summary>
    internal enum ParameterKind
    {
        String,
        StringArray,
        JsonArray,
        Json,
        NumberArray,
        Boolean,
        Integer,
        Number,
        Other
    }
}

/// <summary>
/// Wraps a tool so its arguments go through <see cref="McpArgumentBinder"/> first, and optionally advertises a declared output schema.
/// </summary>
/// <remarks>
/// <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions?)"/> overwrites
/// <see cref="McpServerToolCreateOptions.UseStructuredContent"/> with the value on <see cref="McpServerToolAttribute"/>
/// (false by default) and then drops <see cref="McpServerToolCreateOptions.OutputSchema"/>. Setting the schema on the
/// inner descriptor instead would make the SDK serialize every returned <see cref="CallToolResult"/> into a structured
/// response it then discards, so the schema lives on a separate descriptor copy and invocation passes straight through.
/// </remarks>
internal sealed class McpBoundTool : DelegatingMcpServerTool
{
    private readonly McpServerTool _inner;
    private readonly McpArgumentBinder.Parameter[] _parameters;

    public McpBoundTool(McpServerTool inner, MethodInfo method, JsonElement? outputSchema) : base(inner)
    {
        _inner = inner;
        _parameters = McpArgumentBinder.Describe(method);
        ProtocolTool = outputSchema is not { } schema
            ? inner.ProtocolTool
            : new Tool
            {
                Name = inner.ProtocolTool.Name,
                Title = inner.ProtocolTool.Title,
                Description = inner.ProtocolTool.Description,
                InputSchema = inner.ProtocolTool.InputSchema,
                OutputSchema = schema,
                Annotations = inner.ProtocolTool.Annotations,
                Icons = inner.ProtocolTool.Icons,
                Meta = inner.ProtocolTool.Meta
            };
    }

    /// <inheritdoc/>
    public override Tool ProtocolTool { get; }

    /// <inheritdoc/>
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        if (request.Params is { } parameters)
        {
            Dictionary<string, JsonElement>? arguments = McpArgumentBinder.Normalize(_parameters, parameters.Arguments, out string? error);
            if (arguments is null)
            {
                return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput, error!);
            }

            parameters.Arguments = arguments;
        }

        try
        {
            return await _inner.InvokeAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // Normalization accepts what binds; this is a last line so a client mistake never surfaces as a server error.
            return McpToolExecutor.Error(McpToolErrorCodes.InvalidInput,
                $"The arguments of '{ProtocolTool.Name}' could not be bound; check the parameter names and types against the tool's input schema.");
        }
    }
}
