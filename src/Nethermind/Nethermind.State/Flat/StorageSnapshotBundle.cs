// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

// TODO: Maybe remove this class?
public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    internal Hash256 _addressHash = address.ToAccountPath.ToCommitment();

    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructStateIdx(address);
    public int HintSequenceId => bundle.HintSequenceId;

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        return bundle.TryGetSlot(address, index, _selfDestructKnownStateIdx, out value);
    }

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode value)
    {
        return bundle.TryFindNode(_addressHash, path, hash, _selfDestructKnownStateIdx, out value);
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        return bundle.TryLoadRlp(_addressHash, in path, hash, flags);
    }

    public void SetNode(TreePath path, TrieNode node)
    {
        bundle.SetNode(_addressHash, path, node);
    }

    public void SetNodeHint(in TreePath path, TrieNode node)
    {
        bundle.HintTrieNode(_addressHash, path, node);
    }

    public void Set(UInt256 slot, byte[] value)
    {
        bundle.SetChangedSlot(address, slot, value);
    }

    public void MaybePreReadSlot(UInt256 slot, int sequenceId)
    {
        bundle.MaybePreReadSlot(address, slot, sequenceId);
    }

    public void SelfDestruct()
    {
        bundle.Clear(address, _addressHash);
        _selfDestructKnownStateIdx = bundle.GetSelfDestructKnownStateId();
    }

    public void Dispose()
    {
    }
}
