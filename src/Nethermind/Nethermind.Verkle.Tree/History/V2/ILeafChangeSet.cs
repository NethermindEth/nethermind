// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Verkle.Tree.History.V2;

public interface ILeafChangeSet
{
    public void InsertDiff(long blockNumber, LeafStoreInterface leafTable);
    public byte[]? GetLeaf(long blockNumber, ReadOnlySpan<byte> key);
}
