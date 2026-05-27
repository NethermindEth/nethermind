using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor;

internal class DynamicJson
{
    [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "Used dynamically at runtime")]
    private static class ToConverter
    {
        public static string Hex(int n) => $"0x{n:x}";
        public static string Hex(long n) => $"0x{n:x}";
    }

    private readonly JsonNode _template;
    private readonly List<(object[] Path, Lambda Expression)> _expressions = [];

    public DynamicJson(JsonNode template, params Parameter[] parameters)
    {
        _template = template.DeepClone();

        Interpreter interpreter = new();
        interpreter.Reference(typeof(ToConverter), "To");
        Scan(template, [], interpreter, parameters);
    }

    public JsonNode? Compile(params object[] args)
    {
        JsonNode result = _template.DeepClone();

        foreach ((object[] path, Lambda expr) in _expressions)
        {
            JsonNode? value = JsonSerializer.SerializeToNode(expr.Invoke(args));
            if (path.Length == 0)
                return value;

            JsonNode? parent = Navigate(result, path);
            if (path[^1] is string lastKey)
                ((JsonObject)parent!)[lastKey] = value;
            else
                ((JsonArray)parent!)[(int)path[^1]] = value;
        }

        return result;
    }

    private static JsonNode? Navigate(JsonNode? node, object[] path)
    {
        for (int i = 0; i < path.Length - 1; i++)
            node = path[i] is string key ? node?[key] : node?[(int)path[i]];
        return node;
    }

    private void Scan(JsonNode? node, List<object> path, Interpreter interpreter, Parameter[] parameters)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    path.Add(key);
                    Scan(value, path, interpreter, parameters);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    path.Add(i);
                    Scan(arr[i], path, interpreter, parameters);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case JsonValue val when val.TryGetValue(out string? str) && str.StartsWith("{{") && str.EndsWith("}}"):
                _expressions.Add((path.ToArray(), interpreter.Parse(str[2..^2], parameters)));
                break;
        }
    }
}
