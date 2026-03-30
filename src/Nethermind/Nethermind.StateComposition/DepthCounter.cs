// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

public struct DepthCounter
{
    public long Branches;
    public long Extensions;
    public long Leaves;
    public long ByteSize;

    public void AddBranch(int byteSize)
    {
        Branches++;
        ByteSize += byteSize;
    }

    public void AddExtension(int byteSize)
    {
        Extensions++;
        ByteSize += byteSize;
    }

    public void AddLeaf(int byteSize)
    {
        Leaves++;
        ByteSize += byteSize;
    }
}
