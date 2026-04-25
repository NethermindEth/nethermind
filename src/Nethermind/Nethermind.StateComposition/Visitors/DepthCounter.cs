// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Mutable per-depth node counters using Geth's trie node vocabulary
/// (see <see cref="Data.TrieLevelStat"/> for the full mapping).
/// <para>
/// WARNING: This is a mutable struct. MUST be accessed via <c>ref</c> to avoid
/// accidental copies that silently lose mutations. Always use
/// <c>ref DepthCounter dc = ref array[i];</c> when reading from arrays.
/// </para>
/// </summary>
internal struct DepthCounter
{
    public long ShortNodes;
    public long FullNodes;
    public long ValueNodes;
    public long TotalSize;

    public void AddFullNode(int byteSize)
    {
        FullNodes++;
        TotalSize += byteSize;
    }

    public void AddShortNode(int byteSize)
    {
        ShortNodes++;
        TotalSize += byteSize;
    }

    public void AddValueNode(int byteSize)
    {
        ValueNodes++;
        TotalSize += byteSize;
    }
}
