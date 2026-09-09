// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Holds the per-receipt RLP buffers of a <c>debug_getRawReceipts</c> response, each rented from the
/// pool, that must stay alive until the JSON-RPC response has been serialized. Serializes as a JSON
/// array of hex strings.
/// </summary>
[JsonConverter(typeof(RawReceiptsResultConverter))]
public sealed class RawReceiptsResult : IDisposable
{
    private readonly ArrayPoolList<ArrayPoolList<byte>> _receipts;

    public RawReceiptsResult(ArrayPoolList<ArrayPoolList<byte>> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        _receipts = receipts;
    }

    internal ArrayPoolList<ArrayPoolList<byte>> Receipts => _receipts;

    public void Dispose()
    {
        foreach (ArrayPoolList<byte> receipt in _receipts)
        {
            receipt.Dispose();
        }
        _receipts.Dispose();
    }
}

internal sealed class RawReceiptsResultConverter : JsonConverter<RawReceiptsResult>
{
    public override RawReceiptsResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(RawReceiptsResultConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, RawReceiptsResult value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (ArrayPoolList<byte> receipt in value.Receipts)
        {
            ByteArrayConverter.Convert(writer, receipt.AsSpan(), skipLeadingZeros: false);
        }
        writer.WriteEndArray();
    }
}
