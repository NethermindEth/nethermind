// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;

namespace Nethermind.Blockchain.Test.Blocks;

/// <summary>A memory database that serves batch reads and counts them.</summary>
internal sealed class BatchReadMemDb : MemDb, IBatchReadOnlyDb
{
    public int BatchReads { get; private set; }

    void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder)
    {
        BatchReads++;
        for (int i = 0; i < values.Length; i++)
        {
            byte[]? value = Get(keys.Slice(i * keyLength, keyLength));
            values[i] = value is null ? default! : decoder.Decode(value);
        }
    }
}
