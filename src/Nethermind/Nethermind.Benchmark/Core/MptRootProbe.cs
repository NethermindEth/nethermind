// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Device-resident MPT flow evaluation: root-share / offloadable-fraction / transfer-budget measurement
/// (MEASUREMENT ONLY, benchmark-local, invoked via <c>--mpt-root-probe</c>). Answers three questions about the
/// STANDARD (non-BAL) commit path over realistic block shapes:
/// <list type="number">
/// <item>ROOT SHARE: what share of the commit-path time (write-apply + root-compute + commit persist) is root
/// computation (state + storage tries UpdateRootHash + the per-node encode/hash inside Commit)?</item>
/// <item>OFFLOADABLE FRACTION: within root computation, what fraction is per-node encode+hash+splice
/// (device-movable) vs traversal/collection/bookkeeping (CPU-bound), timed at the BatchedTrieCommitter seam?</item>
/// <item>TRANSFER BUDGET: would-be D2H (keccak+RLP per dirty node) and H2D (leaf values + structure + retained
/// parent RLPs) volumes vs a measured PCIe-order bandwidth assumption and the committed ~300us/dispatch device
/// floor; a net-win bound for a single-dispatch device-resident flow.</item>
/// </list>
/// </summary>
/// <remarks>
/// NOT a BenchmarkDotNet benchmark - it uses Stopwatch across whole-scenario passes because the interesting
/// quantities (phase splits within one commit, wave-step encode/hash/collect breakdown) are per-scenario
/// accumulations, not per-iteration-isolated microbenchmarks. Numbers are coarse (a handful of clean passes,
/// median reported) and reported as such.
/// <para>
/// Modelling choice (justified): the harness builds StateTree + one StorageTree per storage-writing account
/// directly over TestTrieStoreFactory-built raw stores rather than a full WorldState. WorldState adds
/// execution-side journaling, the shared PreBlockCaches, and the two-level trie store, all orthogonal to the
/// commit-path hashing/encode work being measured; the wave shape and encode/hash load at commit time are
/// identical either way. This mirrors <see cref="MergedWaveBenchmarks"/>, which builds PatriciaTree instances
/// directly for the same reason. The authoritative BLOCK-LEVEL root-computation share (which also divides by
/// EVM/execution time this harness excludes) needs an expb/dotTrace payload run - see AGENTS.md
/// dottrace-report.sh - and will be a SMALLER share than the commit-path-only share reported here.
/// </para>
/// </remarks>
public static class MptRootProbe
{
    private const int Passes = 9;                 // odd -> clean median
    private static readonly int[] Shapes = [100, 400, 1600];
    private const int BalanceOnlyMultiplier = 5;  // ~5x balance/nonce-only accounts per storage-writer

    // Transfer-budget assumptions (stated plainly in output).
    private const double PcieEffectiveGiBs = 20.0;         // ~20 GB/s effective PCIe-order bandwidth
    private const double DispatchFloorUs = 300.0;          // committed ~300us/dispatch GPU floor (single dispatch)
    private const int StructureBytesPerNode = 40;          // H2D structure estimate ~40B/node

    public static void Run()
    {
        Console.WriteLine("=== Device-resident MPT flow evaluation: root-share / offloadable-fraction / transfer-budget (manual) ===");
        Console.WriteLine($"Passes per shape: {Passes} (median reported). BalanceOnly multiplier: {BalanceOnlyMultiplier}x storage-writers.");
        Console.WriteLine($"Slot distribution: ~85% 1-4 slots, ~12% 5-30, ~3% 100-400 (seed 42), matching MergedWaveBenchmarks.");
        Console.WriteLine($"Assumptions: PCIe {PcieEffectiveGiBs:F0} GB/s effective, dispatch floor {DispatchFloorUs:F0}us, structure {StructureBytesPerNode}B/node H2D.");
        Console.WriteLine();

        List<ShapeResult> results = [];
        foreach (int shape in Shapes)
        {
            results.Add(Measure(shape));
        }

        PrintTable(results);
    }

    private readonly struct ShapeResult
    {
        public int Accounts { get; init; }
        public int TotalSlots { get; init; }
        public double WriteApplyMs { get; init; }
        public double RootComputeMs { get; init; }     // UpdateRootHash recursive (state + all storage), production standard path
        public double CommitPersistMs { get; init; }   // full Commit MINUS the encode/hash it duplicates == persist/IO share (see note)
        public double CommitFullMs { get; init; }       // full Commit(encode+hash+persist)
        // Offloadable-fraction wave-seam split (of the batched root computation):
        public double CollectMs { get; init; }
        public double EncodeMs { get; init; }
        public double HashMs { get; init; }
        // Transfer-budget volumes:
        public long DirtyNodeCount { get; init; }
        public long D2HBytes { get; init; }              // sum(32 keccak + FullRlp length) over dirty nodes
        public long H2DBytes { get; init; }              // leaf values + ~40B/node structure + retained parent RLPs
        public long LeafValueBytes { get; init; }
        public long RetainedParentRlpBytes { get; init; }
    }

    private static ShapeResult Measure(int accounts)
    {
        int[] slotCounts = BuildSlotCounts(accounts, out int totalSlots);

        // --- Phase timings (median over passes; each pass on a FRESH scenario so no warm-state contamination). ---
        GcQuiesce();
        double writeApplyMs = Median(accounts, () => TimeWriteApply(accounts, slotCounts));
        GcQuiesce();
        double rootComputeMs = Median(accounts, () => TimeRootCompute(accounts, slotCounts));
        GcQuiesce();
        double commitFullMs = Median(accounts, () => TimeCommitFull(accounts, slotCounts));
        GcQuiesce();

        // --- Offloadable-fraction wave-seam split (collect / encode / hash) over storage tries + the state trie. ---
        (double collectMs, double encodeMs, double hashMs, long dirtyNodes, long d2hBytes, long leafValueBytes, long retainedParentBytes) =
            MedianWaveSplit(accounts, slotCounts);

        long structureBytes = dirtyNodes * StructureBytesPerNode;
        long h2dBytes = leafValueBytes + structureBytes + retainedParentBytes;

        // CommitPersist proxy: full Commit includes the recursive encode+hash (same work as RootCompute) PLUS DB
        // persistence. Subtracting RootCompute isolates the persist/IO share. Clamp at 0 (measurement noise).
        double commitPersistMs = Math.Max(0.0, commitFullMs - rootComputeMs);

        return new ShapeResult
        {
            Accounts = accounts,
            TotalSlots = totalSlots,
            WriteApplyMs = writeApplyMs,
            RootComputeMs = rootComputeMs,
            CommitFullMs = commitFullMs,
            CommitPersistMs = commitPersistMs,
            CollectMs = collectMs,
            EncodeMs = encodeMs,
            HashMs = hashMs,
            DirtyNodeCount = dirtyNodes,
            D2HBytes = d2hBytes,
            H2DBytes = h2dBytes,
            LeafValueBytes = leafValueBytes,
            RetainedParentRlpBytes = retainedParentBytes,
        };
    }

    // ---------------------------------------------------------------------------------------------------------
    // Scenario construction. Builds a state tree + one storage tree per storage-writing account, plus
    // BalanceOnlyMultiplier x balance/nonce-only accounts (state-tree-only writes, no storage).
    // ---------------------------------------------------------------------------------------------------------

    private sealed class Scenario
    {
        public required StateTree StateTree { get; init; }
        public required List<StorageTree> StorageTrees { get; init; }
        public required List<Address> StorageAddresses { get; init; }
        public required List<UInt256[]> SlotKeys { get; init; }
        public required List<Address> BalanceOnlyAddresses { get; init; }
        public required TestRawTrieStore Store { get; init; }
    }

    private static Scenario BuildScenario(int accounts, int[] slotCounts)
    {
        TestRawTrieStore store = TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);
        StateTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);

        List<StorageTree> storageTrees = new(accounts);
        List<Address> storageAddresses = new(accounts);
        List<UInt256[]> slotKeys = new(accounts);

        for (int a = 0; a < accounts; a++)
        {
            Address addr = AddressFromIndex(a);
            StorageTree storageTree = new(store.GetTrieStore(addr), LimboLogs.Instance);
            int slots = slotCounts[a];
            UInt256[] keys = new UInt256[slots];
            for (int s = 0; s < slots; s++)
            {
                keys[s] = (UInt256)((ulong)(a * 100_000 + s + 1));
            }

            storageTrees.Add(storageTree);
            storageAddresses.Add(addr);
            slotKeys.Add(keys);
        }

        int balanceOnly = accounts * BalanceOnlyMultiplier;
        List<Address> balanceOnlyAddresses = new(balanceOnly);
        for (int b = 0; b < balanceOnly; b++)
        {
            balanceOnlyAddresses.Add(AddressFromIndex(1_000_000 + b));
        }

        return new Scenario
        {
            StateTree = stateTree,
            StorageTrees = storageTrees,
            StorageAddresses = storageAddresses,
            SlotKeys = slotKeys,
            BalanceOnlyAddresses = balanceOnlyAddresses,
            Store = store,
        };
    }

    /// <summary>Applies all writes (slot Sets incl. keccak+restructure, account Sets) WITHOUT computing roots.</summary>
    private static void ApplyWrites(Scenario sc)
    {
        // Storage writes + storage-writing accounts.
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            UInt256[] keys = sc.SlotKeys[a];
            for (int s = 0; s < keys.Length; s++)
            {
                byte[] value = new byte[32];
                BitConverter.TryWriteBytes(value, (ulong)(s + 1));
                st.Set(keys[s], value);
            }
        }

        // Balance/nonce-only accounts (state tree only; storageRoot stays empty).
        for (int b = 0; b < sc.BalanceOnlyAddresses.Count; b++)
        {
            sc.StateTree.Set(sc.BalanceOnlyAddresses[b], new Account((ulong)(b + 1), (UInt256)((ulong)(b + 1000))));
        }
    }

    private static void ComputeStorageRootsAndComposeAccounts(Scenario sc, bool canBeParallel)
    {
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            st.UpdateRootHash(canBeParallel);
            Account acct = new((ulong)(a + 1), (UInt256)((ulong)(a + 1)), st.RootHash, Keccak.OfAnEmptyString);
            sc.StateTree.Set(sc.StorageAddresses[a], acct);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // (a) write application only.
    // ---------------------------------------------------------------------------------------------------------
    private static double TimeWriteApply(int accounts, int[] slotCounts)
    {
        Scenario sc = BuildScenario(accounts, slotCounts);
        long t0 = Stopwatch.GetTimestamp();
        ApplyWrites(sc);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // ---------------------------------------------------------------------------------------------------------
    // (b) root computation: production standard recursive path (UpdateRootHash), state + all storage tries.
    // Writes applied first (untimed); we time only the storage-root + state-root recursive computation.
    // ---------------------------------------------------------------------------------------------------------
    private static double TimeRootCompute(int accounts, int[] slotCounts)
    {
        Scenario sc = BuildScenario(accounts, slotCounts);
        // Apply storage slot writes (untimed).
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            UInt256[] keys = sc.SlotKeys[a];
            for (int s = 0; s < keys.Length; s++)
            {
                byte[] value = new byte[32];
                BitConverter.TryWriteBytes(value, (ulong)(s + 1));
                st.Set(keys[s], value);
            }
        }
        for (int b = 0; b < sc.BalanceOnlyAddresses.Count; b++)
        {
            sc.StateTree.Set(sc.BalanceOnlyAddresses[b], new Account((ulong)(b + 1), (UInt256)((ulong)(b + 1000))));
        }

        long t0 = Stopwatch.GetTimestamp();
        // Storage roots (recursive, non-parallel = production standard per-tree path) + account composition + state root.
        ComputeStorageRootsAndComposeAccounts(sc, canBeParallel: false);
        sc.StateTree.UpdateRootHash(canBeParallel: false);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // ---------------------------------------------------------------------------------------------------------
    // (c) full Commit: encode + hash + persist to MemDb. Writes applied first (untimed).
    // Commit computes the root internally then persists dirty nodes.
    // ---------------------------------------------------------------------------------------------------------
    private static double TimeCommitFull(int accounts, int[] slotCounts)
    {
        Scenario sc = BuildScenario(accounts, slotCounts);
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            UInt256[] keys = sc.SlotKeys[a];
            for (int s = 0; s < keys.Length; s++)
            {
                byte[] value = new byte[32];
                BitConverter.TryWriteBytes(value, (ulong)(s + 1));
                st.Set(keys[s], value);
            }
        }
        for (int b = 0; b < sc.BalanceOnlyAddresses.Count; b++)
        {
            sc.StateTree.Set(sc.BalanceOnlyAddresses[b], new Account((ulong)(b + 1), (UInt256)((ulong)(b + 1000))));
        }

        long t0 = Stopwatch.GetTimestamp();
        // Storage trees: compose accounts requires storage roots, so commit storage first (each Commit computes+persists).
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            st.Commit();
            Account acct = new((ulong)(a + 1), (UInt256)((ulong)(a + 1)), st.RootHash, Keccak.OfAnEmptyString);
            sc.StateTree.Set(sc.StorageAddresses[a], acct);
        }
        sc.StateTree.Commit();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Offloadable-fraction seam split via BatchedTrieCommitter. Collect (CollectDirtyByDepth), Encode (EncodeLevel),
    // Hash (HashBatch) timed separately. A delegating hasher accumulates HashBatch time; the full batched wave time
    // minus the accumulated hash time minus a separately-timed collect-only pass gives the encode+splice residual.
    // COMMIT-PATH-WIDE: both the per-account storage tries AND the state trie (composed from every touched account -
    // storage writers with their computed storage roots plus the balance/nonce-only accounts) are waved and folded
    // into one set of totals, so the seam split matches the same scope as the commit-path root share.
    // ---------------------------------------------------------------------------------------------------------
    private sealed class TimingHasher(IKeccakBatchHasher inner) : IKeccakBatchHasher
    {
        private readonly System.Diagnostics.Stopwatch _sw = new();
        public double AccumulatedMs => _sw.Elapsed.TotalMilliseconds;
        public void HashBatch(ReadOnlySpan<byte> flat, ReadOnlySpan<int> offsets, Span<ValueHash256> outputs)
        {
            _sw.Start();
            inner.HashBatch(flat, offsets, outputs);
            _sw.Stop();
        }
    }

    private static (double collectMs, double encodeMs, double hashMs, long dirtyNodes, long d2hBytes, long leafValueBytes, long retainedParentBytes)
        MedianWaveSplit(int accounts, int[] slotCounts)
    {
        List<(double c, double e, double h)> splits = [];
        long dirtyNodes = 0, d2h = 0, leafVals = 0, retainedParent = 0;

        // Warmup (unmeasured): JIT the two waves, hasher, collect path.
        {
            Scenario w = BuildScenario(accounts, slotCounts);
            RunFullWave(w, new PerMessageKeccakBatchHasher());
        }

        for (int p = 0; p < Passes; p++)
        {
            // (1) full batched wave time over storage tries + the state trie, with a timing hasher accumulating pure
            // HashBatch time. Account composition (setting each account with its storage root into the state tree)
            // happens BETWEEN the two waves and is untimed here - it is write-application, not wave work.
            Scenario sc = BuildScenario(accounts, slotCounts);
            ApplySlotWrites(sc);
            TimingHasher th = new(new PerMessageKeccakBatchHasher());
            double fullMs = RunFullWave(sc, th);
            double hashMs = th.AccumulatedMs;

            // (2) collect-only time: rebuild fresh, apply the same writes + compose accounts (so the state trie
            // exists), then time just the CollectDirtyByDepth DFS over storage tries + the state trie.
            Scenario sc2 = BuildScenario(accounts, slotCounts);
            ApplySlotWrites(sc2);
            ComposeStateTree(sc2, canBeParallelStorageRoots: false);
            List<PatriciaTree> collectSet = [.. sc2.StorageTrees, sc2.StateTree];
            long tc0 = Stopwatch.GetTimestamp();
            long dn = CollectOnly(collectSet);
            double collectMs = Stopwatch.GetElapsedTime(tc0).TotalMilliseconds;

            // encode = full - hash - collect (collect measured on an equivalent fresh build; the wave also does
            // per-step level resolution + splice bookkeeping which lands in this residual, so "encode" here is
            // encode+splice+bookkeeping = the offloadable-minus-hash-minus-collect remainder). Clamp negatives.
            double encodeMs = Math.Max(0.0, fullMs - hashMs - collectMs);
            splits.Add((collectMs, encodeMs, hashMs));

            if (p == Passes / 2)
            {
                // Measure volumes once (deterministic across passes) after both waves populated Keccak/FullRlp.
                dirtyNodes = dn;
                (d2h, leafVals, retainedParent) = MeasureVolumes([.. sc.StorageTrees, sc.StateTree]);
            }
        }

        // Report the median of each phase independently (robust to per-phase jitter).
        double medC = MedianOf(splits, s => s.c);
        double medE = MedianOf(splits, s => s.e);
        double medH = MedianOf(splits, s => s.h);
        return (medC, medE, medH, dirtyNodes, d2h, leafVals, retainedParent);
    }

    /// <summary>
    /// Runs the storage-trie wave, composes accounts into the state trie, then runs the state-trie wave, returning
    /// the summed wall time of the two waves (account composition between them is NOT counted). The same
    /// <paramref name="hasher"/> backs both waves so its accumulated hash time spans state + storage.
    /// </summary>
    private static double RunFullWave(Scenario sc, IKeccakBatchHasher hasher)
    {
        long t0 = Stopwatch.GetTimestamp();
        BatchedTrieCommitter.UpdateRootHashesBatched(sc.StorageTrees, hasher);
        double storageWaveMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // Compose accounts (storage writers now carry roots; balance-only accounts too). Untimed: write-application.
        ComposeStateTreeFromComputedStorageRoots(sc);

        long t1 = Stopwatch.GetTimestamp();
        BatchedTrieCommitter.UpdateRootHashesBatched([sc.StateTree], hasher);
        double stateWaveMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;

        return storageWaveMs + stateWaveMs;
    }

    /// <summary>Applies the storage slot writes for every storage-writing account (no root computation).</summary>
    private static void ApplySlotWrites(Scenario sc)
    {
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            StorageTree st = sc.StorageTrees[a];
            UInt256[] keys = sc.SlotKeys[a];
            for (int s = 0; s < keys.Length; s++)
            {
                byte[] value = new byte[32];
                BitConverter.TryWriteBytes(value, (ulong)(s + 1));
                st.Set(keys[s], value);
            }
        }
    }

    /// <summary>Sets every touched account into the state trie using each storage tree's ALREADY-computed RootHash.</summary>
    private static void ComposeStateTreeFromComputedStorageRoots(Scenario sc)
    {
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            Account acct = new((ulong)(a + 1), (UInt256)((ulong)(a + 1)), sc.StorageTrees[a].RootHash, Keccak.OfAnEmptyString);
            sc.StateTree.Set(sc.StorageAddresses[a], acct);
        }
        for (int b = 0; b < sc.BalanceOnlyAddresses.Count; b++)
        {
            sc.StateTree.Set(sc.BalanceOnlyAddresses[b], new Account((ulong)(b + 1), (UInt256)((ulong)(b + 1000))));
        }
    }

    /// <summary>Computes storage roots (recursive) then composes accounts into the state trie; used for the collect-only pass.</summary>
    private static void ComposeStateTree(Scenario sc, bool canBeParallelStorageRoots)
    {
        for (int a = 0; a < sc.StorageTrees.Count; a++)
        {
            sc.StorageTrees[a].UpdateRootHash(canBeParallelStorageRoots);
        }
        ComposeStateTreeFromComputedStorageRoots(sc);
    }

    /// <summary>Replicates BatchedTrieCommitter.CollectDirtyByDepth's DFS to isolate collection cost; returns dirty-node count.</summary>
    private static long CollectOnly(IReadOnlyList<PatriciaTree> trees)
    {
        long count = 0;
        foreach (PatriciaTree tree in trees)
        {
            TrieNode? root = tree.RootRef;
            if (root is null || !root.IsDirty) continue;
            Stack<(TrieNode node, int depth)> stack = new();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                (TrieNode node, int depth) = stack.Pop();
                count++;
                int childCount = node.IsBranch ? TrieNode.BranchesCount : node.IsExtension ? 1 : 0;
                for (int i = 0; i < childCount; i++)
                {
                    if (node.TryGetDirtyChild(i, out TrieNode? child))
                    {
                        stack.Push((child!, depth + 1));
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// After a completed wave, walks the dirty DAG to sum: D2H = sum(32 + FullRlp.Length) over dirty nodes (the
    /// changed-node set Commit persists as keccak -> RLP), and H2D leaf-value bytes (the raw leaf payloads a device
    /// flow must be fed). Traverses via the same dirty-child descent; each node visited once.
    /// </summary>
    /// <remarks>
    /// LIMITATION: retained-parent RLP (parents NOT recomputed, whose child refs are spliced verbatim) is 0 here
    /// because these synthetic tries are built fresh in one shot, so every node is dirty and recomputed - there are
    /// no surviving pre-mutation parents to retain. On a real incremental block commit the dirty set is a SUBSET of
    /// the trie and the device flow must additionally be fed each dirty node's retained sibling refs; the H2D volume
    /// reported here is therefore a LOWER bound (structure estimate ~40B/node is the only proxy for that overhead).
    /// </remarks>
    private static (long d2hBytes, long leafValueBytes, long retainedParentBytes) MeasureVolumes(IReadOnlyList<PatriciaTree> trees)
    {
        long d2h = 0, leafVals = 0;
        foreach (PatriciaTree tree in trees)
        {
            TrieNode? root = tree.RootRef;
            if (root is null) continue;
            Stack<TrieNode> stack = new();
            stack.Push(root);
            while (stack.Count > 0)
            {
                TrieNode node = stack.Pop();
                int rlpLen = node.FullRlp.Length;
                // D2H: node's persistable representation = 32B key + its RLP (Commit stores by keccak -> rlp).
                d2h += 32 + rlpLen;
                if (node.IsLeaf)
                {
                    leafVals += node.Value.Length;
                }
                int childCount = node.IsBranch ? TrieNode.BranchesCount : node.IsExtension ? 1 : 0;
                for (int i = 0; i < childCount; i++)
                {
                    if (node.TryGetDirtyChild(i, out TrieNode? child))
                    {
                        stack.Push(child!);
                    }
                }
            }
        }
        // retained-parent = 0 for all-fresh synthetic tries (see remarks); H2D is thus a lower bound.
        return (d2h, leafVals, 0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------
    private static int[] BuildSlotCounts(int accounts, out int totalSlots)
    {
        Random rng = new(42);
        int[] slotCounts = new int[accounts];
        totalSlots = 0;
        for (int i = 0; i < accounts; i++)
        {
            double roll = rng.NextDouble();
            slotCounts[i] = roll < 0.85 ? rng.Next(1, 5)
                : roll < 0.97 ? rng.Next(5, 31)
                : rng.Next(100, 401);
            totalSlots += slotCounts[i];
        }
        return slotCounts;
    }

    private static Address AddressFromIndex(int i)
    {
        byte[] bytes = new byte[20];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), (ulong)i + 1);
        // spread into upper bytes too so addresses don't all share a long common prefix (realistic trie fan-out)
        BitConverter.TryWriteBytes(bytes.AsSpan(12, 8), unchecked((ulong)(i * 2654435761L)));
        return new Address(bytes);
    }

    private static void GcQuiesce()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    private static double Median(int accounts, Func<double> pass)
    {
        // Warmup: drive JIT tiering + steady-state GC before the measured window (unmeasured).
        pass();
        pass();
        double[] samples = new double[Passes];
        for (int p = 0; p < Passes; p++) samples[p] = pass();
        Array.Sort(samples);
        return samples[Passes / 2];
    }

    private static double MedianOf(List<(double c, double e, double h)> splits, Func<(double c, double e, double h), double> sel)
    {
        double[] vals = new double[splits.Count];
        for (int i = 0; i < splits.Count; i++) vals[i] = sel(splits[i]);
        Array.Sort(vals);
        return vals[vals.Length / 2];
    }

    private static void PrintTable(List<ShapeResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("=== ROOT SHARE: Commit-path phase split (median ms) ===");
        Console.WriteLine($"{"Accounts",8} {"TotSlots",9} {"write(a)",10} {"root(b)",10} {"commitFull(c)",14} {"persist(c-b)",13} {"rootShare%",11}");
        foreach (ShapeResult r in results)
        {
            // Commit-path total = write-apply + full commit (full commit already contains root compute).
            // root-computation share = root(b) / (write(a) + commitFull(c))... but commitFull duplicates root work.
            // Cleaner decomposition: total = write(a) + root(b) + persist(c-b). rootShare = root(b)/total.
            double total = r.WriteApplyMs + r.RootComputeMs + r.CommitPersistMs;
            double rootShare = total > 0 ? 100.0 * r.RootComputeMs / total : 0;
            Console.WriteLine($"{r.Accounts,8} {r.TotalSlots,9} {r.WriteApplyMs,10:F3} {r.RootComputeMs,10:F3} {r.CommitFullMs,14:F3} {r.CommitPersistMs,13:F3} {rootShare,11:F1}");
        }
        Console.WriteLine("  total = write(a) + root(b) + persist(c-b); rootShare = root(b)/total.");
        Console.WriteLine("  CAVEAT: excludes EVM/execution time. True block-level root share is SMALLER; needs expb/dotTrace.");

        Console.WriteLine();
        Console.WriteLine("=== OFFLOADABLE FRACTION: root-computation seam split (median ms, batched wave, storage tries + state trie) ===");
        Console.WriteLine($"{"Accounts",8} {"collect",10} {"encode+splice",14} {"hash",10} {"movable%",10} {"cpu-bound%",11}");
        foreach (ShapeResult r in results)
        {
            double waveTotal = r.CollectMs + r.EncodeMs + r.HashMs;
            // GPU-movable = encode+splice + hash (the per-node work). CPU-bound = collect (traversal/bookkeeping).
            double movable = waveTotal > 0 ? 100.0 * (r.EncodeMs + r.HashMs) / waveTotal : 0;
            double cpuBound = waveTotal > 0 ? 100.0 * r.CollectMs / waveTotal : 0;
            Console.WriteLine($"{r.Accounts,8} {r.CollectMs,10:F3} {r.EncodeMs,14:F3} {r.HashMs,10:F3} {movable,10:F1} {cpuBound,11:F1}");
        }
        Console.WriteLine("  movable% = (encode+splice + hash)/(collect+encode+hash); cpu-bound% = collect share.");
        Console.WriteLine("  NOTE: encode column = full-wave minus hash minus collect => encode+splice+per-step-bookkeeping residual.");

        Console.WriteLine();
        Console.WriteLine("=== TRANSFER BUDGET: transfer/launch estimate + net-win bound ===");
        Console.WriteLine($"{"Accounts",8} {"dirtyNodes",11} {"D2H KB",9} {"H2D KB",9} {"xfer us",9} {"dispatch us",12} {"movable ms",11} {"netWin ms",10} {"netWin%",9}");
        foreach (ShapeResult r in results)
        {
            double xferBytes = r.D2HBytes + r.H2DBytes;
            double xferUs = xferBytes / (PcieEffectiveGiBs * 1e9) * 1e6; // bytes / (B/s) -> s -> us
            double dispatchUs = DispatchFloorUs;                          // single device-resident dispatch
            double overheadMs = (xferUs + dispatchUs) / 1000.0;
            double movableMs = r.EncodeMs + r.HashMs;                     // device-movable time from the offloadable split
            double netWinMs = movableMs - overheadMs;
            double movableTotal = r.WriteApplyMs + r.RootComputeMs + r.CommitPersistMs;
            double netWinPct = movableTotal > 0 ? 100.0 * netWinMs / movableTotal : 0;
            Console.WriteLine($"{r.Accounts,8} {r.DirtyNodeCount,11} {r.D2HBytes / 1024.0,9:F1} {r.H2DBytes / 1024.0,9:F1} {xferUs,9:F1} {dispatchUs,12:F1} {movableMs,11:F3} {netWinMs,10:F3} {netWinPct,9:F1}");
        }
        Console.WriteLine("  xfer us = (D2H + H2D) / (PCIe GB/s); netWin ms = movable ms - (xfer + dispatch) ms.");
        Console.WriteLine("  netWin% = netWin ms / commit-path total (from the root-share table). Positive => a single-dispatch device flow could beat CPU on this shape.");
        Console.WriteLine("  CAVEAT: movable ms is measured with the CPU PerMessage hasher as the encode/hash proxy; a real device kernel");
        Console.WriteLine("  would replace hash-ms with kernel time (not modeled here) - this bound credits the GPU the FULL movable ms,");
        Console.WriteLine("  so it is an OPTIMISTIC upper bound on the win.");
    }
}
