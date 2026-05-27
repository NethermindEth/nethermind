// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly PreservedSparseTrie? _preservedSparseTrie;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private SparseRootComputer? _sparseRootComputer;
    private SparseStateTrie? _sparseStateTrie;
    private Hash256? _sparseComputedRoot;
    private bool _sparseIsAuthoritative;
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;

    private static int _sparseMatchCount;
    private static int _sparseMismatchCount;
    private static int _sparseFailCount;
    private static int _consecutiveMatches;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        PreservedSparseTrie? preservedSparseTrie,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;
        _preservedSparseTrie = preservedSparseTrie;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount);
        _stateTree = new(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _warmupStateTree = new(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        if (configuration.UseSparseRootComputation && !isReadOnly)
        {
            Hash256 prevRoot = currentStateId.StateRoot.ToCommitment();
            ParentStateTrieNodeReader proofReader = new(snapshotBundle);

            // M2: always fresh trie per block. Cross-block reuse deferred to M3.
            _sparseRootComputer = new SparseRootComputer(proofReader, prevRoot);

            // After enough consecutive matches, skip Patricia UpdateRootHash (unless verification mode is on)
            _sparseIsAuthoritative = !configuration.SparseTrieVerificationMode
                && Volatile.Read(ref _consecutiveMatches) >= 10;
        }

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        WaitForOutstandingWarmups();

        // Return the sparse trie to the preserved store if it was checked out but never
        // stored back (e.g., scope disposed without Commit due to reorg or exception).
        if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
        {
            try { _preservedSparseTrie.StoreCleared(_sparseStateTrie); }
            catch (InvalidOperationException) { }
            _sparseStateTrie = null;
        }

        _sparseRootComputer?.Dispose();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    internal Action? OnWaitingForWarmups;

    private void WaitForOutstandingWarmups()
    {
        if (Volatile.Read(ref _outstandingWarmups) == 0) return;

        OnWaitingForWarmups?.Invoke();

        SpinWait spinWait = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _outstandingWarmups) != 0)
        {
            if (stopwatch.ElapsedMilliseconds > 1000)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"TrieWarmer outstanding jobs ({Volatile.Read(ref _outstandingWarmups)}) did not drain within 1s during scope dispose");
                return;
            }
            spinWait.SpinOnce();
        }
    }

    public Hash256 RootHash => _sparseComputedRoot ?? _stateTree.RootHash;

    public void UpdateRootHash()
    {
        if (_sparseRootComputer is not null)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                _sparseComputedRoot = _sparseRootComputer.ComputeStateRoot();
                sw.Stop();

                if (!_sparseIsAuthoritative)
                {
                    // Validation mode: run Patricia too and compare
                    _stateTree.UpdateRootHash();

                    if (_sparseComputedRoot == _stateTree.RootHash)
                    {
                        int matchCount = Interlocked.Increment(ref _sparseMatchCount);
                        int consecutive = Interlocked.Increment(ref _consecutiveMatches);
                        if (matchCount % 100 == 1 || matchCount <= 5)
                        {
                            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                            if (logger.IsInfo) logger.Info(
                                $"SPARSE ROOT MATCH #{matchCount}! Root={_sparseComputedRoot}, " +
                                $"SparseTime={sw.ElapsedMilliseconds}ms, Consecutive={consecutive}, " +
                                $"Totals: match={matchCount} mismatch={_sparseMismatchCount} fail={_sparseFailCount}");
                        }
                    }
                    else
                    {
                        int mismatchCount = Interlocked.Increment(ref _sparseMismatchCount);
                        Interlocked.Exchange(ref _consecutiveMatches, 0);
                        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                        if (logger.IsWarn) logger.Warn(
                            $"SPARSE ROOT MISMATCH #{mismatchCount}! Patricia={_stateTree.RootHash}, Sparse={_sparseComputedRoot}, " +
                            $"SparseTime={sw.ElapsedMilliseconds}ms, Accounts={_sparseRootComputer.AccountChangeCount}, " +
                            $"ProofNodes={_sparseRootComputer.LastProofNodeCount}, PrevRoot={_sparseRootComputer.PreviousRoot}. " +
                            $"Falling back to Patricia.");

                        if (mismatchCount <= 3)
                            DiagnoseMismatch(logger, _sparseRootComputer);

                        _sparseComputedRoot = null;
                    }
                }
                else
                {
                    // Authoritative mode: sparse root is the answer, skip Patricia UpdateRootHash
                    int matchCount = Interlocked.Increment(ref _sparseMatchCount);
                    Interlocked.Increment(ref _consecutiveMatches);
                    if (matchCount % 500 == 1)
                    {
                        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                        if (logger.IsInfo) logger.Info(
                            $"SPARSE ROOT (authoritative) #{matchCount}: Root={_sparseComputedRoot}, " +
                            $"SparseTime={sw.ElapsedMilliseconds}ms");
                    }
                }
            }
            catch (Exception ex)
            {
                int failCount = Interlocked.Increment(ref _sparseFailCount);
                Interlocked.Exchange(ref _consecutiveMatches, 0);
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn(
                    $"SPARSE ROOT FAIL #{failCount}! Exception={ex.GetType().Name}: {ex.Message}, " +
                    $"Totals: match={_sparseMatchCount} mismatch={_sparseMismatchCount} fail={failCount}");
                _sparseComputedRoot = null;
                _sparseIsAuthoritative = false;

                // In SkipPatricia (M3) mode, Patricia BulkSet was never called, so falling back
                // to Patricia would produce a stale/empty root. Propagate the exception.
                if (_configuration.SparseTrieSkipPatricia)
                    throw;

                // Fallback: run Patricia
                _stateTree.UpdateRootHash();
            }
        }
        else
        {
            _stateTree.UpdateRootHash();
        }
    }

    /// <summary>Returns the total byte length of the RLP-encoded item starting at <paramref name="offset"/>.</summary>
    private static int RlpItemLength(ReadOnlySpan<byte> rlp, int offset)
    {
        byte first = rlp[offset];
        if (first < 0x80) return 1;
        if (first < 0xb8) return 1 + (first - 0x80);
        if (first < 0xc0)
        {
            int lenOfLen = first - 0xb7;
            int len = 0;
            for (int i = 0; i < lenOfLen; i++) len = (len << 8) | rlp[offset + 1 + i];
            return 1 + lenOfLen + len;
        }
        if (first < 0xf8) return 1 + (first - 0xc0);
        {
            int lenOfLen = first - 0xf7;
            int len = 0;
            for (int i = 0; i < lenOfLen; i++) len = (len << 8) | rlp[offset + 1 + i];
            return 1 + lenOfLen + len;
        }
    }

    /// <summary>
    /// Walks down sparse and Patricia tries in parallel, comparing Branch RLPs at each level
    /// until divergence reduces to a single child path or hits a leaf.
    /// </summary>
    private void DrillDivergence(ILogger logger, SparseSubtrie sub, int sparseNodeIdx, TrieNode? patriciaNode, TreePath path, int depth)
    {
        if (depth > 8 || patriciaNode is null) return;

        SparseTrieNode sparseNode = sub.NodeAt(sparseNodeIdx);

        if (!sparseNode.IsBranch())
        {
            logger.Warn($"  DIAG[d{depth}] path={path} sparse kind={sparseNode.Kind}, patricia NodeType={patriciaNode.NodeType}, sparse.shortKey.len={sparseNode.ShortKey?.Length ?? -1}");
            return;
        }

        if (patriciaNode.NodeType != NodeType.Branch)
        {
            logger.Warn($"  DIAG[d{depth}] path={path} STRUCTURAL: sparse=Branch, patricia={patriciaNode.NodeType} — sparse over-revealed");
            return;
        }

        // Both are branches — diff their RLPs
        if (sparseNode.FullRlp is null || patriciaNode.FullRlp.Length == 0) return;
        ReadOnlySpan<byte> sFull = sparseNode.FullRlp;
        ReadOnlySpan<byte> pFull = patriciaNode.FullRlp.AsSpan();

        int diff = -1;
        int minLen = Math.Min(sFull.Length, pFull.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (sFull[i] != pFull[i]) { diff = i; break; }
        }
        if (diff < 0 && sFull.Length == pFull.Length) return; // identical
        logger.Warn($"  DIAG[d{depth}] path={path} sparseLen={sFull.Length} patLen={pFull.Length} firstDiff={diff}");

        if (diff < 3)
        {
            logger.Warn($"  DIAG[d{depth}] header-level diff — dumping full hex");
            logger.Warn($"  DIAG[d{depth}] sparseFull={Convert.ToHexString(sFull[..Math.Min(sFull.Length, 600)])}");
            logger.Warn($"  DIAG[d{depth}] patFull   ={Convert.ToHexString(pFull[..Math.Min(pFull.Length, 600)])}");
            // Show sparse's StateMask and child entries for direct inspection
            logger.Warn($"  DIAG[d{depth}] sparse stateMask={sparseNode.StateMask}, blindedMask={sparseNode.BlindedMask}, childCount={sparseNode.ChildCount()}");
            return;
        }

        // For branches NOT all-hash-children (RLP != 532), child slot sizes vary.
        // Parse RLP slot-by-slot to find the actual diverging child index.
        int childIdx = -1;
        try
        {
            int sCur = 3, pCur = 3;
            for (int n = 0; n < 16; n++)
            {
                int sItemLen = RlpItemLength(sFull, sCur);
                int pItemLen = RlpItemLength(pFull, pCur);
                if (diff >= sCur && diff < sCur + sItemLen) { childIdx = n; break; }
                if (sItemLen != pItemLen || !sFull.Slice(sCur, sItemLen).SequenceEqual(pFull.Slice(pCur, pItemLen)))
                {
                    childIdx = n; break;
                }
                sCur += sItemLen;
                pCur += pItemLen;
            }
        }
        catch (Exception parseEx)
        {
            logger.Warn($"  DIAG[d{depth}] slot parse failed: {parseEx.GetType().Name}");
        }
        if (childIdx < 0)
        {
            logger.Warn($"  DIAG[d{depth}] could not identify diverging slot, dumping full hex");
            logger.Warn($"  DIAG[d{depth}] sparseFull={Convert.ToHexString(sFull[..Math.Min(sFull.Length, 600)])}");
            logger.Warn($"  DIAG[d{depth}] patFull   ={Convert.ToHexString(pFull[..Math.Min(pFull.Length, 600)])}");
            return;
        }
        logger.Warn($"  DIAG[d{depth}] divergence in child #{childIdx}");

        if (childIdx > 15) return;

        try
        {
            TreePath nextPath = path.Append(childIdx);

            if (!sparseNode.StateMask.IsBitSet(childIdx))
            {
                logger.Warn($"  DIAG[d{depth}] sparse has NO child at #{childIdx} but Patricia might — structural mismatch");
                return;
            }

            int denseIdx = sparseNode.DenseChildIndex(childIdx);
            SparseChildEntry entry = sub.ChildAt(denseIdx);

            TrieNode? patChild = patriciaNode.GetChild(_stateTree.TrieStore, ref Unsafe.AsRef(in nextPath), childIdx);

            if (entry.IsBlinded)
            {
                logger.Warn($"  DIAG[d{depth}] sparse child #{childIdx} BLINDED (proof did not reveal). patricia keccak={patChild?.Keccak}");
                return;
            }

            SparseTrieNode childNode = sub.NodeAt(entry.ArenaIndex);
            Hash256? sparseKeccak = childNode.FullRlp is not null ? Keccak.Compute(childNode.FullRlp) : null;
            logger.Warn($"  DIAG[d{depth}] child #{childIdx} sparse kind={childNode.Kind} keccak={sparseKeccak} patricia keccak={patChild?.Keccak}");
            DrillDivergence(logger, sub, entry.ArenaIndex, patChild, nextPath, depth + 1);
        }
        catch (Exception ex)
        {
            logger.Warn($"  DIAG[d{depth}] drill exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DiagnoseMismatch(ILogger logger, SparseRootComputer computer)
    {
        try
        {
            Hash256 prevRoot = computer.PreviousRoot;
            ParentStateTrieNodeReader reader = new(_snapshotBundle);

            byte[] rootRlp = reader.LoadStateRlp(TreePath.Empty, prevRoot);
            Hash256 rootRlpHash = Keccak.Compute(rootRlp);
            logger.Warn($"  DIAG: prevRoot={prevRoot}, rootRlp.Length={rootRlp.Length}, keccak(rootRlp)={rootRlpHash}, match={rootRlpHash == prevRoot}");
            logger.Warn($"  DIAG: rootRlp[0..min(64,len)]={Convert.ToHexString(rootRlp.AsSpan(0, Math.Min(64, rootRlp.Length)))}");

            SparseSubtrie subtrie = computer.Trie.AccountTrie.Subtrie;
            if (subtrie.Root >= 0)
            {
                SparseTrieNode rootNode = subtrie.NodeAt(subtrie.Root);
                logger.Warn($"  DIAG: sparseRoot kind={rootNode.Kind}, state={rootNode.State}, " +
                    $"shortKey={rootNode.ShortKey?.Length ?? -1}, stateMask={rootNode.StateMask}, " +
                    $"blindedMask={rootNode.BlindedMask}, childCount={rootNode.ChildCount()}");

                if (rootNode.CachedRlp.IsNull)
                    logger.Warn("  DIAG: sparseRoot CachedRlp is NULL (not computed?)");
                else
                {
                    ReadOnlySpan<byte> cachedSpan = rootNode.CachedRlp.AsSpan();
                    logger.Warn($"  DIAG: sparseRoot CachedRlp.Length={cachedSpan.Length}");
                    logger.Warn($"  DIAG: sparseRoot CachedRlp[0..min(64,len)]={Convert.ToHexString(cachedSpan[..Math.Min(64, cachedSpan.Length)])}");
                    Hash256 sparseCachedHash = Keccak.Compute(cachedSpan);
                    logger.Warn($"  DIAG: keccak(sparseCachedRlp)={sparseCachedHash}");
                }

                if (rootNode.FullRlp is not null)
                {
                    logger.Warn($"  DIAG: sparseRoot FullRlp.Length={rootNode.FullRlp.Length}");
                    if (rootNode.FullRlp.Length != rootNode.CachedRlp.Length)
                        logger.Warn($"  DIAG: WARNING FullRlp.Length != CachedRlp.Length!");
                }

                int blindedCount = rootNode.BlindedMask.CountBits();
                int totalChildren = rootNode.StateMask.CountBits();
                logger.Warn($"  DIAG: root has {totalChildren} children, {blindedCount} still blinded");
            }

            DecodedMultiProof diagnosticProof = MultiProofReader.ReadAccountProofs(
                reader, prevRoot, [Keccak.Zero]);
            logger.Warn($"  DIAG: test proof read returned {diagnosticProof.AccountNodes.Count} nodes (no exception = reader works)");

            // Dump Patricia root RLP for child-by-child comparison with sparse
            try
            {
                TrieNode? patriciaRootRef = _stateTree.RootRef;
                if (patriciaRootRef is not null && patriciaRootRef.FullRlp.Length > 0)
                {
                    ReadOnlySpan<byte> patSpan = patriciaRootRef.FullRlp.AsSpan();
                    logger.Warn($"  DIAG: PATRICIA root NodeType={patriciaRootRef.NodeType}, FullRlp.Length={patSpan.Length}");
                    Hash256 patriciaRootHash = Keccak.Compute(patSpan);
                    logger.Warn($"  DIAG: PATRICIA keccak(rootRlp)={patriciaRootHash}, expected={_stateTree.RootHash}, match={patriciaRootHash == _stateTree.RootHash}");

                    // Find first byte-divergence between Patricia and sparse RLPs
                    SparseSubtrie sub = computer.Trie.AccountTrie.Subtrie;
                    if (sub.Root >= 0)
                    {
                        SparseTrieNode sparseRoot = sub.NodeAt(sub.Root);
                        if (!sparseRoot.CachedRlp.IsNull)
                        {
                            ReadOnlySpan<byte> sparseSpan = sparseRoot.CachedRlp.AsSpan();
                            int diffAt = -1;
                            int minLen = Math.Min(patSpan.Length, sparseSpan.Length);
                            for (int i = 0; i < minLen; i++)
                            {
                                if (patSpan[i] != sparseSpan[i]) { diffAt = i; break; }
                            }
                            logger.Warn($"  DIAG: RLP first byte diff at index={diffAt}, patriciaLen={patSpan.Length}, sparseLen={sparseSpan.Length}");
                            if (diffAt >= 0)
                            {
                                // Print 32 bytes around the diff
                                int start = Math.Max(0, diffAt - 4);
                                int end = Math.Min(patSpan.Length, diffAt + 32);
                                logger.Warn($"  DIAG: patricia[{start}..{end}]={Convert.ToHexString(patSpan[start..end])}");
                                logger.Warn($"  DIAG: sparse  [{start}..{end}]={Convert.ToHexString(sparseSpan[start..Math.Min(sparseSpan.Length, end)])}");

                                // Determine which child slot the diff is in
                                // Branch RLP: 3-byte header + 16 × 33-byte child slots + 1-byte 0x80
                                // Child N starts at offset 3 + N*33 (if all children are A0 + 32-byte hashes)
                                if (diffAt >= 3 && patSpan.Length >= 532)
                                {
                                    int relativeOffset = diffAt - 3;
                                    int childIdx = relativeOffset / 33;
                                    int byteInChild = relativeOffset % 33;
                                    logger.Warn($"  DIAG: divergence in child #{childIdx}, byte {byteInChild} of child slot (0=tag, 1-32=hash bytes)");

                                    // Drill into sparse trie's child at nibble childIdx
                                    try
                                    {
                                        SparseTrieNode rootNode = sub.NodeAt(sub.Root);
                                        if (rootNode.IsBranch() && rootNode.StateMask.IsBitSet(childIdx))
                                        {
                                            int denseIdx = rootNode.DenseChildIndex(childIdx);
                                            SparseChildEntry entry = sub.ChildAt(denseIdx);
                                            if (entry.IsBlinded)
                                            {
                                                logger.Warn($"  DIAG: sparse child #{childIdx} is BLINDED (kept from proof). RlpNode={entry.BlindedRlp}");
                                            }
                                            else
                                            {
                                                SparseTrieNode childNode = sub.NodeAt(entry.ArenaIndex);
                                                logger.Warn($"  DIAG: sparse child #{childIdx} kind={childNode.Kind}, state={childNode.State}, shortKey.len={childNode.ShortKey?.Length ?? -1}, stateMask={childNode.StateMask}, childCount={childNode.ChildCount()}");
                                                if (!childNode.CachedRlp.IsNull)
                                                {
                                                    ReadOnlySpan<byte> ccr = childNode.CachedRlp.AsSpan();
                                                    logger.Warn($"  DIAG: sparse child #{childIdx} CachedRlp.Length={ccr.Length}");
                                                    if (ccr.Length >= 32) logger.Warn($"  DIAG: sparse child #{childIdx} keccak={Keccak.Compute(ccr)}");
                                                }
                                                if (childNode.FullRlp is not null)
                                                {
                                                    logger.Warn($"  DIAG: sparse child #{childIdx} FullRlp.Length={childNode.FullRlp.Length}, keccak={Keccak.Compute(childNode.FullRlp)}");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception scEx)
                                    {
                                        logger.Warn($"  DIAG: sparse child drill failed: {scEx.GetType().Name}: {scEx.Message}");
                                    }

                                    // Walk Patricia tree's RootRef to child #childIdx
                                    try
                                    {
                                        TreePath emptyPath = TreePath.Empty;
                                        TrieNode? patChild = patriciaRootRef?.GetChild(_stateTree.TrieStore, ref emptyPath, childIdx);
                                        if (patChild is not null)
                                        {
                                            logger.Warn($"  DIAG: patricia child #{childIdx} NodeType={patChild.NodeType}, Keccak={patChild.Keccak}, FullRlp.Length={patChild.FullRlp.Length}");

                                            // Recurse down to find where the trees actually differ
                                            SparseTrieNode rootNode2 = sub.NodeAt(sub.Root);
                                            if (rootNode2.IsBranch() && rootNode2.StateMask.IsBitSet(childIdx))
                                            {
                                                int denseIdx2 = rootNode2.DenseChildIndex(childIdx);
                                                SparseChildEntry entry2 = sub.ChildAt(denseIdx2);
                                                if (!entry2.IsBlinded)
                                                {
                                                    TreePath childPath = TreePath.Empty.Append(childIdx);
                                                    DrillDivergence(logger, sub, entry2.ArenaIndex, patChild, childPath, 1);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            logger.Warn($"  DIAG: patricia child #{childIdx} = null");
                                        }
                                    }
                                    catch (Exception ppEx)
                                    {
                                        logger.Warn($"  DIAG: patricia child walk failed: {ppEx.GetType().Name}: {ppEx.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    logger.Warn($"  DIAG: PATRICIA root inspection: RootRef={(patriciaRootRef is null ? "null" : "non-null")}, FullRlp.Length={patriciaRootRef?.FullRlp.Length ?? -1}");
                }
            }
            catch (Exception patEx)
            {
                logger.Warn($"  DIAG: PATRICIA root inspection failed: {patEx.GetType().Name}: {patEx.Message}");
            }

            // Re-do from scratch with a FRESH trie to confirm cross-block isn't the issue
            if (computer.AccountChangeCount > 0 && computer.AccountChangeCount < 10000)
            {
                try
                {
                    ParentStateTrieNodeReader freshReader = new(_snapshotBundle);
                    using SparseRootComputer freshComputer = new(freshReader, prevRoot);
                    freshComputer.SetAccountChanges(computer.LastAccountChanges!);
                    Hash256 freshRoot = freshComputer.ComputeStateRoot();
                    logger.Warn($"  DIAG: FRESH sparse root={freshRoot}, matches_patricia={freshRoot == _stateTree.RootHash}");
                }
                catch (Exception freshEx)
                {
                    logger.Warn($"  DIAG: fresh recompute failed: {freshEx.GetType().Name}: {freshEx.Message}");
                }

                // Dump account changes for replay reproduction
                Dictionary<Hash256, LeafUpdate> changes = computer.LastAccountChanges!;
                int dumped = 0;
                int deletions = 0;
                int totalRlpBytes = 0;
                foreach (KeyValuePair<Hash256, LeafUpdate> kv in changes)
                {
                    if (kv.Value.IsDelete) deletions++;
                    else if (kv.Value.Value is not null) totalRlpBytes += kv.Value.Value.Length;
                    if (dumped++ < 5)
                    {
                        if (kv.Value.IsDelete)
                            logger.Warn($"  DIAG: change[{dumped - 1}] keccak={kv.Key} DELETED");
                        else
                            logger.Warn($"  DIAG: change[{dumped - 1}] keccak={kv.Key} rlp.Length={kv.Value.Value!.Length} rlp[0..min(64,len)]={Convert.ToHexString(kv.Value.Value.AsSpan(0, Math.Min(64, kv.Value.Value.Length)))}");
                    }
                }
                logger.Warn($"  DIAG: total changes={changes.Count}, deletions={deletions}, total rlp bytes={totalRlpBytes}");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"  DIAG: exception during diagnosis: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address);

        HintGet(address, account);

        if (_configuration.VerifyWithTrie)
        {
            Account? accTrie = _stateTree.Get(address);
            if (accTrie != account)
            {
                throw new TrieException($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
            }
        }

        return account;
    }

    public void HintGet(Address address, Account? account)
    {
        _snapshotBundle.SetAccount(address, account);
        if (_snapshotBundle.ShouldQueuePrewarm(address))
        {
            if (_warmer.PushAddressJob(this, address, _hintSequenceId))
                Interlocked.Increment(ref _outstandingWarmups);
        }
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId;

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;
            _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => Interlocked.Decrement(ref _outstandingWarmups);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists) return storage!;

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        storage = new FlatStorageTree(
            this,
            _warmer,
            _snapshotBundle,
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
        new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        bool usedSparseCommit = false;
        if (_sparseIsAuthoritative && _sparseComputedRoot is not null && _sparseRootComputer is not null)
        {
            try
            {
                SparseStateTrie trie = _sparseRootComputer.Trie;
                SparseTrieSnapshotCommitter.CommitAccountTrie(trie.AccountTrie.Subtrie, _snapshotBundle);
                usedSparseCommit = true;
            }
            catch (Exception ex)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"SparseTrieSnapshotCommitter failed: {ex.Message}. Falling back to Patricia Commit.");
                Interlocked.Exchange(ref _consecutiveMatches, 0);
            }
        }

        if (!usedSparseCommit)
        {
            _stateTree.Commit();
        }

        _storages.Clear();

        StateId newStateId = new(blockNumber, RootHash);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        (Snapshot? newSnapshot, TransientResource? cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

        if (shouldAddSnapshot)
        {
            if (_currentStateId != newStateId)
            {
                _commitTarget.AddSnapshot(newSnapshot!, cachedResource!);
            }
            else
            {
                newSnapshot?.Dispose();
                cachedResource?.Dispose();
            }
        }

        _currentStateId = newStateId;

        // Create fresh SparseRootComputer for next block.
        // Cross-block trie reuse (PreservedSparseTrie) is deferred to M3 — the current
        // RevealNodes logic skips already-revealed nodes, so stale structure from prior
        // blocks causes wrong roots. For M2, a fresh trie per block is correct.
        if (_sparseRootComputer is not null)
        {
            Hash256 newRoot = newStateId.StateRoot.ToCommitment();

            if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
            {
                _preservedSparseTrie.StoreCleared(_sparseStateTrie);
                _sparseStateTrie = null;
            }

            _sparseRootComputer.Dispose();
            ParentStateTrieNodeReader proofReader = new(_snapshotBundle);
            _sparseRootComputer = new SparseRootComputer(proofReader, newRoot);

            _sparseComputedRoot = null;
            _sparseIsAuthoritative = !_configuration.SparseTrieVerificationMode
                && Volatile.Read(ref _consecutiveMatches) >= 10;
        }

        _pausePrewarmer = false;
    }

    private class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);

            if (account is null)
            {
                scope.CreateStorageTreeImpl(key).SelfDestruct();
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            try
            {
                while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    if (!_dirtyAccounts.TryGetValue(key, out Account? account)) account = scope.Get(key);
                    if (account is null)
                    {
                        if (storageRoot == Keccak.EmptyTreeHash) continue;
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(key.Value, 0);
                        wb.Clear();
                        continue;
                    }
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    Address address = key.Value;
                    OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                    if (logger.IsTrace) Trace(address, storageRoot, account);
                }

                // Feed Patricia BulkSet UNLESS authoritative + SkipPatricia configured (M3 mode).
                // In M3 mode, sparse is the only computation; Patricia BulkSet is skipped for perf.
                bool skipPatriciaBulkSet = scope._configuration.SparseTrieSkipPatricia
                    && scope._sparseIsAuthoritative
                    && !scope._configuration.SparseTrieVerificationMode;
                if (!skipPatriciaBulkSet)
                {
                    using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                }

                // Feed account changes to SparseRootComputer
                if (scope._sparseRootComputer is not null)
                {
                    Dictionary<Hash256, LeafUpdate> sparseAccountUpdates = new(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        Hash256 hashedAddr = Keccak.Compute(kv.Key.Value.Bytes);
                        if (kv.Value is null)
                        {
                            sparseAccountUpdates[hashedAddr] = LeafUpdate.Deleted();
                        }
                        else
                        {
                            byte[] rlp = Nethermind.Serialization.Rlp.AccountDecoder.Instance.Encode(kv.Value).Bytes;
                            sparseAccountUpdates[hashedAddr] = LeafUpdate.Changed(rlp);
                        }
                    }
                    scope._sparseRootComputer.SetAccountChanges(sparseAccountUpdates);
                }
            }
            finally
            {
                _dirtyAccounts.Clear();

                Interlocked.Increment(ref scope._hintSequenceId);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account) =>
                logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
        }
    }
}
