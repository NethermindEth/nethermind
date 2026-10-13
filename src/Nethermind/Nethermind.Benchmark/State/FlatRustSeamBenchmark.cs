// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// The cost of one hot read through the native flat DB library next to the same read through the C# persistence, over
/// one RocksDB holding the same data: an account, a slot and a trie node by path, each present and in the block cache,
/// or in the carry-forward cache when it is on.
/// </summary>
/// <remarks>
/// With <see cref="FlatRustTransitionBenchmark"/> this is the per-call cost the seam counters
/// (<see cref="SeamCounters"/>) are multiplied by to decompose the seam's share of a block. The keys are read in a
/// shuffled order that cycles through more keys than a branch predictor learns.
/// </remarks>
[MemoryDiagnoser]
public class FlatRustSeamBenchmark
{
    private const int KeyCount = 4096;

    private TempPath _path = null!;
    private ColumnsDb<FlatDbColumns> _db = null!;
    private IPersistence _persistence = null!;
    private IPersistence.IPersistenceReader _reader = null!;
    private Address[] _accounts = null!;
    private UInt256[] _slots = null!;
    private TreePath[] _paths = null!;
    private int[] _order = null!;
    private int _index;

    [Params(FlatDbBackend.CSharp, FlatDbBackend.RustPersistence)]
    public FlatDbBackend Backend;

    [Params(false, true)]
    public bool CarryForward;

    [GlobalSetup]
    public void Setup()
    {
        _path = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        _db = new ColumnsDb<FlatDbColumns>(_path.Path, new DbSettings("Flat", DbNames.Flat), dbConfig,
            new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(1.GiB), NullLogManager.Instance, validateConfig: false),
            NullLogManager.Instance, []);

        Random random = new(20261013);
        _accounts = new Address[KeyCount];
        _slots = new UInt256[KeyCount];
        _paths = new TreePath[KeyCount];
        IPersistence writer = new RocksDbPersistence(_db, NullLogManager.Instance, new FlatDbConfig());
        using (IPersistence.IWriteBatch batch = writer.CreateWriteBatch(StateId.PreGenesis, new StateId(1, Keccak.Compute("state 1"))))
        {
            byte[] node = new byte[100];
            for (int i = 0; i < KeyCount; i++)
            {
                _accounts[i] = new Address(Keccak.Compute($"account {i}"));
                _slots[i] = (UInt256)random.NextInt64();
                _paths[i] = TreePath.FromHexString(Keccak.Compute($"path {i}").ToString(false)[..(4 + random.Next(8))]);
                random.NextBytes(node);
                batch.SetAccount(_accounts[i], new Account((ulong)i, (UInt256)i));
                batch.SetStorage(_accounts[i], _slots[i], (UInt256)(i + 1));
                batch.SetStateTrieNode(_paths[i], node);
            }
        }

        _order = [.. Enumerable.Range(0, KeyCount)];
        random.Shuffle(_order);
        FlatDbConfig config = new() { Backend = Backend, EnableCarryForwardCache = CarryForward };
        IPersistence layout = Backend == FlatDbBackend.CSharp
            ? new CachedReaderPersistence(new RocksDbPersistence(_db, NullLogManager.Instance, config), new ProcessExitSource(CancellationToken.None), NullLogManager.Instance)
            : new RustPersistence(_db, config, NullLogManager.Instance);
        _persistence = CarryForward && Backend == FlatDbBackend.CSharp ? new CarryForwardCachingPersistence(layout) : layout;
        _reader = _persistence.CreateReader();

        // Into the block cache, and into the carry-forward cache when it is on.
        for (int i = 0; i < KeyCount; i++)
        {
            GetAccount();
            TryGetSlot();
            TryLoadStateRlp();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        switch (_persistence)
        {
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        _db.Dispose();
        _path.Dispose();
    }

    [Benchmark]
    public Account GetAccount() => _reader.GetAccount(_accounts[Next()]);

    [Benchmark]
    public bool TryGetSlot()
    {
        int index = Next();
        UInt256 value = default;
        return _reader.TryGetSlot(_accounts[index], _slots[index], ref value);
    }

    [Benchmark]
    public byte[] TryLoadStateRlp() => _reader.TryLoadStateRlp(_paths[Next()], ReadFlags.None);

    private int Next() => _order[_index = (_index + 1) & (KeyCount - 1)];
}

/// <summary>The bare cost of crossing into the native flat DB library: a P/Invoke, then one through the export guard.</summary>
public class FlatRustTransitionBenchmark
{
    [GlobalSetup]
    public void Setup() => NativeMethods.EnsureAvailable();

    /// <summary>An export that returns a constant, without the guard every other export runs in.</summary>
    [Benchmark(Baseline = true)]
    public uint Unguarded() => NativeMethods.fdb_abi_version();

    /// <summary>An export with a trivial body inside the guard, which catches panics and counts the call.</summary>
    [Benchmark]
    public unsafe int Guarded()
    {
        byte* name;
        nuint length;
        return (int)NativeMethods.fdb_seam_counter_name(0, &name, &length);
    }
}
