// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

// very limited and unoptimized abstraction for path in JSON
internal sealed class JsonPath
{
    private readonly object[] _segments;

    public JsonPath() => _segments = [];

    public JsonPath(string path) =>
        _segments = [.. path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private JsonPath(object[] segments) => _segments = segments;

    public int Length => _segments.Length;

    public JsonPath Append(string key) => new([.. _segments, key]);
    public JsonPath Append(int index) => new([.. _segments, index]);

    public JsonNode? Navigate(JsonNode? node)
    {
        foreach (object segment in _segments)
        {
            if (node is null)
                return null;
            node = segment is string key ? node[key] : node[(int)segment];
        }

        return node;
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        foreach (object segment in _segments)
        {
            if (segment is int index)
                sb.Append('[').Append(index).Append(']');
            else
                sb.Append(sb.Length > 0 ? "." : "").Append(segment);
        }

        return sb.ToString();
    }
}
