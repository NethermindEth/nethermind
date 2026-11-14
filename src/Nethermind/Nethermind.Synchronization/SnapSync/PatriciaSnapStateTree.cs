// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree, SnapUpperBoundAdapter adapter, INodeStorage nodeStorage) : ISnapTree<PathWithAccount>
{
    private List<(TrieNode, TreePath)>? _proofNodes;

    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(IByteArrayList proofs, IReadOnlyList<SnapProofNode> nodes) =>
        _proofNodes = PatriciaSnapProof.SetRoot(tree, proofs, nodes);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        nodeStorage.KeyExists(null, path, keccak);

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithAccount> entries) =>
        ISnapTree<PathWithAccount>.DoBulkSetAndUpdateRootHash(tree, entries);

    public bool Commit(ValueHash256 upperBound)
    {
        PatriciaSnapProof.StitchBoundaries(_proofNodes, this);
        adapter.UpperBound = upperBound;
        tree.Commit(skipRoot: true, WriteFlags.DisableWAL);
        return _proofNodes is not { Count: > 0 } || _proofNodes[0].Item1.IsPersisted;
    }

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
