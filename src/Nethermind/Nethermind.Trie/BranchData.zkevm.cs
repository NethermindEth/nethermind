// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Trie;

public sealed partial class BranchData
{
    private ushort _taken;

    /// <summary>A bit for each slot whose child may differ from the one the branch's RLP encodes.</summary>
    /// <remarks>
    /// Only where <see cref="FirstTouchMemoryFlag"/> is on. <see cref="TrieNode"/> marks a slot each time it takes a writable
    /// reference to it, which every child write does, and when a write below changes the child in place. The hot read path
    /// resolves a child without a mark. A slot without its bit holds null or the child that the RLP encodes, so a re-encode
    /// copies that slot from the RLP. A walk that tests the bits first skips the other slots: SP1 charges for reading each
    /// of them the first time. A bit is never cleared, as a slot with its bit set is only encoded again.
    /// </remarks>
    internal ushort Taken => _taken;

    /// <summary>Records that slot <paramref name="index"/> may now hold a child.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkTaken(int index)
    {
        if (FirstTouchMemoryFlag.IsActive) _taken |= (ushort)(1 << index);
    }

    partial void CopyTakenTo(BranchData clone) => clone._taken = _taken;
}
