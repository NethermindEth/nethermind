// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.StateDiff.Core.Data;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateDiff.Core.Diff;

/// <summary>
/// Walks two state roots in parallel and emits per-contract slot-count and per-account
/// code-hash changes, plus net byte / leaf deltas.
/// </summary>
public sealed partial class TrieDiffWalker
{
    private static readonly ValueHash256 s_emptyCodeHash = Keccak.OfAnEmptyString.ValueHash256;

    private readonly List<SlotCountChange> _slotCountChanges = [];
    private readonly List<CodeHashChange> _codeHashChanges = [];

    private long _accountTrieBytesDelta;
    private long _storageTrieBytesDelta;
    private long _accountsAddedDelta;

    // FlatDb scopes nodes per state, so each side needs its own resolver;
    // a single resolver on the new head makes the prev root's disjoint subtrees Unknown.
    private readonly record struct ResolverPair(ITrieNodeResolver Old, ITrieNodeResolver New)
    {
        public ITrieNodeResolver Pick(bool added) => added ? New : Old;
        public ResolverPair ForStorage(Hash256? address) =>
            new(Old.GetStorageTrieNodeResolver(address), New.GetStorageTrieNodeResolver(address));
    }

    private ValueHash256 _currentContract;
    private long _currentContractSlotDelta;
    private bool _inContractStorage;

    public TrieDiff ComputeDiff(Hash256? oldRoot, Hash256? newRoot,
        ITrieNodeResolver oldResolver, ITrieNodeResolver newResolver)
    {
        ResolverPair resolvers = new(oldResolver, newResolver);
        ResetState();

        Hash256? oldHash = NormalizeHash(oldRoot);
        Hash256? newHash = NormalizeHash(newRoot);

        if (oldHash == newHash) return TrieDiff.Empty;

        TreePath path = TreePath.Empty;
        DiffSubtree(oldHash, newHash, ref path, resolvers, isStorage: false);

        return new TrieDiff(
            SlotCountChanges: _slotCountChanges.ToArray(),
            CodeHashChanges: _codeHashChanges.ToArray(),
            AccountTrieBytesDelta: _accountTrieBytesDelta,
            StorageTrieBytesDelta: _storageTrieBytesDelta,
            AccountsAddedDelta: _accountsAddedDelta);
    }

    private void RecordNodeBytes(int rlpLength, bool isStorage, bool added)
    {
        long signed = added ? rlpLength : -rlpLength;
        if (isStorage) _storageTrieBytesDelta += signed;
        else _accountTrieBytesDelta += signed;
    }

    private void BeginContractStorage(in ValueHash256 addressHash)
    {
        _currentContract = addressHash;
        _currentContractSlotDelta = 0;
        _inContractStorage = true;
    }

    private void EndContractStorage()
    {
        if (_inContractStorage && _currentContractSlotDelta != 0)
        {
            _slotCountChanges.Add(new SlotCountChange(_currentContract, _currentContractSlotDelta));
        }

        _inContractStorage = false;
        _currentContractSlotDelta = 0;
    }

    private void RecordCodeHashChange(in ValueHash256 addressHash, in ValueHash256 oldCodeHash, in ValueHash256 newCodeHash)
    {
        ValueHash256 oldNormalized = oldCodeHash == s_emptyCodeHash ? CodeHashChange.NoCode : oldCodeHash;
        ValueHash256 newNormalized = newCodeHash == s_emptyCodeHash ? CodeHashChange.NoCode : newCodeHash;

        if (oldNormalized == newNormalized) return;

        _codeHashChanges.Add(new CodeHashChange(addressHash, oldNormalized, newNormalized));
    }

    private void DiffSubtree(Hash256? oldHash, Hash256? newHash, ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        if (oldHash == newHash) return;

        if (oldHash is null)
        {
            TrieNode newNode = resolvers.New.FindCachedOrUnknown(in path, newHash!);
            newNode.ResolveNode(resolvers.New, in path);
            CollectSubtree(newNode, ref path, resolvers, isStorage, added: true);
            return;
        }

        if (newHash is null)
        {
            TrieNode oldNode = resolvers.Old.FindCachedOrUnknown(in path, oldHash);
            oldNode.ResolveNode(resolvers.Old, in path);
            CollectSubtree(oldNode, ref path, resolvers, isStorage, added: false);
            return;
        }

        TrieNode oldResolved = resolvers.Old.FindCachedOrUnknown(in path, oldHash);
        oldResolved.ResolveNode(resolvers.Old, in path);
        TrieNode newResolved = resolvers.New.FindCachedOrUnknown(in path, newHash);
        newResolved.ResolveNode(resolvers.New, in path);

        DiffNodes(oldResolved, newResolved, ref path, resolvers, isStorage);
    }

    private void DiffNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        if (oldNode.NodeType != newNode.NodeType)
        {
            // Match leaves by full path across both sides; otherwise leaves present on
            // both would be double-counted as add+remove.
            DiffMismatchedNodes(oldNode, newNode, ref path, resolvers, isStorage);
            return;
        }

        switch (oldNode.NodeType)
        {
            case NodeType.Branch:
                DiffBranches(oldNode, newNode, ref path, resolvers, isStorage);
                break;
            case NodeType.Extension:
                DiffExtensions(oldNode, newNode, ref path, resolvers, isStorage);
                break;
            case NodeType.Leaf:
                DiffLeaves(oldNode, newNode, ref path, resolvers, isStorage);
                break;
        }
    }

    private static Hash256? NormalizeHash(Hash256? hash)
    {
        if (hash is null) return null;
        return hash == Keccak.EmptyTreeHash ? null : hash;
    }

    private void ResetState()
    {
        _slotCountChanges.Clear();
        _codeHashChanges.Clear();
        _accountTrieBytesDelta = 0;
        _storageTrieBytesDelta = 0;
        _accountsAddedDelta = 0;
        _currentContract = default;
        _currentContractSlotDelta = 0;
        _inContractStorage = false;
    }
}
