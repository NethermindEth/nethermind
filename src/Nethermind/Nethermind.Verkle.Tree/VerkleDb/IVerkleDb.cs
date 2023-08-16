// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public interface IVerkleDb
{
    bool GetLeaf(ReadOnlySpan<byte> key, out byte[]? value);
    bool GetInternalNode(ReadOnlySpan<byte> key, out InternalNode? value);

    void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue);
    void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue);

    void RemoveLeaf(ReadOnlySpan<byte> leafKey);
    void RemoveInternalNode(ReadOnlySpan<byte> internalNodeKey);

    void BatchLeafInsert(IEnumerable<KeyValuePair<byte[], byte[]?>> keyLeaf);
    void BatchInternalNodeInsert(IEnumerable<KeyValuePair<byte[], InternalNode?>> internalNodeLeaf);
}
