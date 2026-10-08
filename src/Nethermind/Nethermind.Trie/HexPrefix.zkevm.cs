// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie;

public static partial class HexPrefix
{
    // Filled on first use: a block touches a small fraction of the 4096 paths, and building all of them
    // up front is thousands of allocations in the type initializer. Unsynchronized: a racing fill only hands
    // out an equal-content duplicate, costing reference identity, which callers do not rely on.
    private static readonly byte[]?[] TripleNibblePaths = new byte[TripleNibblePathCount][];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] TripleNibblePath(int index) =>
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), index) ??= CreateTripleNibblePath(index);
}
