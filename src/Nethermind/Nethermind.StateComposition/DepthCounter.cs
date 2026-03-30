// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Mutable per-depth node counters.
/// Short=Extension, Full=Branch, Value=Leaf.
/// </summary>
public struct DepthCounter
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
