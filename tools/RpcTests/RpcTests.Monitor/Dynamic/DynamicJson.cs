// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor.Dynamic;

internal class DynamicJson<TContext>
{
    private class JsonCompileException(IEnumerable<object> path, Exception exception) :
        Exception($"Error at path '{string.Join('.', path)}': {exception.Message}", exception);

    private readonly JsonNode _template;
    private readonly List<(object[] Path, Lambda Expression)> _expressions = [];

    public DynamicJson(JsonNode template)
    {
        _template = template.DeepClone();
        Scan(template, [], DynamicBinder<TContext>.CreateInterpreter());
    }

    public JsonNode Compile(TContext context)
    {
        object[] args = DynamicBinder<TContext>.GetArgs(context);
        JsonNode result = _template.DeepClone();

        foreach ((object[] path, Lambda expr) in _expressions)
        {
            JsonNode node = JsonSerializer.SerializeToNode(expr.Invoke(args))!;
            if (path.Length == 0)
                return node;

            JsonNode? parent = Navigate(result, path);
            if (path[^1] is string lastKey)
                ((JsonObject)parent!)[lastKey] = node;
            else
                ((JsonArray)parent!)[(int)path[^1]] = node;
        }

        return result;
    }

    private static JsonNode? Navigate(JsonNode? node, object[] path)
    {
        for (int i = 0; i < path.Length - 1; i++)
            node = path[i] is string key ? node?[key] : node?[(int)path[i]];
        return node;
    }

    private void Scan(JsonNode? node, List<object> path, Interpreter interpreter)
    {
        try
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach ((string key, JsonNode? value) in obj)
                    {
                        path.Add(key);
                        Scan(value, path, interpreter);
                        path.RemoveAt(path.Count - 1);
                    }

                    break;
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        path.Add(i);
                        Scan(arr[i], path, interpreter);
                        path.RemoveAt(path.Count - 1);
                    }

                    break;
                case JsonValue val when val.TryGetValue(out string? str) && str.StartsWith("{{") && str.EndsWith("}}"):
                    _expressions.Add((path.ToArray(), interpreter.Parse(str[2..^2], DynamicBinder<TContext>.Parameters)));
                    break;
            }
        }
        catch (Exception exception) when (exception is not JsonCompileException)
        {
            throw new JsonCompileException(path, exception);
        }
    }
}
