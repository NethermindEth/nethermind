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

/// <summary>Writes blob/proof V1 results directly into a <see cref="PipeWriter"/>.</summary>
public sealed class BlobsV1DirectResponse(ArrayPoolList<BlobAndProofV1?> items) : IStreamableResult, IReadOnlyList<BlobAndProofV1?>, IDisposable
{
    private readonly ArrayPoolList<BlobAndProofV1?> _items = items;

    public int Count => _items.Count;

    public BlobAndProofV1? this[int index] => _items[index];

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _items.Count, new ItemWriter(_items), cancellationToken);

    public IEnumerator<BlobAndProofV1?> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _items.Dispose();

    private readonly struct ItemWriter(ArrayPoolList<BlobAndProofV1?> items) : IJsonArrayItemWriter
    {
        public void WriteItem(PipeWriter writer, int index)
        {
            BlobAndProofV1? item = items[index];
            if (item is null)
            {
                writer.Write("null"u8);
                return;
            }

            writer.Write("{\"blob\":"u8);
            HexWriter.WriteHexString(writer, item.Blob, chunked: true);
            writer.Write(",\"proof\":"u8);
            HexWriter.WriteHexString(writer, item.Proof, chunked: false);
            writer.Write("}"u8);
        }
    }
}
