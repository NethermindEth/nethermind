// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Consensus.Stateless;

/// <summary>Native trie endpoints with the full witness available to managed consumers on demand.</summary>
internal sealed unsafe class AirbenderWitnessNodes(byte* first, byte* last, int count, Func<byte[][]> readNodes)
    : IOwnedReadOnlyList<byte[]>
{
    private byte[][]? _items;
    private byte[][] Items => _items ??= readNodes();
    public bool TryAttach() => Accelerators.AirbenderMptAttach(first, last, (uint)count);
    public int Count => count;
    public byte[] this[int index] => Items[index];
    public ReadOnlySpan<byte[]> AsSpan() => Items;
    public IEnumerator<byte[]> GetEnumerator() => ((IEnumerable<byte[]>)Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
}
