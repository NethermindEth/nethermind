// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor.Dynamic;

internal class DynamicJson<TContext>
{
    private class JsonCompileException(JsonPath path, Exception exception) :
        Exception($"Error at path '{path}': {exception.Message}", exception);

    private readonly JsonNode _template;
    private readonly List<(JsonPath Path, Lambda Expression)> _expressions = [];

    public DynamicJson(JsonNode template)
    {
        _template = template.DeepClone();
        Scan(template, new JsonPath(), DynamicBinder<TContext>.CreateInterpreter());
    }

    public JsonNode Compile(TContext context)
    {
        object[] args = DynamicBinder<TContext>.GetArgs(context);
        JsonNode result = _template.DeepClone();

        foreach ((JsonPath path, Lambda expr) in _expressions)
        {
            JsonNode node = JsonSerializer.SerializeToNode(expr.Invoke(args))!;
            if (path.Length == 0)
                return node;

            result.ReplaceAt(path, node);
        }

        return result;
    }

    private void Scan(JsonNode? node, JsonPath path, Interpreter interpreter)
    {
        try
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((string key, JsonNode? value) in obj)
                        Scan(value, path.Append(key), interpreter);

                    break;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                        Scan(arr[i], path.Append(i), interpreter);

                    break;
                case JsonValue val when val.TryGetValue(out string? str) && str.StartsWith("{{") && str.EndsWith("}}"):
                    _expressions.Add((path, interpreter.Parse(str[2..^2], DynamicBinder<TContext>.Parameters)));
                    break;
            }
        }
        catch (Exception exception) when (exception is not JsonCompileException)
        {
            throw new JsonCompileException(path, exception);
        }
    }
}
