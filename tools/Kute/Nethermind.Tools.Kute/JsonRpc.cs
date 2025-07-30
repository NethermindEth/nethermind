// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute;

public abstract class JsonRpc
{
    private readonly JsonDocument _document;

    private JsonRpc(JsonDocument document)
    {
        _document = document;
    }

    public JsonElement Json => _document.RootElement;

    public string ToJsonString() => _document.RootElement.ToString();

    public abstract class Request : JsonRpc
    {
        private Request(JsonDocument document) : base(document) { }

        public class Single : Request
        {
            private readonly Lazy<string?> _methodName;

            public string? MethodName { get => _methodName.Value; }

            public Single(JsonDocument document) : base(document)
            {
                _methodName = new(() =>
                {
                    if (_document.RootElement.TryGetProperty("method", out var jsonMethodField))
                    {
                        return jsonMethodField.GetString();
                    }

                    return null;
                });
            }

            public override string ToString() => $"{nameof(Single)} {ToJsonString()}";
        }
        public class Batch(JsonDocument document) : Request(document)
        {
            public IEnumerable<Single?> Items()
            {
                foreach (var element in _document.RootElement.EnumerateArray())
                {
                    var document = JsonSerializer.Deserialize<JsonDocument>(element);
                    yield return document is null ? null : new Single(document);
                }
            }

            public override string ToString() => $"{nameof(Batch)} {ToJsonString()}";
        }
    }

    public class Response(JsonDocument document) : JsonRpc(document)
    {
        public static async Task<Response> FromHttpResponseAsync(HttpResponseMessage response, CancellationToken token = default)
        {
            var content = await response.Content.ReadAsStreamAsync(token);
            var document = await JsonDocument.ParseAsync(content, cancellationToken: token);

            return new Response(document);
        }

        public override string ToString() => $"{nameof(Response)} {ToJsonString()}";
    }
}
