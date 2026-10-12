// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

// simple implementation for a path in JSON.
// supports object keys, array indices and the '[*]' (or '.*') wildcard matching all array elements.
internal sealed class JsonPath
{
    private readonly Segment[] _segments;

    public JsonPath() => _segments = [];

    public JsonPath(string path) => _segments = Parse(path);

    private JsonPath(Segment[] segments) => _segments = segments;

    public int Length => _segments.Length;

    public JsonPath Append(string key) => new([.. _segments, Segment.Key(key)]);
    public JsonPath Append(int index) => new([.. _segments, Segment.Index(index)]);

    public JsonNode? Navigate(JsonNode? node)
    {
        foreach (Segment segment in _segments)
        {
            if (node is null)
                return null;
            node = segment.Navigate(node);
        }

        return node;
    }

    public bool RemoveAllFrom(JsonNode? root) => _segments.Length > 0 && RemoveAllFrom(root, 0);

    private bool RemoveAllFrom(JsonNode? container, int depth)
    {
        while (true)
        {
            if (container is null) return false;

            Segment segment = _segments[depth];
            bool isLast = depth == _segments.Length - 1;

            if (segment.IsWildcard)
            {
                if (container is not JsonArray array) return false;

                if (isLast)
                {
                    bool any = array.Count > 0;
                    array.Clear();
                    return any;
                }

                bool removed = false;
                foreach (JsonNode? element in array)
                    removed |= RemoveAllFrom(element, depth + 1);

                return removed;
            }

            if (isLast) return RemoveChild(container, segment);
            container = segment.Navigate(container);
            depth += 1;
        }
    }

    private static bool RemoveChild(JsonNode container, Segment segment)
    {
        switch (container)
        {
            case JsonObject obj when segment.IsKey:
                return obj.Remove(segment.KeyValue);
            case JsonArray arr when segment.IsIndex && (uint)segment.IndexValue < (uint)arr.Count:
                arr.RemoveAt(segment.IndexValue);
                return true;
            default:
                return false;
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        foreach (Segment segment in _segments)
        {
            if (segment.IsWildcard)
                sb.Append("[*]");
            else if (segment.IsIndex)
                sb.Append('[').Append(segment.IndexValue).Append(']');
            else
                sb.Append(sb.Length > 0 ? "." : "").Append(segment.KeyValue);
        }

        return sb.ToString();
    }

    private static Segment[] Parse(string path)
    {
        List<Segment> segments = [];
        ReadOnlySpan<char> span = path;

        for (int i = 0; i < span.Length;)
        {
            switch (span[i])
            {
                case '.':
                    i++;
                    break;
                case '[':
                    int close = span[i..].IndexOf(']');
                    if (close < 0)
                        throw new FormatException($"Unterminated '[' in JSON path '{path}'.");
                    ReadOnlySpan<char> inner = span.Slice(i + 1, close - 1).Trim();
                    segments.Add(inner is "*" ? Segment.Wildcard : Segment.Index(int.Parse(inner)));
                    i += close + 1;
                    break;
                default:
                    int end = i;
                    while (end < span.Length && span[end] is not ('.' or '['))
                        end++;
                    ReadOnlySpan<char> key = span[i..end].Trim();
                    if (!key.IsEmpty)
                        segments.Add(key is "*" ? Segment.Wildcard : Segment.Key(key.ToString()));
                    i = end;
                    break;
            }
        }

        return [.. segments];
    }

    /// <summary> A single path segment: an object key, an array index, or the '*' wildcard </summary>
    private readonly struct Segment(string? key, int index)
    {
        public static Segment Key(string key) => new(key, 0);
        public static Segment Index(int index) => new(null, index);
        public static readonly Segment Wildcard = new(null, -1);

        public bool IsKey => key is not null;
        public bool IsIndex => key is null && IndexValue >= 0;
        public bool IsWildcard => key is null && IndexValue < 0;

        public string KeyValue => key!;
        public int IndexValue => index;

        public JsonNode? Navigate(JsonNode node) => (key, index, node) switch
        {
            (not null, _, JsonObject obj) => obj[key],
            (not null, _, JsonArray) => throw new JsonException($"Object expected, got array at '{key}'."),
            (null, >= 0, JsonArray array) => array[index],
            (null, >= 0, JsonObject) => throw new JsonException($"Array expected, got object at '{index}'."),
            (null, < 0, JsonArray) => throw new JsonException("Wildcard can't be used for navigation."),
            _ => throw new JsonException("Invalid path segment."),
        };
    }
}
