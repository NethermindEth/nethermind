// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Rust;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// The cost of a call on one native scope from many threads at once, as the storage workers, the block-access-list
/// readers and the prewarmer call the main block's scope: through the <see cref="SafeHandle"/> a P/Invoke would take,
/// whose reference count all the threads share, or through the striped call lease of <see cref="ScopeHandle.Enter"/>
/// (K-lease), which keeps each thread's count on a cache line of its own and passes the raw pointer.
/// </summary>
/// <remarks>
/// The call is an account read of a scope with nothing written, over the empty state, so it takes no lock and reads
/// nothing: what is left is the transition, the export's guard and the handle's reference counting. Each thread reads
/// its own address; every invocation runs <see cref="CallsPerThread"/> calls on each of <see cref="Threads"/> dedicated
/// threads, and the time reported is per call of one thread.
/// </remarks>
public unsafe partial class ScopeCallBenchmark
{
    private const int CallsPerThread = 16 * 1024;

    private NativeScope _native = null!;
    private Thread[] _workers = [];
    private Barrier _start = null!;
    private Barrier _end = null!;
    private Action<int> _work = static _ => { };
    private volatile bool _stopping;

    [Params(1, 16, 64)]
    public int Threads;

    [GlobalSetup]
    public void Setup()
    {
        _native = new NativeScope();
        _start = new Barrier(Threads + 1);
        _end = new Barrier(Threads + 1);
        _workers = new Thread[Threads];
        for (int i = 0; i < Threads; i++)
        {
            int index = i;
            _workers[i] = new Thread(() => RunWorker(index)) { IsBackground = true, Name = $"scope-call-{i}" };
            _workers[i].Start();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stopping = true;
        _start.SignalAndWait();
        foreach (Thread worker in _workers) worker.Join();
        _native.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = CallsPerThread)]
    public void SafeHandle() => RunOnEveryThread(SafeHandleCalls);

    [Benchmark(OperationsPerInvoke = CallsPerThread)]
    public void StripedLease() => RunOnEveryThread(LeasedCalls);

    private void RunOnEveryThread(Action<int> work)
    {
        _work = work;
        _start.SignalAndWait();
        _end.SignalAndWait();
    }

    private void RunWorker(int index)
    {
        while (true)
        {
            _start.SignalAndWait();
            if (_stopping) return;
            _work(index);
            _end.SignalAndWait();
        }
    }

    private void SafeHandleCalls(int index)
    {
        byte* address = stackalloc byte[Address.Size];
        address[0] = (byte)index;
        FfiAccount account;
        byte found;
        ReadSource source;
        for (int i = 0; i < CallsPerThread; i++)
        {
            StatusMap.ThrowIfFailed(ScopeGetAccount(_native.Scope, address, 0, &account, &found, &source));
        }
    }

    private void LeasedCalls(int index)
    {
        byte* address = stackalloc byte[Address.Size];
        address[0] = (byte)index;
        FfiAccount account;
        byte found;
        ReadSource source;
        for (int i = 0; i < CallsPerThread; i++)
        {
            using BundleBaseHandle.Call call = _native.Scope.Enter();
            StatusMap.ThrowIfFailed(NativeMethods.fdb_scope_get_account(call.Bundle, address, 0, &account, &found, &source));
        }
    }

    /// <summary><c>fdb_scope_get_account</c> through the <see cref="SafeHandle"/> marshaller, which the adapter does not use.</summary>
    [LibraryImport("nethermind_flatdb", EntryPoint = "fdb_scope_get_account")]
    private static partial FdbStatus ScopeGetAccount(ScopeHandle scope, byte* address, byte promote, FfiAccount* account, byte* found, ReadSource* source);
}

/// <summary>
/// The writes of a block's state trie nodes into a native scope: one call per node, as <c>SetStateNode</c> makes them,
/// against one call for the whole batch, as <c>PublishStateNodes</c> hands them over. Each invocation writes
/// <see cref="Nodes"/> nodes of a block's sizes into a fresh scope; <see cref="OpenAndRelease"/> is the scope alone.
/// </summary>
public unsafe class ScopeNodeBatchBenchmark
{
    private const int Nodes = 1200;

    private NativeScope _native = null!;
    private FfiNodeRecord[] _records = [];
    private byte[] _blob = [];

    [GlobalSetup]
    public void Setup()
    {
        _native = new NativeScope();
        Random random = new(20261013);
        _records = new FfiNodeRecord[Nodes];
        int blobLength = 0;
        int[] lengths = new int[Nodes];
        for (int i = 0; i < Nodes; i++)
        {
            // Branches up to 532 bytes, leaves and extensions down to an inline-sized 32.
            lengths[i] = random.Next(32, 533);
            blobLength += lengths[i];
        }

        _blob = new byte[blobLength];
        random.NextBytes(_blob);
        ulong offset = 0;
        fixed (FfiNodeRecord* records = _records)
        {
            for (int i = 0; i < Nodes; i++)
            {
                FfiNodeRecord* record = records + i;
                TreePath path = TreePath.FromHexString(Keccak.Compute($"path {i}").ToString(false)[..random.Next(1, 65)]);
                path.Path.Bytes.CopyTo(new Span<byte>(record->Path, ValueHash256.MemorySize));
                record->PathLength = (byte)path.Length;
                Keccak.Compute($"node {i}").Bytes.CopyTo(new Span<byte>(record->Hash, ValueHash256.MemorySize));
                record->RlpOffset = offset;
                record->RlpLength = (uint)lengths[i];
                offset += (ulong)lengths[i];
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _native.Dispose();

    [Benchmark(Baseline = true)]
    public void OpenAndRelease() => _native.OpenScope().Dispose();

    [Benchmark]
    public void SetStateNodeEach()
    {
        using ScopeHandle scope = _native.OpenScope();
        fixed (FfiNodeRecord* records = _records)
        fixed (byte* blob = _blob)
        {
            for (int i = 0; i < Nodes; i++)
            {
                FfiNodeRecord* record = records + i;
                using BundleBaseHandle.Call call = scope.Enter();
                StatusMap.ThrowIfFailed(NativeMethods.fdb_scope_set_state_node(call.Bundle, record->Path, record->PathLength, record->Hash, blob + record->RlpOffset, record->RlpLength));
            }
        }
    }

    [Benchmark]
    public void PutStateNodes()
    {
        using ScopeHandle scope = _native.OpenScope();
        using BundleBaseHandle.Call call = scope.Enter();
        fixed (FfiNodeRecord* records = _records)
        fixed (byte* blob = _blob)
        {
            StatusMap.ThrowIfFailed(NativeMethods.fdb_scope_put_state_nodes(call.Bundle, records, Nodes, blob, (nuint)_blob.Length));
        }
    }
}

/// <summary>A native manager over an empty RocksDB, and a scope of the empty state opened from it.</summary>
internal sealed unsafe class NativeScope : IDisposable
{
    private readonly TempPath _path = TempPath.GetTempDirectory();
    private readonly ColumnsDb<FlatDbColumns> _db;
    private readonly RustPersistence _persistence;
    private readonly NativeFlatManager _manager;

    public NativeScope()
    {
        NativeMethods.EnsureAvailable();
        FlatDbConfig config = new() { Backend = FlatDbBackend.Rust };
        DbConfig dbConfig = new();
        _db = new ColumnsDb<FlatDbColumns>(_path.Path, new DbSettings("Flat", DbNames.Flat), dbConfig,
            new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), NullLogManager.Instance, validateConfig: false),
            NullLogManager.Instance, []);
        _persistence = new RustPersistence(_db, config, NullLogManager.Instance);
        _manager = new NativeFlatManager(_persistence, config, new CompactionSchedule(new MemDb(), config, NullLogManager.Instance),
            new BlocksConfig(), new NoFinality(), new StatePersistenceBarrier(), new ProcessExitSource(CancellationToken.None),
            new MetricsConfig(), NullLogManager.Instance);
        Scope = OpenScope();
    }

    public ScopeHandle Scope { get; }

    /// <summary>A main-block-processing scope of the empty state, holding its references on the store and the manager.</summary>
    public ScopeHandle OpenScope()
    {
        RustFlatStore store = _persistence.Store;
        ManagerHandle manager = _manager.Handle;
        store.AddChildReference();
        bool managerAdded = false;
        try
        {
            manager.DangerousAddRef(ref managerAdded);
            FfiStateId preGenesis = new(StateId.PreGenesis);
            FfiScopeFlags flags = new(filterInMemorySlotReads: false, ResourcePool.Usage.MainBlockProcessing);
            nint scope;
            FfiRobInfo info;
            _manager.ThrowIfFailed(NativeMethods.fdb_scope_open(manager, &preGenesis, 0, &flags, &scope, &info));
            return new ScopeHandle(store, manager, scope);
        }
        catch
        {
            if (managerAdded) manager.DangerousRelease();
            store.ReleaseChildReference();
            throw;
        }
    }

    public void Dispose()
    {
        Scope.Dispose();
        _manager.Dispose();
        _persistence.Dispose();
        _db.Dispose();
        _path.Dispose();
    }

    private sealed class NoFinality : IStateHeaderProvider
    {
        public ulong FinalizedBlockNumber => 0;

        public BlockHeader FindParentHeader(BlockHeader target) => null;

        public BlockHeader GetFinalizedHeader(ulong blockNumber) => null;
    }
}
