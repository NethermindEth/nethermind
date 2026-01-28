// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Isolated microbenchmarks for the slot-based StateProvider internals.
/// Measures inline cache hit/dict hit/cold miss reads, mutation fast/slow paths,
/// snapshot/restore, commit, and reset — independent of EVM dispatch overhead.
/// Run: dotnet run -c Release -p src/Nethermind/Nethermind.Benchmark.Runner -- --filter "*StateProviderBenchmarks*"
/// </summary>
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class StateProviderBenchmarks
{
    // Pre-created addresses. _hotAddress is the inline-cache-hit target.
    // _warmAddresses are dict-hit targets (16 address overflow the 4-entry inline cache).
    private Address _hotAddress = null!;
    private Address[] _warmAddresses = null!;

    private WorldState _worldState = null!;
    private StateProvider _stateProvider = null!;
    private IDisposable _scope = null!;
    private IReleaseSpec _spec = null!;

    [GlobalSetup]
    public void Setup()
    {
        _spec = Prague.Instance;
        _worldState = TestWorldStateFactory.CreateForTest();
        _stateProvider = _worldState._stateProvider;
        _scope = _worldState.BeginScope(IWorldState.PreGenesis);

        // Create accounts so they exist in state
        _hotAddress = new Address(Keccak.Compute("hot"));
        _worldState.CreateAccount(_hotAddress, 1000);

        _warmAddresses = new Address[16];
        for (int i = 0; i < _warmAddresses.Length; i++)
        {
            _warmAddresses[i] = new Address(Keccak.Compute($"warm{i}"));
            _worldState.CreateAccount(_warmAddresses[i], (UInt256)(i + 1));
        }

        _worldState.Commit(_spec, NullStateTracer.Instance);
        _worldState.CommitTree(0);
        _worldState.Reset();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
    }

    /// <summary>
    /// GetBalance on the same address repeatedly — inline cache hit path.
    /// Target: &lt;5ns per call.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public UInt256 GetBalance_InlineCacheHit()
    {
        // Prime the inline cache with a single read
        _stateProvider.GetBalance(_hotAddress);

        UInt256 result = default;
        for (int i = 0; i < 1000; i++)
        {
            result = _stateProvider.GetBalance(_hotAddress);
        }

        _stateProvider.Reset();
        return result;
    }

    /// <summary>
    /// GetBalance cycling through 16 addresses — forces dict lookups (inline cache is 4-entry).
    /// Target: ~12-15ns per call.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1024)]
    public UInt256 GetBalance_DictHit()
    {
        Address[] address = _warmAddresses;

        // Prime all 16 into the slot index
        for (int i = 0; i < address.Length; i++)
        {
            _stateProvider.GetBalance(address[i]);
        }

        UInt256 result = default;
        for (int i = 0; i < 1024; i++)
        {
            result = _stateProvider.GetBalance(address[i & 15]);
        }

        _stateProvider.Reset();
        return result;
    }

    /// <summary>
    /// First AddToBalance per frame per address — triggers SaveUndoAndUpdateFrame.
    /// Target: measures the slow path with undo buffer store.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 16)]
    public void Mutation_FirstWriteInFrame()
    {
        Address[] address = _warmAddresses;
        int snapshot = _stateProvider.TakeSnapshot();

        for (int i = 0; i < 16; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        _stateProvider.Restore(snapshot);
        _stateProvider.Reset();
    }

    /// <summary>
    /// Repeated AddToBalance on same address in same frame — OwnerFrameId already matches,
    /// so MutateSlot skips undo save. Should be ~6 instructions.
    /// Target: &lt;3ns per call.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public void Mutation_NthWriteSameSlot()
    {
        int snapshot = _stateProvider.TakeSnapshot();

        // First write primes the undo entry
        _stateProvider.AddToBalance(_hotAddress, 1, _spec);

        // Subsequent writes are the fast path
        for (int i = 0; i < 1000; i++)
        {
            _stateProvider.AddToBalance(_hotAddress, 1, _spec);
        }

        _stateProvider.Restore(snapshot);
        _stateProvider.Reset();
    }

    /// <summary>
    /// Revert a frame with 5 undo entries.
    /// Should show forward array writes, zero dict lookups.
    /// Target: &lt;50ns total.
    /// </summary>
    [Benchmark]
    public void Revert_5Entries()
    {
        Address[] address = _warmAddresses;
        int snapshot = _stateProvider.TakeSnapshot();

        for (int i = 0; i < 5; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        _stateProvider.Restore(snapshot);
        _stateProvider.Reset();
    }

    /// <summary>
    /// Revert a frame with 16 undo entries — measures scaling.
    /// </summary>
    [Benchmark]
    public void Revert_16Entries()
    {
        Address[] address = _warmAddresses;
        int snapshot = _stateProvider.TakeSnapshot();

        for (int i = 0; i < 16; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        _stateProvider.Restore(snapshot);
        _stateProvider.Reset();
    }

    /// <summary>
    /// Commit with 3 dirty slots. Iterates _dirtySlotIndices only.
    /// </summary>
    [Benchmark]
    public void Commit_3DirtySlots()
    {
        Address[] address = _warmAddresses;
        for (int i = 0; i < 3; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        _stateProvider.Commit(_spec, NullStateTracer.Instance, commitRoots: false, isGenesis: false);
        _stateProvider.Reset();
    }

    /// <summary>
    /// Commit with 10 dirty slots — measures scaling.
    /// </summary>
    [Benchmark]
    public void Commit_10DirtySlots()
    {
        Address[] address = _warmAddresses;
        for (int i = 0; i < 10; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        _stateProvider.Commit(_spec, NullStateTracer.Instance, commitRoots: false, isGenesis: false);
        _stateProvider.Reset();
    }

    /// <summary>
    /// TakeSnapshot is O(1): two array stores + one increment.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public void TakeSnapshot_SuccessReturn()
    {
        for (int i = 0; i < 100; i++)
        {
            _stateProvider.TakeSnapshot();
        }

        // Simulate success returns — just forget the snapshots
        _stateProvider.Reset();
    }

    /// <summary>
    /// 10-deep nested snapshots with one mutation each, then revert all.
    /// Exercises the frame stack and undo buffer with realistic nesting.
    /// </summary>
    [Benchmark]
    public void NestedSnapshotRestore_Depth10()
    {
        Address[] address = _warmAddresses;
        Span<int> snapshots = stackalloc int[10];

        for (int depth = 0; depth < 10; depth++)
        {
            snapshots[depth] = _stateProvider.TakeSnapshot();
            _stateProvider.AddToBalance(address[depth], 1, _spec);
        }

        // Revert from deepest to shallowest
        for (int depth = 9; depth >= 0; depth--)
        {
            _stateProvider.Restore(snapshots[depth]);
        }

        _stateProvider.Reset();
    }

    /// <summary>
    /// Phase 8 baseline: GetBalance through IWorldState interface dispatch.
    /// Measures the vtable overhead that direct StateProvider access eliminates.
    /// Compare with GetBalance_InlineCacheHit to see the interface dispatch cost.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public UInt256 GetBalance_ViaInterface()
    {
        IWorldState ws = _worldState;
        Address Address = _hotAddress;
        ws.GetBalance(Address);

        UInt256 result = default;
        for (int i = 0; i < 1000; i++)
        {
            result = ws.GetBalance(Address);
        }

        _worldState.Reset();
        return result;
    }

    /// <summary>
    /// Phase 10 baseline: Reset cost after touching 4 slots (typical simple transfer).
    /// Phase 10 replaces O(slotCount) Array.Clear with O(1) epoch bump.
    /// </summary>
    [Benchmark]
    public void Reset_4Slots()
    {
        Address[] address = _warmAddresses;
        for (int i = 0; i < 4; i++)
        {
            _stateProvider.GetBalance(address[i]);
        }

        _stateProvider.Reset();
    }

    /// <summary>
    /// Phase 10 baseline: Reset cost after touching 16 slots (complex tx with access list).
    /// Shows O(n) scaling that epoch-based reset would eliminate.
    /// </summary>
    [Benchmark]
    public void Reset_16Slots()
    {
        Address[] address = _warmAddresses;
        for (int i = 0; i < 16; i++)
        {
            _stateProvider.GetBalance(address[i]);
        }

        _stateProvider.Reset();
    }

    /// <summary>
    /// Phase 11 baseline: Commit cost isolated — primes slots in IterationSetup to separate
    /// mutation alloc overhead from commit dict overhead.
    /// Measures _blockChanges dictionary insert cost for 10 dirty slots.
    /// </summary>
    [Benchmark]
    public void Commit_Isolated_10DirtySlots()
    {
        Address[] address = _warmAddresses;

        // Prime slots + mutate (cost we want to exclude from the commit measurement)
        for (int i = 0; i < 10; i++)
        {
            _stateProvider.AddToBalance(address[i], 1, _spec);
        }

        // This is the part Phase 11 optimizes: _blockChanges dict lookups
        _stateProvider.Commit(_spec, NullStateTracer.Instance, commitRoots: false, isGenesis: false);

        // Reset without clearing block changes — simulates mid-block state
        _stateProvider.Reset(resetBlockChanges: false);
    }

    /// <summary>
    /// Phase 12 baseline: First-touch slot creation from clean state.
    /// Measures GetSlotIndexSlow + possible array resize on cold addresses.
    /// Phase 12 pre-allocates arrays to eliminate resizes on the hot path.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 16)]
    public void ColdSlotCreation_16Addresses()
    {
        Address[] address = _warmAddresses;
        for (int i = 0; i < 16; i++)
        {
            _stateProvider.GetBalance(address[i]);
        }

        _stateProvider.Reset();
    }
}
