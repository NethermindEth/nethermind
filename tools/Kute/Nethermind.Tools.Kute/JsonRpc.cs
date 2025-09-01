// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.Tools.Kute;

public abstract class JsonRpc
{
    public abstract string? Id { get; }
    private readonly JsonNode _node;

    private JsonRpc(JsonNode node)
    {
        _node = node;
    }

    public string ToJsonString() => _node.ToJsonString();
    public JsonNode Json => _node;

    public abstract class Request : JsonRpc
    {

        private Request(JsonNode node) : base(node) { }

        public class Single : Request
        {
            private readonly Lazy<string?> _id;
            private readonly Lazy<string?> _methodName;

            public override string? Id { get => _id.Value; }
            public string? MethodName { get => _methodName.Value; }

            public Single(JsonNode node) : base(node)
            {
                _id = new(() =>
                {
                    if (_node["id"] is JsonNode id)
                    {
                        return ((Int64)id).ToString();
                    }

                    return null;
                });
                _methodName = new(() =>
                {
                    if (_node["method"] is JsonNode method)
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

            public override string? Id { get => _id.Value; }

            public Batch(JsonNode node) : base(node)
            {
                _id = new(() =>
                {
                    if (Items().Any())
                    {
                        var first = Items().First()?.Id?.ToString();
                        var last = Items().Last()?.Id?.ToString();

                        if (first is not null && last is not null)
                        {
                            return $"{first}:{last}";
                        }
                    }

                    return null;
                });
            }

            public IEnumerable<Single?> Items()
            {
                foreach (var node in _node.AsArray())
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

        public override string? Id { get => _id.Value; }

        public Response(JsonNode node) : base(node)
        {
            _id = new(() =>
            {
                if (_node["id"] is JsonNode id)
                {
                    return ((Int64)id).ToString();
                }

                return null;
            });
        }

        public static async Task<Response> FromHttpResponseAsync(HttpResponseMessage response, CancellationToken token = default)
        {
            var content = await response.Content.ReadAsStreamAsync(token);
            var node = await JsonNode.ParseAsync(content, cancellationToken: token);

            return new Response(node!);
        }

        public override string ToString() => $"{nameof(Response)} {ToJsonString()}";
    }
}
