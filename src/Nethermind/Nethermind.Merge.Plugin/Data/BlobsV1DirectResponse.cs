// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Wraps an <see cref="ArrayPoolList{T}"/> of <see cref="BlobAndProofV1"/> and writes JSON
/// directly into a <see cref="PipeWriter"/>, bypassing <see cref="System.Text.Json.Utf8JsonWriter"/>
/// to avoid extra buffer copies for large blob payloads.
/// </summary>
public sealed class BlobsV1DirectResponse : IStreamableResult, IEnumerable<BlobAndProofV1?>, IDisposable
{
    private readonly ArrayPoolList<BlobAndProofV1?> _items;

    public BlobsV1DirectResponse(ArrayPoolList<BlobAndProofV1?> items)
    {
        _items = items;
    }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        int count = _items.Count;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) writer.Write(","u8);

            BlobAndProofV1? item = _items[i];
            if (item is null)
            {
                writer.Write("null"u8);
            }
            else
            {
                writer.Write("{\"blob\":\"0x"u8);
                HexWriter.WriteHexChunked(writer, item.Blob);
                writer.Write("\",\"proof\":\"0x"u8);
                HexWriter.WriteHexSmall(writer, item.Proof);
                writer.Write("\"}"u8);
            }

            // Flush after each entry for backpressure
            FlushResult flushResult = await writer.FlushAsync(cancellationToken);
            if (flushResult.IsCompleted || flushResult.IsCanceled)
                return;
        }

        writer.Write("]"u8);
    }

    public IEnumerator<BlobAndProofV1?> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _items.Dispose();
}
