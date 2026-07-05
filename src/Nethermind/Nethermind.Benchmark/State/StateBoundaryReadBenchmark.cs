// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Compares the two ways a state boundary read can obtain the persisted <see cref="StateId"/>
/// through the production decorator stack (CarryForwardCaching over CachedReader): materializing
/// a persistence reader versus the metadata-only <see cref="IPersistence.GetCurrentState"/>.
/// The AfterWriteBatch variants measure the post-persist window, where the shared reader cache
/// has just been invalidated. In-memory columns db, so the reader-path cold cost excludes the
/// native RocksDB snapshot a real node would additionally pay.
/// </summary>
[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class StateBoundaryReadBenchmark
{
    private IPersistence _stack = null!;
    private StateId _current;

    [GlobalSetup]
    public void Setup()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence inner = new(db, LimboLogs.Instance);
        _stack = new CarryForwardCachingPersistence(
            new CachedReaderPersistence(inner, new ProcessExitSource(CancellationToken.None), LimboLogs.Instance));

        _current = new StateId(1, Keccak.EmptyTreeHash);
        using (_stack.CreateWriteBatch(StateId.PreGenesis, _current)) { }
    }

    [Benchmark(Baseline = true)]
    public StateId ReaderPath()
    {
        using IPersistence.IPersistenceReader reader = _stack.CreateReader();
        return reader.CurrentState;
    }

    [Benchmark]
    public StateId AccessorPath() => _stack.GetCurrentState();

    [Benchmark]
    public StateId ReaderPathAfterWriteBatch()
    {
        using (_stack.CreateWriteBatch(_current, _current)) { }
        using IPersistence.IPersistenceReader reader = _stack.CreateReader();
        return reader.CurrentState;
    }

    [Benchmark]
    public StateId AccessorPathAfterWriteBatch()
    {
        using (_stack.CreateWriteBatch(_current, _current)) { }
        return _stack.GetCurrentState();
    }
}
