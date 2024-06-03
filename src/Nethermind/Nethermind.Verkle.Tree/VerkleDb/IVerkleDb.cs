// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public interface IVerkleDbWithBatching : IVerkleDb
{
    public VerkleKeyValueBatch StartWriteBatch();
}

public interface IVerkleDb : IVerkleWriteOnlyDb, IVerkleReadOnlyDb
{
}

public interface IVerkleWriteOnlyDb
{
    void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue);
    void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue);

    void RemoveLeaf(ReadOnlySpan<byte> leafKey);
    void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey);
}

public interface IVerkleReadOnlyDb
{
    bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value);
    bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode? value);
}
