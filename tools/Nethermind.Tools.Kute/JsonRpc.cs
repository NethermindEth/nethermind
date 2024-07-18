// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute;

public abstract record JsonRpc
{
    public JsonDocument Document;

    private JsonRpc(JsonDocument document)
    {
        Document = document;
    }

    public string ToJsonString() => Document.RootElement.ToString();

    public record BatchJsonRpc : JsonRpc
    {
        public BatchJsonRpc(JsonDocument document) : base(document) { }

        public override string ToString() => $"{nameof(BatchJsonRpc)} {ToJsonString()}";
    }

    public record SingleJsonRpc : JsonRpc
    {
        private readonly Lazy<bool> _isResponse;
        private readonly Lazy<string?> _methodName;

        public SingleJsonRpc(JsonDocument document) : base(document)
        {
            _isResponse = new(() =>
                Document.RootElement.TryGetProperty("response", out _)
            );
            _methodName = new(() =>
            {
                if (Document.RootElement.TryGetProperty("method", out var jsonMethodField))
                {
                    return jsonMethodField.GetString();
                }

                return null;
            });
        }

        public bool IsResponse { get => _isResponse.Value; }
        public string? MethodName { get => _methodName.Value; }

        public override string ToString() => $"{nameof(SingleJsonRpc)} {ToJsonString()}";
    }
}
