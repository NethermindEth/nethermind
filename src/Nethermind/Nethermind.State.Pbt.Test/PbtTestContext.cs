// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using Nethermind.State.Pbt.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>Wires the full PBT component stack over in-memory column dbs, as the plugin module would.</summary>
internal sealed class PbtTestContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PbtCachedReaderPersistence _cachedReaderPersistence;
    private readonly PbtTrieNodeCache _trieNodeCache;
    private readonly TestStateHeaderProvider _stateHeaderProvider = new();
    private readonly IPbtChildHeaderSource _childHeaders;

    public MemDb CodeDb { get; } = new();
    public PbtConfig Config { get; }
    public TestFinalizedStateProvider FinalizedStateProvider { get; } = new();
    public PbtSnapshotRepository Repository { get; } = new(new MetricsConfig());
    public IPbtResourcePool ResourcePool { get; }
    public IPbtPersistence Persistence { get; }
    public PbtPersistenceCoordinator Coordinator { get; }
    public PbtDbManager Manager { get; }
    public PbtStateReader StateReader { get; }
    public PbtWorldStateManager WorldStateManager { get; }

    public PbtTestContext(IColumnsDb<PbtColumns>? db = null, PbtConfig? config = null, IPbtChildHeaderSource? childHeaders = null, IMetricsConfig? metricsConfig = null, IRefCountingMemoryProvider? nodeGroupMemory = null)
    {
        metricsConfig ??= new MetricsConfig();
        db ??= new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        Config = config ?? new PbtConfig();
        _childHeaders = childHeaders ?? NullPbtChildHeaderSource.Instance;

        // Production randomizes this offset; tests must use stable compaction boundaries.
        if (Config.CompactionOffset < 0) Config.CompactionOffset = 0;
        _cachedReaderPersistence = new PbtCachedReaderPersistence(new PbtRocksDbPersistence(db, Config, NullTrieNodeLog.Instance), new TestProcessExitSource(_cts));
        Persistence = Config.CarryForwardCache ? new PbtCarryForwardCachingPersistence(_cachedReaderPersistence) : _cachedReaderPersistence;
        ResourcePool = new PbtResourcePool(Config, nodeGroupMemory ?? PooledRefCountingMemoryProvider.Instance);
        PbtCompactionSchedule schedule = new(new MemDb(), Config, LimboLogs.Instance);
        PbtSnapshotCompactor compactor = new(ResourcePool, schedule, Repository, Config);
        Coordinator = new PbtPersistenceCoordinator(Config, FinalizedStateProvider, Persistence, Repository, schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        _trieNodeCache = new PbtTrieNodeCache(Config);
        Manager = new PbtDbManager(Repository, Coordinator, Persistence, ResourcePool, compactor, new TestProcessExitSource(_cts), LimboLogs.Instance, Config, metricsConfig, _trieNodeCache);
        StateReader = new PbtStateReader(CodeDb, Manager);
        WorldStateManager = new PbtWorldStateManager(Manager, _childHeaders, _stateHeaderProvider, ResourcePool, StateReader, () => new PbtOverridableWorldScope(CodeDb, Manager, ResourcePool, metricsConfig, Config, _stateHeaderProvider, _trieNodeCache), CodeDb, Config);
    }

    public PbtScopeProvider CreateScopeProvider(bool isReadOnly = false, ILogManager? logManager = null) =>
        new(CodeDb, Manager, _childHeaders, _stateHeaderProvider, ResourcePool, isReadOnly ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing, isReadOnly,
            Config, logManager);

    /// <summary>
    /// The contract code deployed by <see cref="RunReferenceBlocks"/>: more than 128 + 256 chunks (11904 bytes), so the
    /// overflow chunks not only land in the content-addressed code zone but span more than one stem of it.
    /// </summary>
    public static readonly byte[] ReferenceCode = CreateReferenceCode();

    /// <summary>
    /// Processes three blocks over <paramref name="provider"/>, the last of which only reads back what the first two wrote,
    /// and returns their state roots.
    /// </summary>
    public static Hash256[] RunReferenceBlocks(IWorldStateScopeProvider provider)
    {
        IReleaseSpec spec = Prague.Instance;
        Address eoa = TestItem.AddressA;
        Address contract = TestItem.AddressB;
        WorldState worldState = new(provider, LimboLogs.Instance);

        Hash256 root1;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(eoa, 100, 1);
            worldState.CreateAccount(contract, 42);
            worldState.InsertCode(contract, ValueKeccak.Compute(ReferenceCode), ReferenceCode, spec);
            worldState.Set(new StorageCell(contract, 5), (UInt256)0xAB);       // header-region slot
            worldState.Set(new StorageCell(contract, 1000), (UInt256)0x1234); // storage-zone slot
            worldState.Commit(spec);
            worldState.CommitTree(1);
            root1 = worldState.StateRoot;
        }

        BlockHeader header1 = Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject;
        Hash256 root2;
        using (worldState.BeginScope(header1))
        {
            // The world state must not read emptiness off the storage tree's placeholder root: taking it as proof
            // that the account holds no slot answers every read after the first with zero, and hands the commit a
            // storage clear that would mark the account self-destructed.
            Assert.That(worldState.Get(new StorageCell(contract, 5)), Is.EqualTo((UInt256)0xAB));
            Assert.That(worldState.Get(new StorageCell(contract, 1000)), Is.EqualTo((UInt256)0x1234));
            Assert.That(worldState.Get(new StorageCell(contract, 5)), Is.EqualTo((UInt256)0xAB));

            worldState.AddToBalance(eoa, 5, spec, out _);
            // a contract receiving ETH is dirty with unchanged code — its BASIC_DATA is rewritten
            // and its code size must be preserved (read back, not recomputed from the code)
            worldState.AddToBalance(contract, 10, spec, out _);
            worldState.Set(new StorageCell(contract, 5), UInt256.Zero);
            worldState.Set(new StorageCell(contract, 70), (UInt256)0x07);
            worldState.Commit(spec);
            worldState.CommitTree(2);
            root2 = worldState.StateRoot;
        }

        BlockHeader header2 = Build.A.BlockHeader.WithNumber(2).WithStateRoot(root2).TestObject;
        Hash256 root3;
        using (worldState.BeginScope(header2))
        {
            Assert.That(worldState.GetBalance(eoa), Is.EqualTo((UInt256)105));
            Assert.That(worldState.GetBalance(contract), Is.EqualTo((UInt256)52));
            Assert.That(worldState.GetCode(contract).ToArray(), Is.EqualTo(ReferenceCode));
            Assert.That(worldState.Get(new StorageCell(contract, 5)), Is.EqualTo(UInt256.Zero));
            Assert.That(worldState.Get(new StorageCell(contract, 70)), Is.EqualTo((UInt256)0x07));
            Assert.That(worldState.Get(new StorageCell(contract, 1000)), Is.EqualTo((UInt256)0x1234));

            worldState.IncrementNonce(eoa, 1, out _);
            worldState.Commit(spec);
            worldState.CommitTree(3);
            root3 = worldState.StateRoot;
        }

        return [root1, root2, root3];
    }

    private static byte[] CreateReferenceCode()
    {
        byte[] code = new byte[15000];
        for (int i = 0; i < code.Length; i += 10) code[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        return code;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Manager.DisposeAsync();
        _trieNodeCache.Dispose();
        await _cachedReaderPersistence.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>Finality as the persistence coordinator sees it; parents are never resolved through it.</summary>
    public sealed class TestFinalizedStateProvider : IStateHeaderProvider
    {
        private readonly Dictionary<ulong, BlockHeader> _headers = [];

        public ulong FinalizedBlockNumber { get; set; }

        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => _headers.GetValueOrDefault(blockNumber);

        public BlockHeader? FindParentHeader(BlockHeader target) => null;

        public void SetCanonicalRoot(ulong blockNumber, Hash256 root) =>
            _headers[blockNumber] = Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(root).TestObject;
    }

    private sealed class TestProcessExitSource(CancellationTokenSource cts) : IProcessExitSource
    {
        public CancellationToken Token => cts.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
