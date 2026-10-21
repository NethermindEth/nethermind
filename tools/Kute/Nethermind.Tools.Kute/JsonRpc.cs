// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.Tools.Kute;

public abstract class JsonRpc
{
    private readonly JsonNode _node;

    private JsonRpc(JsonNode node) => _node = node;

    public string ToJsonString() => _node.ToJsonString();
    public JsonNode Json => _node;

    public abstract class Request : JsonRpc
    {

        private Request(JsonNode node) : base(node) { }

        public class Single : Request
        {
            private readonly Lazy<string?> _id;
            private readonly Lazy<string?> _methodName;

            public string? Id { get => _id.Value; }
            public string? MethodName { get => _methodName.Value; }

            public Single(JsonNode node) : base(node)
            {
                _id = new(() =>
                {
                    if (_node["id"] is { } id)
                    {
                        return ((Int64)id).ToString();
                    }

                    return null;
                });
                _methodName = new(() =>
                {
                    if (_node["method"] is { } method)
                    {
                        return (string?)method;
                    }

                    return null;
                });
            }

            public override string ToString() => $"{nameof(Single)} {ToJsonString()}";
        }

        public class Batch : Request
        {
            private readonly Lazy<string?> _id;

            public string? Id { get => _id.Value; }

            public Batch(JsonNode node) : base(node) => _id = new(() =>
                _node.AsArray() is { Count: > 0 } arr
                    && arr[0]?["id"] is { } firstId
                    && arr[^1]?["id"] is { } lastId
                        ? $"{(Int64)firstId}:{(Int64)lastId}"
                        : null);

            public IEnumerable<Single?> Items()
            {
                foreach (JsonNode? node in _node.AsArray())
                {
                    yield return node is null ? null : new Single(node);
                }
            }

            public override string ToString() => $"{nameof(Batch)} {ToJsonString()}";
        }
    }

    public class Response : JsonRpc
    {
        private readonly Lazy<string?> _id;

        public string? Id { get => _id.Value; }

        public Response(JsonNode node) : base(node) => _id = new(() =>
            _node["id"] is { } id ? ((Int64)id).ToString() : null);

        public static async Task<Response> FromHttpResponseAsync(HttpResponseMessage response, CancellationToken token = default)
        {
            Stream content = await response.Content.ReadAsStreamAsync(token);
            JsonNode? node = await JsonNode.ParseAsync(content, cancellationToken: token);

            return new Response(node!);
        }

        public override string ToString() => $"{nameof(Response)} {ToJsonString()}";
    }
}
