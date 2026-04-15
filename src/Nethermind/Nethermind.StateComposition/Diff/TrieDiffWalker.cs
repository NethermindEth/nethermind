// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker(ITrieNodeResolver rootResolver, bool trackDepth = false)
{
    private readonly CumulativeDepthStats _depthDelta = new();
    private readonly List<SlotCountChange> _slotCountChanges = new();
    private readonly List<CodeHashChange> _codeHashChanges = new();

    // Active contract context for per-account slot tracking.
    // Set by BeginContractStorage / cleared by EndContractStorage around every
    // storage-subtree walk — matches exactly one per-account payload entry.
    private ValueHash256 _currentContract;
    private long _currentContractSlotDelta;
    private bool _inContractStorage;

    private int _accountsAdded, _accountsRemoved;
    private int _contractsAdded, _contractsRemoved;
    private int _accountTrieBranchesAdded, _accountTrieBranchesRemoved;
    private int _accountTrieExtensionsAdded, _accountTrieExtensionsRemoved;
    private int _accountTrieLeavesAdded, _accountTrieLeavesRemoved;
    private long _accountTrieBytesAdded, _accountTrieBytesRemoved;
    private int _storageTrieBranchesAdded, _storageTrieBranchesRemoved;
    private int _storageTrieExtensionsAdded, _storageTrieExtensionsRemoved;
    private int _storageTrieLeavesAdded, _storageTrieLeavesRemoved;
    private long _storageTrieBytesAdded, _storageTrieBytesRemoved;
    private long _storageSlotsAdded, _storageSlotsRemoved;
    private int _contractsWithStorageAdded, _contractsWithStorageRemoved;
    private int _emptyAccountsAdded, _emptyAccountsRemoved;

    public TrieDiff ComputeDiff(Hash256? oldRoot, Hash256? newRoot)
    {
        ResetCounters();
        if (trackDepth) _depthDelta.Reset();

        Hash256? oldHash = NormalizeHash(oldRoot);
        Hash256? newHash = NormalizeHash(newRoot);

        if (oldHash == newHash) return default;

        TreePath path = TreePath.Empty;
        DiffSubtree(oldHash, newHash, ref path, rootResolver, isStorage: false, depth: 0);

        return new TrieDiff(
            _accountsAdded, _accountsRemoved,
            _contractsAdded, _contractsRemoved,
            _accountTrieBranchesAdded, _accountTrieBranchesRemoved,
            _accountTrieExtensionsAdded, _accountTrieExtensionsRemoved,
            _accountTrieLeavesAdded, _accountTrieLeavesRemoved,
            _accountTrieBytesAdded, _accountTrieBytesRemoved,
            _storageTrieBranchesAdded, _storageTrieBranchesRemoved,
            _storageTrieExtensionsAdded, _storageTrieExtensionsRemoved,
            _storageTrieLeavesAdded, _storageTrieLeavesRemoved,
            _storageTrieBytesAdded, _storageTrieBytesRemoved,
            _storageSlotsAdded, _storageSlotsRemoved,
            _contractsWithStorageAdded, _contractsWithStorageRemoved,
            _emptyAccountsAdded, _emptyAccountsRemoved,
            DepthDelta: trackDepth ? _depthDelta : null,
            SlotCountChanges: _slotCountChanges.Count > 0 ? _slotCountChanges.ToArray() : null,
            CodeHashChanges: _codeHashChanges.Count > 0 ? _codeHashChanges.ToArray() : null
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
        ValueHash256 emptyCode = Keccak.OfAnEmptyString.ValueHash256;
        ValueHash256 oldNormalized = oldCodeHash == emptyCode ? CodeHashChange.NoCode : oldCodeHash;
        ValueHash256 newNormalized = newCodeHash == emptyCode ? CodeHashChange.NoCode : newCodeHash;

        if (oldNormalized == newNormalized) return;

        _codeHashChanges.Add(new CodeHashChange(addressHash, oldNormalized, newNormalized));
    }

    private void DiffSubtree(Hash256? oldHash, Hash256? newHash, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        // Fast path: identical subtrees
        if (oldHash == newHash) return;

        // One side is empty → collect entire other side
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

        // Both exist and differ → resolve and compare
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
        _accountTrieBranchesAdded = 0; _accountTrieBranchesRemoved = 0;
        _accountTrieExtensionsAdded = 0; _accountTrieExtensionsRemoved = 0;
        _accountTrieLeavesAdded = 0; _accountTrieLeavesRemoved = 0;
        _accountTrieBytesAdded = 0; _accountTrieBytesRemoved = 0;
        _storageTrieBranchesAdded = 0; _storageTrieBranchesRemoved = 0;
        _storageTrieExtensionsAdded = 0; _storageTrieExtensionsRemoved = 0;
        _storageTrieLeavesAdded = 0; _storageTrieLeavesRemoved = 0;
        _storageTrieBytesAdded = 0; _storageTrieBytesRemoved = 0;
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
