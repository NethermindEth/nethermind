// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class StorageSnapshotBundle(Address address, SnapshotBundle bundle)
{
    internal Hash256 _addressHash = address.ToAccountPath.ToCommitment();

    bool _hasSelfDestruct = false;
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructStateIdx(address);

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        if (bundle.TryGetChangedSlot(address, index, out value))
        {
            return true;
        }

        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

        return bundle.TryGetSlot(address, index, _selfDestructKnownStateIdx, out value);
    }

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode value)
    {
        if (bundle.TryGetChangedNode(_addressHash, path, hash, out value))
        {
            return true;
        }

        if (_hasSelfDestruct)
        {
            value = null;
            return true;
        }

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

    public void Set(UInt256 slot, byte[] value)
    {
        bundle.SetChangedSlot(address, slot, value);
    }

    public bool HintGet(UInt256 slot, byte[] value)
    {
        return bundle.TryAdd(address, slot, value);
    }

    public void SelfDestruct()
    {
        _hasSelfDestruct = true;
        bundle.Clear(address, _addressHash);
    }

    public void Dispose()
    {
    }
}
