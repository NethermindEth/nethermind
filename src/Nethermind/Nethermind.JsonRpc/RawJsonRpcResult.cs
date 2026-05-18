// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

/// <summary>
/// Wraps an already serialized UTF-8 JSON value for direct JSON-RPC response writing.
/// </summary>
/// <remarks>
/// The supplied memory must contain one complete, valid JSON value and remain stable until
/// the response has been written.
/// </remarks>
[JsonConverter(typeof(RawJsonRpcResultConverter))]
public sealed class RawJsonRpcResult(ReadOnlyMemory<byte> json) : IStreamableResult
{
    public ReadOnlyMemory<byte> Json { get; } = json;

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> json = Json.Span;
        Span<byte> buffer = writer.GetSpan(json.Length);
        json.CopyTo(buffer);
        writer.Advance(json.Length);
        return ValueTask.CompletedTask;
    }
}

public sealed class RawJsonRpcResultConverter : JsonConverter<RawJsonRpcResult>
{
    public override RawJsonRpcResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, RawJsonRpcResult? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(value.Json.Span, skipInputValidation: true);
    }
}
