// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie;

public static partial class HexPrefix
{
    private static readonly byte[][] TripleNibblePaths = CreateTripleNibblePaths();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] TripleNibblePath(int index) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), index);

    private static byte[][] CreateTripleNibblePaths()
    {
        byte[][] paths = new byte[TripleNibblePathCount][];
        for (int i = 0; i < TripleNibblePathCount; i++)
        {
            paths[i] = CreateTripleNibblePath(i);
        }
        return paths;
    }
}
