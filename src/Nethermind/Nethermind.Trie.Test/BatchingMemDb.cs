// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;

namespace Nethermind.Trie.Test;

/// <summary>A memory DB that reads in batches and counts them.</summary>
internal sealed class BatchingMemDb : TestMemDb, IBatchReadOnlyDb
{
    private int _batchReads;

    public int BatchReads => _batchReads;

    void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder, ReadFlags flags)
    {
        Interlocked.Increment(ref _batchReads);
        for (int i = 0; i < values.Length; i++)
        {
            byte[]? value = Get(keys.Slice(i * keyLength, keyLength), flags);
            values[i] = value is null ? default! : decoder.Decode(value);
        }
    }
}
