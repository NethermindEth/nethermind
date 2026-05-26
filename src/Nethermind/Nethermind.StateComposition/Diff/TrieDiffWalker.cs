// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker(bool trackDepth = false)
{
    private static readonly ValueHash256 s_emptyCodeHash = Keccak.OfAnEmptyString.ValueHash256;

    private readonly CumulativeDepthStats _depthDelta = new();
    private readonly List<SlotCountChange> _slotCountChanges = [];
    private readonly List<CodeHashChange> _codeHashChanges = [];

    private ITrieNodeResolver _rootResolver = null!;

    // Active contract context for per-account slot tracking.
    // Set by BeginContractStorage / cleared by EndContractStorage around every
    // storage-subtree walk — matches exactly one per-account payload entry.
    private ValueHash256 _currentContract;
    private long _currentContractSlotDelta;
    private bool _inContractStorage;

    private int _accountsAdded, _accountsRemoved;
    private int _contractsAdded, _contractsRemoved;

    // 2D counter tables indexed by [isStorage ? 1 : 0, NodeKind].
    // NodeKind mirrors the Branch=0, Extension=1, Leaf=2 layout used in RecordNode.
    // Collapses 16 scalar counters into two compact tables + two byte-total pairs.
    private const int AccountTrie = 0;
    private const int StorageTrie = 1;
    private const int BranchKind = 0;
    private const int ExtensionKind = 1;
    private const int LeafKind = 2;
    private readonly int[,] _trieNodesAdded = new int[2, 3];
    private readonly int[,] _trieNodesRemoved = new int[2, 3];
    private long _accountBytesAdded, _accountBytesRemoved;
    private long _storageBytesAdded, _storageBytesRemoved;

    private long _storageSlotsAdded, _storageSlotsRemoved;
    private int _contractsWithStorageAdded, _contractsWithStorageRemoved;
    private int _emptyAccountsAdded, _emptyAccountsRemoved;

    public TrieDiff ComputeDiff(Hash256? oldRoot, Hash256? newRoot, ITrieNodeResolver rootResolver)
    {
        _rootResolver = rootResolver;
        ResetCounters();
        if (trackDepth) _depthDelta.Reset();

        Hash256? oldHash = NormalizeHash(oldRoot);
        Hash256? newHash = NormalizeHash(newRoot);

        if (oldHash == newHash) return TrieDiff.Empty;

        TreePath path = TreePath.Empty;
        DiffSubtree(oldHash, newHash, ref path, _rootResolver, isStorage: false, depth: 0);

        return new TrieDiff(
            _accountsAdded, _accountsRemoved,
            _contractsAdded, _contractsRemoved,
            _trieNodesAdded[AccountTrie, BranchKind], _trieNodesRemoved[AccountTrie, BranchKind],
            _trieNodesAdded[AccountTrie, ExtensionKind], _trieNodesRemoved[AccountTrie, ExtensionKind],
            _trieNodesAdded[AccountTrie, LeafKind], _trieNodesRemoved[AccountTrie, LeafKind],
            _accountBytesAdded, _accountBytesRemoved,
            _trieNodesAdded[StorageTrie, BranchKind], _trieNodesRemoved[StorageTrie, BranchKind],
            _trieNodesAdded[StorageTrie, ExtensionKind], _trieNodesRemoved[StorageTrie, ExtensionKind],
            _trieNodesAdded[StorageTrie, LeafKind], _trieNodesRemoved[StorageTrie, LeafKind],
            _storageBytesAdded, _storageBytesRemoved,
            _storageSlotsAdded, _storageSlotsRemoved,
            _contractsWithStorageAdded, _contractsWithStorageRemoved,
            _emptyAccountsAdded, _emptyAccountsRemoved,
            // Clone so the TrieDiff outlives the next ComputeDiff, which Reset()s
            // _depthDelta in place.
            DepthDelta: _depthDelta.CloneAsDelta(),
            SlotCountChanges: _slotCountChanges.ToArray(),
            CodeHashChanges: _codeHashChanges.ToArray()
        );
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

    private void DiffSubtree(Hash256? oldHash, Hash256? newHash, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        if (oldHash == newHash) return;

        if (oldHash is null)
        {
            TrieNode newNode = resolver.FindCachedOrUnknown(in path, newHash!);
            newNode.ResolveNode(resolver, in path);
            CollectSubtree(newNode, ref path, resolver, isStorage, added: true, depth: depth);
            return;
        }

        if (newHash is null)
        {
            TrieNode oldNode = resolver.FindCachedOrUnknown(in path, oldHash);
            oldNode.ResolveNode(resolver, in path);
            CollectSubtree(oldNode, ref path, resolver, isStorage, added: false, depth: depth);
            return;
        }

        TrieNode oldResolved = resolver.FindCachedOrUnknown(in path, oldHash);
        oldResolved.ResolveNode(resolver, in path);
        TrieNode newResolved = resolver.FindCachedOrUnknown(in path, newHash);
        newResolved.ResolveNode(resolver, in path);

        DiffNodes(oldResolved, newResolved, ref path, resolver, isStorage, depth);
    }

    private void DiffNodes(TrieNode oldNode, TrieNode newNode, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        if (oldNode.NodeType != newNode.NodeType)
        {
            // Type mismatch (e.g., leaf → extension+branch on insert).
            // Can't just collect both subtrees independently — that would double-count
            // accounts/contracts/slots that exist in both. Instead, enumerate leaves
            // from both sides, match by full path, and diff semantically.
            DiffMismatchedNodes(oldNode, newNode, ref path, resolver, isStorage, depth);
            return;
        }

        switch (oldNode.NodeType)
        {
            case NodeType.Branch:
                DiffBranches(oldNode, newNode, ref path, resolver, isStorage, depth);
                break;
            case NodeType.Extension:
                DiffExtensions(oldNode, newNode, ref path, resolver, isStorage, depth);
                break;
            case NodeType.Leaf:
                DiffLeaves(oldNode, newNode, ref path, isStorage, depth);
                break;
        }
    }

    private static Hash256? NormalizeHash(Hash256? hash)
    {
        if (hash is null) return null;
        return hash == Keccak.EmptyTreeHash ? null : hash;
    }

    private static int CountBranchChildren(TrieNode branch)
    {
        int count = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!branch.IsChildNull(i)) count++;
        }
        return count;
    }

    private void ResetCounters()
    {
        _accountsAdded = 0; _accountsRemoved = 0;
        _contractsAdded = 0; _contractsRemoved = 0;
        Array.Clear(_trieNodesAdded);
        Array.Clear(_trieNodesRemoved);
        _accountBytesAdded = 0; _accountBytesRemoved = 0;
        _storageBytesAdded = 0; _storageBytesRemoved = 0;
        _storageSlotsAdded = 0; _storageSlotsRemoved = 0;
        _contractsWithStorageAdded = 0; _contractsWithStorageRemoved = 0;
        _emptyAccountsAdded = 0; _emptyAccountsRemoved = 0;

        _slotCountChanges.Clear();
        _codeHashChanges.Clear();
        _currentContract = default;
        _currentContractSlotDelta = 0;
        _inContractStorage = false;
    }
}
