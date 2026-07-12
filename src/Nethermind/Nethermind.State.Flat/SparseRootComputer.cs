// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Applies proof-backed changes to a sparse state trie and computes its roots.
/// </summary>
/// <remarks>
/// An externally supplied trie remains owned by the caller and can be retained across blocks.
/// Incremental apply methods let a single-owner worker reveal and mutate paths while execution is
/// still producing changes; the compatibility methods retain the original synchronous API.
/// </remarks>
public sealed class SparseRootComputer : IDisposable
{
    private const int MaxTriePathRetries = 65;

    private readonly SparseStateTrie _trie;
    private readonly ITrieNodeReader _reader;
    private readonly Hash256 _previousStateRoot;
    private readonly bool _ownsTrie;
    private readonly ConcurrentDictionary<Hash256, (Hash256 PreviousStorageRoot, Dictionary<ValueHash256, LeafUpdate> Updates)> _storageChanges = new();
    private readonly ConcurrentDictionary<Hash256, Hash256> _storageBaseRoots = new();
    private Dictionary<ValueHash256, LeafUpdate>? _accountChanges;

    public SparseRootComputer(ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(new SparseStateTrie(), reader, previousStateRoot, ownsTrie: true)
    {
    }

    public SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(trie, reader, previousStateRoot, ownsTrie: false)
    {
    }

    private SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot, bool ownsTrie)
    {
        _trie = trie;
        _reader = reader;
        _previousStateRoot = previousStateRoot;
        _ownsTrie = ownsTrie;
    }

    public void AddStorageChanges(
        Hash256 accountPathHash,
        Hash256 previousStorageRoot,
        Dictionary<ValueHash256, LeafUpdate> slotUpdates) =>
        _storageChanges[accountPathHash] = (previousStorageRoot, slotUpdates);

    public void SetAccountChanges(Dictionary<ValueHash256, LeafUpdate> accountUpdates) =>
        _accountChanges = accountUpdates;

    /// <summary>
    /// Applies the registered changes for one storage trie and computes its root.
    /// </summary>
    public Hash256 ComputeStorageRoot(Hash256 accountPathHash)
    {
        if (!_storageChanges.TryGetValue(
            accountPathHash,
            out (Hash256 PreviousStorageRoot, Dictionary<ValueHash256, LeafUpdate> Updates) entry))
        {
            return Keccak.EmptyTreeHash;
        }

        ApplyStorageChanges(accountPathHash, entry.PreviousStorageRoot, entry.Updates);
        return _trie.ComputeStorageRoot(accountPathHash);
    }

    /// <summary>
    /// Reveals and applies storage leaves without hashing the storage root.
    /// </summary>
    internal void ApplyStorageChanges(
        Hash256 accountPathHash,
        Hash256 previousStorageRoot,
        Dictionary<ValueHash256, LeafUpdate> updates)
    {
        _storageChanges[accountPathHash] = (previousStorageRoot, updates);
        if (updates.Count == 0)
            return;

        SparsePatriciaTree storageTrie = _trie.GetOrCreateStorageTrie(accountPathHash);
        if (_storageBaseRoots.TryAdd(accountPathHash, previousStorageRoot))
        {
            Hash256 retainedRoot = storageTrie.ComputeRoot(allowParallel: false);
            if (retainedRoot != previousStorageRoot)
            {
                _trie.WipeStorage(accountPathHash);
                storageTrie = _trie.GetOrCreateStorageTrie(accountPathHash);
            }

            if (storageTrie.Subtrie.IsEmpty && previousStorageRoot != Keccak.EmptyTreeHash)
            {
                byte[] rootRlp = _reader.LoadStorageRlp(
                    accountPathHash,
                    TreePath.Empty,
                    previousStorageRoot);
                storageTrie.RevealNodes([MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty)]);
            }
        }
        else if (_storageBaseRoots[accountPathHash] != previousStorageRoot)
        {
            throw new InvalidOperationException(
                $"Conflicting parent storage roots for account {accountPathHash}.");
        }

        ApplyStorageLeaves(storageTrie, accountPathHash, previousStorageRoot, updates);
    }

    /// <summary>
    /// Computes an already-mutated storage trie. Callers may run this concurrently for distinct
    /// account hashes once all mutations have stopped.
    /// </summary>
    internal Hash256 ComputeAppliedStorageRoot(Hash256 accountPathHash, bool allowParallel) =>
        _trie.ComputeStorageRoot(accountPathHash, allowParallel);

    internal void WipeStorage(Hash256 accountPathHash)
    {
        _trie.WipeStorage(accountPathHash);
        _storageBaseRoots[accountPathHash] = Keccak.EmptyTreeHash;
    }

    private void ApplyStorageLeaves(
        SparsePatriciaTree storageTrie,
        Hash256 accountPathHash,
        Hash256 previousStorageRoot,
        Dictionary<ValueHash256, LeafUpdate> updates)
    {
        ValueHash256? lastFirstTarget = null;
        int sameTargetCount = 0;
        ValueHash256[]? missBuffer = null;
        int missCount = -1;

        try
        {
            for (int retry = 0; retry < MaxTriePathRetries; retry++)
            {
                List<(ValueHash256 Key, byte MinLength)> targets = [];
                if (missCount < 0)
                {
                    _trie.UpdateStorageLeaves(
                        accountPathHash,
                        updates,
                        (key, minLength) => targets.Add((key, minLength)));
                }
                else
                {
                    _trie.UpdateStorageLeavesSubset(
                        accountPathHash,
                        updates,
                        missBuffer.AsSpan(0, missCount),
                        (key, minLength) => targets.Add((key, minLength)));
                }

                if (targets.Count == 0)
                    return;

                ValueHash256 firstTarget = targets[0].Key;
                sameTargetCount = lastFirstTarget is not null && lastFirstTarget.Value == firstTarget
                    ? sameTargetCount + 1
                    : 0;
                lastFirstTarget = firstTarget;

                if (sameTargetCount > 0)
                    TryResolveBlindedStorageSiblings(storageTrie, accountPathHash, updates, targets);

                if (retry == MaxTriePathRetries - 1)
                {
                    throw new TrieException(
                        $"Sparse storage retry loop exceeded {MaxTriePathRetries} iterations for account " +
                        $"{accountPathHash}. {targets.Count} blinded targets remain; " +
                        $"parentRoot={previousStorageRoot}.");
                }

                List<MultiProofReader.BlindedProofTarget> blinded = BuildBlindedTargets(
                    storageTrie.Subtrie,
                    targets);
                if (blinded.Count > 0)
                {
                    DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(
                        _reader,
                        accountPathHash,
                        blinded);
                    if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                        storageTrie.RevealNodes(nodes);
                }

                missBuffer = CopyMisses(targets, missBuffer);
                missCount = targets.Count;
            }
        }
        finally
        {
            if (missBuffer is not null)
                ArrayPool<ValueHash256>.Shared.Return(missBuffer);
        }
    }

    public Hash256 PreviousRoot => _previousStateRoot;
    public int AccountChangeCount => _accountChanges?.Count ?? 0;
    public int LastProofNodeCount { get; private set; }
    public long LastProofReadMs { get; private set; }
    public long LastRevealMs { get; private set; }
    public long LastUpdateLeavesMs { get; private set; }
    public long LastComputeRootMs { get; private set; }
    public int LastRetryCount { get; private set; }
    internal Dictionary<ValueHash256, LeafUpdate>? LastAccountChanges => _accountChanges;

    internal SparseStateTrie Trie => _trie;

    public IEnumerable<Hash256> DirtyStorageAccountHashes()
    {
        foreach (KeyValuePair<Hash256, (Hash256, Dictionary<ValueHash256, LeafUpdate>)> entry in _storageChanges)
            yield return entry.Key;
    }

    /// <summary>
    /// Applies the registered account changes and computes the state root.
    /// </summary>
    public Hash256 ComputeStateRoot()
    {
        if (_accountChanges is null || _accountChanges.Count == 0)
            return _previousStateRoot;

        ApplyAccountChanges(_accountChanges);
        return ComputeAppliedStateRoot();
    }

    /// <summary>
    /// Reveals and applies account leaves without hashing the state root.
    /// </summary>
    internal void ApplyAccountChanges(Dictionary<ValueHash256, LeafUpdate> updates)
    {
        if (updates.Count == 0)
            return;

        long startedAt = Stopwatch.GetTimestamp();
        EnsureAccountRootRevealed();
        long revealedAt = Stopwatch.GetTimestamp();
        LastProofReadMs += ToMilliseconds(revealedAt - startedAt);

        ValueHash256? lastTarget = null;
        int sameTargetCount = 0;
        ValueHash256[]? missBuffer = null;
        int missCount = -1;

        try
        {
            for (int retry = 0; retry < MaxTriePathRetries; retry++)
            {
                long updateStartedAt = Stopwatch.GetTimestamp();
                List<(ValueHash256 Key, byte MinLength)> targets = [];
                if (missCount < 0)
                {
                    _trie.UpdateAccountLeaves(
                        updates,
                        (key, minLength) => targets.Add((key, minLength)));
                }
                else
                {
                    _trie.UpdateAccountLeavesSubset(
                        updates,
                        missBuffer.AsSpan(0, missCount),
                        (key, minLength) => targets.Add((key, minLength)));
                }

                LastUpdateLeavesMs += ToMilliseconds(Stopwatch.GetTimestamp() - updateStartedAt);
                LastRetryCount = retry;
                if (targets.Count == 0)
                    return;

                ValueHash256 firstTarget = targets[0].Key;
                sameTargetCount = lastTarget is not null && lastTarget.Value == firstTarget
                    ? sameTargetCount + 1
                    : 0;
                lastTarget = firstTarget;

                if (sameTargetCount > 0)
                    TryResolveBlindedAccountSiblings(updates, targets);

                if (retry == MaxTriePathRetries - 1)
                {
                    throw new TrieException(
                        $"Sparse account retry loop exceeded {MaxTriePathRetries} iterations. " +
                        $"{targets.Count} blinded targets remain; firstTarget={firstTarget}; " +
                        $"parentRoot={_previousStateRoot}; changes={updates.Count}.");
                }

                List<MultiProofReader.BlindedProofTarget> blinded = BuildBlindedTargets(
                    _trie.AccountTrie.Subtrie,
                    targets);
                if (blinded.Count > 0)
                {
                    long proofStartedAt = Stopwatch.GetTimestamp();
                    DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(
                        _reader,
                        accountPathHash: null,
                        blinded);
                    long proofReadAt = Stopwatch.GetTimestamp();
                    _trie.AccountTrie.RevealNodes(proof.AccountNodes);
                    LastProofReadMs += ToMilliseconds(proofReadAt - proofStartedAt);
                    LastRevealMs += ToMilliseconds(Stopwatch.GetTimestamp() - proofReadAt);
                    LastProofNodeCount += proof.AccountNodes.Count;
                }

                missBuffer = CopyMisses(targets, missBuffer);
                missCount = targets.Count;
            }
        }
        finally
        {
            if (missBuffer is not null)
                ArrayPool<ValueHash256>.Shared.Return(missBuffer);
        }
    }

    internal Hash256 ComputeAppliedStateRoot()
    {
        long startedAt = Stopwatch.GetTimestamp();
        Hash256 root = _trie.ComputeRoot();
        LastComputeRootMs += ToMilliseconds(Stopwatch.GetTimestamp() - startedAt);
        return root;
    }

    private void EnsureAccountRootRevealed()
    {
        SparsePatriciaTree accountTrie = _trie.AccountTrie;
        if (!accountTrie.Subtrie.IsEmpty || _previousStateRoot == Keccak.EmptyTreeHash)
            return;

        byte[] rootRlp = _reader.LoadStateRlp(TreePath.Empty, _previousStateRoot);
        accountTrie.RevealNodes([MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty)]);
        LastProofNodeCount++;
    }

    private static List<MultiProofReader.BlindedProofTarget> BuildBlindedTargets(
        SparseSubtrie subtrie,
        List<(ValueHash256 Key, byte MinLength)> targets)
    {
        List<MultiProofReader.BlindedProofTarget> blinded = [];
        foreach ((ValueHash256 key, byte _) in targets)
        {
            byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
            if (subtrie.TryFindBlindedEntryOnPath(
                nibbles,
                out TreePath path,
                out RlpNode rlp,
                out int _))
            {
                blinded.Add(new MultiProofReader.BlindedProofTarget(path, rlp, nibbles));
            }
        }

        return blinded;
    }

    private static ValueHash256[] CopyMisses(
        List<(ValueHash256 Key, byte MinLength)> targets,
        ValueHash256[]? buffer)
    {
        if (buffer is null || buffer.Length < targets.Count)
        {
            if (buffer is not null)
                ArrayPool<ValueHash256>.Shared.Return(buffer);
            buffer = ArrayPool<ValueHash256>.Shared.Rent(targets.Count);
        }

        for (int i = 0; i < targets.Count; i++)
            buffer[i] = targets[i].Key;
        return buffer;
    }

    private void TryResolveBlindedAccountSiblings(
        Dictionary<ValueHash256, LeafUpdate> updates,
        List<(ValueHash256 Key, byte MinLength)> targets)
    {
        foreach ((ValueHash256 target, byte _) in targets)
        {
            if (!updates.TryGetValue(target, out LeafUpdate update) || !update.IsDelete)
                continue;

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            if (!_trie.AccountTrie.Subtrie.TryFindBlindedSiblingForDeletion(
                nibbles,
                out TreePath siblingPath,
                out RlpNode siblingNode))
            {
                continue;
            }

            byte[] siblingRlp;
            if (siblingNode.IsHash())
            {
                try
                {
                    siblingRlp = _reader.LoadStateRlp(siblingPath, siblingNode.AsHash());
                }
                catch (Exception exception) when (exception is MissingTrieNodeException or TrieNodeHashMismatchException)
                {
                    continue;
                }
            }
            else
            {
                siblingRlp = siblingNode.AsSpan().ToArray();
            }

            _trie.AccountTrie.RevealNodes(
                [MultiProofReader.DecodeProofNode(siblingRlp, siblingPath)]);
        }
    }

    private void TryResolveBlindedStorageSiblings(
        SparsePatriciaTree storageTrie,
        Hash256 accountPathHash,
        Dictionary<ValueHash256, LeafUpdate> updates,
        List<(ValueHash256 Key, byte MinLength)> targets)
    {
        foreach ((ValueHash256 target, byte _) in targets)
        {
            if (!updates.TryGetValue(target, out LeafUpdate update) || !update.IsDelete)
                continue;

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            if (!storageTrie.Subtrie.TryFindBlindedSiblingForDeletion(
                nibbles,
                out TreePath siblingPath,
                out RlpNode siblingNode))
            {
                continue;
            }

            byte[] siblingRlp;
            if (siblingNode.IsHash())
            {
                try
                {
                    siblingRlp = _reader.LoadStorageRlp(
                        accountPathHash,
                        siblingPath,
                        siblingNode.AsHash());
                }
                catch (Exception exception) when (exception is MissingTrieNodeException or TrieNodeHashMismatchException)
                {
                    continue;
                }
            }
            else
            {
                siblingRlp = siblingNode.AsSpan().ToArray();
            }

            storageTrie.RevealNodes([MultiProofReader.DecodeProofNode(siblingRlp, siblingPath)]);
        }
    }

    private static long ToMilliseconds(long ticks) =>
        ticks * 1000 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_ownsTrie)
            _trie.Dispose();
    }
}
