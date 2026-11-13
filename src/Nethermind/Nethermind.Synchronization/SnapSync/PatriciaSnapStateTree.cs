// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapStateTree(StateTree tree, SnapUpperBoundAdapter adapter, INodeStorage nodeStorage) : ISnapTree<IPathWithAccountList>
{
    public Hash256 RootHash => tree.RootHash;

    public void SetRootFromProof(TrieNode root) => tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
        nodeStorage.KeyExists(null, path, keccak);

    public void BulkSetAndUpdateRootHash(IPathWithAccountList entries) =>
        ISnapTree<IPathWithAccountList>.DoBulkSetAndUpdateRootHash(tree, entries);

    public void Commit(ValueHash256 upperBound)
    {
        adapter.UpperBound = upperBound;
        tree.Commit(skipRoot: true, WriteFlags.DisableWAL);
    }

    public void Dispose() { } // No-op - Patricia doesn't own resources
}
