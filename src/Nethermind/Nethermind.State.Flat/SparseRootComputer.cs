// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Orchestrates proof-based state root computation for a single block using the sparse trie.
/// <remarks>
/// Before applying leaf updates, existing trie structure is revealed via multiproofs.
/// Accepts an external <see cref="SparseStateTrie"/> for cross-block reuse (M3).
/// </remarks>
/// </summary>
public sealed class SparseRootComputer : IDisposable
{
    private const int MaxRetries = 10;

    private readonly SparseStateTrie _trie;
    private readonly ITrieNodeReader _reader;
    private readonly Hash256 _previousStateRoot;
    private readonly bool _ownsTrie;
    /// <summary>
    /// Concurrent because <see cref="AddStorageChanges"/> is invoked from
    /// PersistentStorageProvider.UpdateRootHashesMultiThread's parallel worker threads,
    /// one per contract.
    /// </summary>
    private readonly ConcurrentDictionary<Hash256, (Hash256 PreviousStorageRoot, Dictionary<ValueHash256, LeafUpdate> Updates)> _storageChanges = new();
    private Dictionary<ValueHash256, LeafUpdate>? _accountChanges;

    public SparseRootComputer(ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(new SparseStateTrie(), reader, previousStateRoot, ownsTrie: true) { }

    public SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot)
        : this(trie, reader, previousStateRoot, ownsTrie: false) { }

    private SparseRootComputer(SparseStateTrie trie, ITrieNodeReader reader, Hash256 previousStateRoot, bool ownsTrie)
    {
        _trie = trie;
        _reader = reader;
        _previousStateRoot = previousStateRoot;
        _ownsTrie = ownsTrie;
    }

    public void AddStorageChanges(Hash256 accountPathHash, Hash256 previousStorageRoot, Dictionary<ValueHash256, LeafUpdate> slotUpdates) =>
        _storageChanges[accountPathHash] = (previousStorageRoot, slotUpdates);

    public void SetAccountChanges(Dictionary<ValueHash256, LeafUpdate> accountUpdates) =>
        _accountChanges = accountUpdates;

    // USDT contract hash — when DiagDumpForContract matches, we dump every proof read and
    // every revealed node to console so the offline reproducer has the actual mainnet bytes.
    private static readonly Hash256 _usdtHash = new("0xab14d68802a763f7db875346d03fbf86f137de55814b191c069e721f47474733");
    public static Hash256? DiagDumpForContract { get; set; }

    public Hash256 ComputeStorageRoot(Hash256 accountPathHash)
    {
        if (!_storageChanges.TryGetValue(accountPathHash, out (Hash256 PreviousStorageRoot, Dictionary<ValueHash256, LeafUpdate> Updates) entry))
            return Keccak.EmptyTreeHash;

        // Touch LFU for every slot we're about to update so hot slots survive Prune.
        // Default-disabled LFU (caps = int.MaxValue) means the per-slot virtual call would
        // be a wasted pass over the updates dictionary; HasAccountLfu / HasSlotLfu let us
        // short-circuit the loop entirely in the common case.
        if (_trie.HasAccountLfu)
            _trie.TouchLfu(accountPathHash);
        if (_trie.HasSlotLfu)
        {
            foreach (ValueHash256 slotKey in entry.Updates.Keys)
                _trie.TouchSlotLfu(accountPathHash, slotKey.ToCommitment());
        }

        if (entry.PreviousStorageRoot == Keccak.EmptyTreeHash && entry.Updates.Count == 0)
            return Keccak.EmptyTreeHash;

        bool diag = DiagDumpForContract is not null && accountPathHash == DiagDumpForContract;
        if (diag)
            Console.Error.WriteLine($"DIAG_STORAGE_ROOT_BEGIN addr={accountPathHash} prevRoot={entry.PreviousStorageRoot} updates={entry.Updates.Count}");

        SparsePatriciaTree storageTrie = _trie.GetOrCreateStorageTrie(accountPathHash);

        // Reth-style: try the cached sparse storage trie first. For empty trie (cold start),
        // reveal just the storage root node so UpdateLeaves has a starting point. The retry
        // loop then drives proof fetches for blinded targets only, using minLen.
        if (storageTrie.Subtrie.Root < 0 && entry.PreviousStorageRoot != Keccak.EmptyTreeHash)
        {
            byte[] rootRlp = _reader.LoadStorageRlp(accountPathHash, TreePath.Empty, entry.PreviousStorageRoot);
            ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
            if (diag) Console.Error.WriteLine($"DIAG_STORAGE_INITIAL_ROOT_RLP path=. hash={entry.PreviousStorageRoot} rlp=0x{Convert.ToHexString(rootRlp)}");
            storageTrie.RevealNodes([rootProof]);
        }
        else if (diag)
        {
            Console.Error.WriteLine($"DIAG_STORAGE_REUSE_EXISTING_TRIE subtrieRoot={storageTrie.Subtrie.Root} cachedRoot={(storageTrie.Subtrie.Root < 0 ? "empty" : storageTrie.ComputeRoot().ToString())}");
        }

        Dictionary<ValueHash256, LeafUpdate> updates = entry.Updates;
        ValueHash256? lastFirstTarget = null;
        int sameTargetCount = 0;
        // Drain-and-reinsert-misses, same as the account loop: first pass applies the whole
        // slot set, retries re-apply ONLY the prior pass's blinded misses. Storage-heavy
        // contracts (large slot sets, multiple blinded boundaries) are exactly where the old
        // full-dictionary-every-retry replay hurt most.
        ValueHash256[]? missBuffer = null;
        int missCount = -1; // -1 => first pass uses the full dictionary
        try
        {
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            List<(ValueHash256 key, byte minLen)> targets = [];
            if (missCount < 0)
                _trie.UpdateStorageLeaves(accountPathHash, updates, (key, minLen) => targets.Add((key, minLen)));
            else
                _trie.UpdateStorageLeavesSubset(accountPathHash, updates, missBuffer.AsSpan(0, missCount),
                    (key, minLen) => targets.Add((key, minLen)));
            if (targets.Count == 0) break;

            // Detect deletion-with-blinded-sibling stalls: the proof reader walks only target
            // paths, never the sibling that the collapse needs. If the same first target keeps
            // returning NeedsProof, resolve the blinded sibling directly from the sparse trie's
            // known blinded hashes. Mirrors the account-loop's TryResolveBlindedSiblings.
            ValueHash256 firstTarget = targets[0].key;
            if (lastFirstTarget is not null && lastFirstTarget.Value == firstTarget) sameTargetCount++;
            else sameTargetCount = 0;
            lastFirstTarget = firstTarget;

            if (sameTargetCount >= 1)
            {
                TryResolveBlindedStorageSiblings(storageTrie, accountPathHash, updates, targets);
            }

            if (retry == MaxRetries - 1)
            {
                // Dump the stuck state so we can build an offline reproducer
                Console.Error.WriteLine($"DIAG_STUCK_BEGIN addr={accountPathHash} prevRoot={entry.PreviousStorageRoot} updates={entry.Updates.Count}");
                foreach ((ValueHash256 key, byte _) in targets)
                {
                    Console.Error.WriteLine($"DIAG_STUCK_TARGET key={key} isDelete={(updates.TryGetValue(key, out LeafUpdate u) ? u.IsDelete : false)}");
                    byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                    if (storageTrie.Subtrie.TryFindBlindedEntryOnPath(nibbles, out TreePath bp, out RlpNode br, out int _))
                        Console.Error.WriteLine($"DIAG_STUCK_BLINDED_ON_PATH path={bp} rlp=0x{Convert.ToHexString(br.AsSpan())}");
                    else
                        Console.Error.WriteLine($"DIAG_STUCK_NO_BLINDED_ON_PATH key={key}");
                    if (storageTrie.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath sp, out RlpNode sr))
                        Console.Error.WriteLine($"DIAG_STUCK_BLINDED_SIBLING path={sp} rlp=0x{Convert.ToHexString(sr.AsSpan())}");
                    else
                        Console.Error.WriteLine($"DIAG_STUCK_NO_BLINDED_SIBLING key={key}");
                }
                Console.Error.WriteLine($"DIAG_STUCK_END");
                throw new TrieException($"Sparse trie storage retry loop exceeded {MaxRetries} iterations for account {accountPathHash}. {targets.Count} blinded targets remain.");
            }

            // P0 minLen: same blinded-boundary optimization as the account loop.
            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach ((ValueHash256 key, byte _) in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                if (storageTrie.Subtrie.TryFindBlindedEntryOnPath(
                    nibbles, out TreePath bPath, out RlpNode bRlp, out int _))
                {
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
                }
            }
            if (blinded.Count > 0)
            {
                if (diag)
                {
                    Console.Error.WriteLine($"DIAG_STORAGE_RETRY {retry} blindedEntries={blinded.Count}");
                    foreach (MultiProofReader.BlindedProofTarget b in blinded)
                    {
                        Console.Error.WriteLine($"DIAG_STORAGE_BLINDED path={b.BlindedPath} rlp=0x{Convert.ToHexString(b.BlindedRlp.AsSpan())} targetNibbles=0x{Convert.ToHexString(b.TargetNibbles)}");
                    }
                }
                DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(
                    _reader, accountPathHash, blinded);
                if (proof.StorageNodes.TryGetValue(accountPathHash, out List<ProofNode>? nodes))
                {
                    if (diag)
                    {
                        Console.Error.WriteLine($"DIAG_STORAGE_PROOF_NODES count={nodes.Count}");
                        foreach (ProofNode pn in nodes)
                        {
                            Console.Error.WriteLine($"DIAG_STORAGE_PROOF_NODE path={pn.Path} kind={pn.Kind} rawRlp=0x{Convert.ToHexString(pn.RawRlp ?? [])}");
                        }
                    }
                    storageTrie.RevealNodes(nodes);
                }
            }

            // Next retry re-applies only this pass's misses.
            if (missBuffer is null || missBuffer.Length < targets.Count)
            {
                if (missBuffer is not null) System.Buffers.ArrayPool<ValueHash256>.Shared.Return(missBuffer);
                missBuffer = System.Buffers.ArrayPool<ValueHash256>.Shared.Rent(targets.Count);
            }
            for (int t = 0; t < targets.Count; t++) missBuffer[t] = targets[t].key;
            missCount = targets.Count;
        }
        }
        finally
        {
            if (missBuffer is not null) System.Buffers.ArrayPool<ValueHash256>.Shared.Return(missBuffer);
        }

        Hash256 result = _trie.ComputeStorageRoot(accountPathHash);
        if (diag) Console.Error.WriteLine($"DIAG_STORAGE_ROOT_END addr={accountPathHash} computedRoot={result}");
        return result;
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

    /// <summary>The underlying trie for persistence and cross-block storage.</summary>
    internal SparseStateTrie Trie => _trie;

    /// <summary>Iterates address-hashes whose storage trie was modified this block. Persistence
    /// walks only these instead of every retained storage trie â€” important once
    /// PreservedSparseTrie keeps thousands of warm contracts across blocks. Lock-free
    /// enumeration of the concurrent dictionary (no snapshot copy).</summary>
    public IEnumerable<Hash256> DirtyStorageAccountHashes()
    {
        foreach (KeyValuePair<Hash256, (Hash256, Dictionary<ValueHash256, LeafUpdate>)> kvp in _storageChanges)
            yield return kvp.Key;
    }

    public Hash256 ComputeStateRoot()
    {
        if (_accountChanges is null || _accountChanges.Count == 0)
            return _previousStateRoot;

        // Touch LFU for every account we're about to update so hot accounts survive Prune.
        // Skip the loop entirely when the account-LFU is disabled (default Prune-off path).
        if (_trie.HasAccountLfu)
        {
            foreach (ValueHash256 accountKey in _accountChanges.Keys)
                _trie.TouchLfu(accountKey.ToCommitment());
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Reth-style: TRY THE CACHED SPARSE TRIE FIRST. Don't issue an unconditional
        // root-to-leaf proof read for every changed account. The first UpdateLeaves call
        // will report which accounts hit blinded nodes (and at what depth) via the minLen
        // callback. We fetch proofs only for those misses, starting at minLen — so warm
        // cross-block paths cost nothing.
        // Cold start (empty trie): the first retry will collect all targets at minLen=0
        // and fetch the same proofs the old initial call would have.
        if (_trie.AccountTrie.Subtrie.Root < 0 && _previousStateRoot != Keccak.EmptyTreeHash)
        {
            // Empty sparse trie with a non-empty parent — reveal the prevRoot node so
            // UpdateLeaves has a starting point. For Keccak.EmptyTreeHash the persistent
            // trie has no root node to load; UpdateLeaves will InsertLeaf directly into
            // the empty sparse trie.
            byte[] rootRlp = _reader.LoadStateRlp(TreePath.Empty, _previousStateRoot);
            ProofNode rootProof = MultiProofReader.DecodeProofNode(rootRlp, TreePath.Empty);
            _trie.AccountTrie.RevealNodes([rootProof]);
            LastProofNodeCount = 1;
        }
        else
        {
            LastProofNodeCount = 0;
        }

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastProofReadMs = (t1 - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        LastRevealMs = 0;

        long updateMsAccum = 0;
        ValueHash256? lastTarget = null;
        int sameTargetCount = 0;
        // Drain-and-reinsert-misses: the first pass applies the whole change set; every retry
        // afterwards re-applies ONLY the keys that hit a blinded node last time. Without this,
        // each proof-reveal retry re-sorts and re-walks the entire dictionary even though all but
        // a handful of leaves are already applied â€” O(retries Ã— N) instead of O(N + misses).
        ValueHash256[]? missBuffer = null;
        int missCount = -1; // -1 => first pass uses the full dictionary
        try
        {
            for (int retry = 0; retry < MaxRetries; retry++)
            {
                long ts = System.Diagnostics.Stopwatch.GetTimestamp();
                List<(ValueHash256 key, byte minLen)> targets = [];
                if (missCount < 0)
                {
                    _trie.UpdateAccountLeaves(_accountChanges, (key, minLen) => targets.Add((key, minLen)));
                }
                else
                {
                    _trie.UpdateAccountLeavesSubset(_accountChanges, missBuffer.AsSpan(0, missCount),
                        (key, minLen) => targets.Add((key, minLen)));
                }
                updateMsAccum += (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                LastRetryCount = retry;
                if (targets.Count == 0) break;

            // Detect stuck-on-same-target case: track if the same target keeps coming back
            ValueHash256 firstTarget = targets[0].key;
            if (lastTarget is not null && lastTarget.Value == firstTarget) sameTargetCount++;
            else sameTargetCount = 0;
            lastTarget = firstTarget;

            // For deletion-with-blinded-sibling: the proof reader doesn't fetch the sibling
            // (no target walks through that nibble). Resolve the sibling directly from the
            // sparse trie's known blinded hashes and inject it as a ProofNode.
            if (sameTargetCount >= 1)
            {
                List<ValueHash256> deletionTargets = [];
                foreach ((ValueHash256 k, _) in targets) deletionTargets.Add(k);
                TryResolveBlindedSiblings(deletionTargets);
            }

            if (retry == MaxRetries - 1)
                throw new TrieException(
                    $"Sparse trie account retry loop exceeded {MaxRetries} iterations. " +
                    $"{targets.Count} blinded targets remain. firstTarget={firstTarget}, " +
                    $"prevRoot={_previousStateRoot}, totalChanges={_accountChanges.Count}");

            // P0 minLen: for each blinded target, look up the blinded subtrie boundary
            // already revealed in the sparse trie. ReadProofsFromBlinded starts at THAT
            // subtrie's hash — one DB load per blinded subtrie, not a root-to-leaf walk.
            // Diagnostic isolation confirmed the previous wrong-root failure was in storage,
            // not here, so re-enable for accounts.
            List<MultiProofReader.BlindedProofTarget> blinded = [];
            foreach ((ValueHash256 key, byte _) in targets)
            {
                byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
                if (_trie.AccountTrie.Subtrie.TryFindBlindedEntryOnPath(
                    nibbles, out TreePath bPath, out RlpNode bRlp, out int _))
                {
                    blinded.Add(new MultiProofReader.BlindedProofTarget(bPath, bRlp, nibbles));
                }
            }
            if (blinded.Count > 0)
            {
                long tRead = System.Diagnostics.Stopwatch.GetTimestamp();
                DecodedMultiProof proof = MultiProofReader.ReadProofsFromBlinded(
                    _reader, accountPathHash: null, blinded);
                long tReveal = System.Diagnostics.Stopwatch.GetTimestamp();
                _trie.AccountTrie.RevealNodes(proof.AccountNodes);
                long tDone = System.Diagnostics.Stopwatch.GetTimestamp();
                LastProofReadMs += (tReveal - tRead) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                LastRevealMs += (tDone - tReveal) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                LastProofNodeCount += proof.AccountNodes.Count;
            }

            // Next retry re-applies only this pass's misses. Rent/grow a reusable buffer and
            // copy the miss keys into it; the subset overload sorts them in place.
            if (missBuffer is null || missBuffer.Length < targets.Count)
            {
                if (missBuffer is not null) System.Buffers.ArrayPool<ValueHash256>.Shared.Return(missBuffer);
                missBuffer = System.Buffers.ArrayPool<ValueHash256>.Shared.Rent(targets.Count);
            }
            for (int t = 0; t < targets.Count; t++) missBuffer[t] = targets[t].key;
            missCount = targets.Count;
            }
        }
        finally
        {
            if (missBuffer is not null) System.Buffers.ArrayPool<ValueHash256>.Shared.Return(missBuffer);
        }

        LastUpdateLeavesMs = updateMsAccum;
        long tc = System.Diagnostics.Stopwatch.GetTimestamp();
        Hash256 root = _trie.ComputeRoot();
        LastComputeRootMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tc) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        return root;
    }

    public void Dispose()
    {
        if (_ownsTrie) _trie.Dispose();
    }

    /// <summary>
    /// For deletion targets whose collapse would need a blinded sibling, fetch the sibling
    /// directly via the reader (using the hash stored as the blinded child's RlpNode) and
    /// reveal it. This unsticks the retry loop for blinded-sibling-deletion cases.
    /// </summary>
    private void TryResolveBlindedSiblings(List<ValueHash256> targets)
    {
        foreach (ValueHash256 target in targets)
        {
            if (!_accountChanges!.TryGetValue(target, out LeafUpdate upd) || !upd.IsDelete)
                continue;

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            if (!_trie.AccountTrie.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath siblingPath, out RlpNode siblingRlpNode))
                continue;

            // Hash form: load from DB. Inline form: decode locally â€” the blinded entry
            // holds the full RLP. Treating inline as "already known" without revealing leaves
            // CollapseBranch stuck on the blinded sibling forever.
            byte[] siblingRlp;
            if (siblingRlpNode.IsHash())
            {
                try
                {
                    Hash256 siblingHash = siblingRlpNode.AsHash();
                    siblingRlp = _reader.LoadStateRlp(siblingPath, siblingHash);
                }
                catch (Exception ex) when (ex is MissingTrieNodeException or TrieNodeHashMismatchException)
                {
                    // Best-effort: outer retry loop will hit the blinded sibling again and
                    // rethrow with full diagnostics there.
                    continue;
                }
            }
            else
            {
                siblingRlp = siblingRlpNode.AsSpan().ToArray();
            }

            ProofNode siblingProof = MultiProofReader.DecodeProofNode(siblingRlp, siblingPath);
            _trie.AccountTrie.RevealNodes([siblingProof]);
        }
    }

    /// <summary>
    /// Storage-side counterpart of <see cref="TryResolveBlindedSiblings"/>. When a slot
    /// deletion would collapse a branch whose sibling is blinded, the proof reader doesn't
    /// fetch the sibling (only walks target paths). The sibling's RlpNode is known from
    /// the sparse storage trie itself — load it directly and reveal.
    /// </summary>
    private void TryResolveBlindedStorageSiblings(
        SparsePatriciaTree storageTrie,
        Hash256 accountPathHash,
        Dictionary<ValueHash256, LeafUpdate> updates,
        List<(ValueHash256 key, byte minLen)> targets)
    {
        foreach ((ValueHash256 target, _) in targets)
        {
            if (!updates.TryGetValue(target, out LeafUpdate upd) || !upd.IsDelete)
                continue;

            byte[] nibbles = Nibbles.BytesToNibbleBytes(target.Bytes);
            if (!storageTrie.Subtrie.TryFindBlindedSiblingForDeletion(nibbles, out TreePath siblingPath, out RlpNode siblingRlpNode))
                continue;

            // The sibling's blinded entry can hold either a 32-byte hash (load from DB) or an
            // embedded RLP for inline nodes (< 32 bytes — typically a small Leaf). Both are
            // valid blinded states; the inline case happens for leaves whose RLP fits below
            // the keccak boundary and got embedded directly in the parent branch's slot.
            // Decode inline locally; load hash form from DB.
            byte[] siblingRlp;
            if (siblingRlpNode.IsHash())
            {
                try
                {
                    Hash256 siblingHash = siblingRlpNode.AsHash();
                    siblingRlp = _reader.LoadStorageRlp(accountPathHash, siblingPath, siblingHash);
                }
                catch (Exception ex) when (ex is MissingTrieNodeException or TrieNodeHashMismatchException)
                {
                    continue;
                }
            }
            else
            {
                siblingRlp = siblingRlpNode.AsSpan().ToArray();
            }

            ProofNode siblingProof = MultiProofReader.DecodeProofNode(siblingRlp, siblingPath);
            storageTrie.RevealNodes([siblingProof]);
        }
    }

}
