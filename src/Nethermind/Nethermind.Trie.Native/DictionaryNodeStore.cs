// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>An in-memory <see cref="INativeNodeStore"/> that reads from <paramref name="reads"/> and writes to <paramref name="writes"/>.</summary>
/// <remarks>Pass the same dictionary for both to get a regular store. Nodes are keyed by path alone.</remarks>
public sealed class DictionaryNodeStore(IReadOnlyDictionary<TreePath, byte[]> reads, ConcurrentDictionary<TreePath, byte[]> writes) : INativeNodeStore
{
    public int Fetch(in TreePath path, in ValueHash256 hash, Span<byte> buffer)
    {
        if (!reads.TryGetValue(path, out byte[]? node)) return 0;
        if (node.Length <= buffer.Length) node.CopyTo(buffer);
        return node.Length;
    }

    public void Write(in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp) => writes[path] = rlp.ToArray();
}
