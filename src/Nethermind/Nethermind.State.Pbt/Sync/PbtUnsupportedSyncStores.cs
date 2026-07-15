// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.Sync;

/// <summary>The PBT backend has no state sync; construction is cheap and every operation throws.</summary>
public sealed class PbtUnsupportedSnapTrieFactory : ISnapTrieFactory
{
    public ISnapTree<PathWithAccount> CreateStateTree() => throw Unsupported();

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("Snap sync is not supported by the pbt state backend");
}

/// <inheritdoc cref="PbtUnsupportedSnapTrieFactory"/>
public sealed class PbtUnsupportedTreeSyncStore : ITreeSyncStore
{
    public bool NodeExists(Hash256? address, in TreePath path, in ValueHash256 hash) => throw Unsupported();

    public void SaveNode(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data) => throw Unsupported();

    public void EnsureStorageEmpty(Hash256 address) => throw Unsupported();

    public void FinalizeSync(BlockHeader pivotHeader) => throw Unsupported();

    public ITreeSyncVerificationContext CreateVerificationContext(byte[] rootNodeData) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("Fast sync is not supported by the pbt state backend");
}
