// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>One block's write batch where every written account also has a few written slots, as for active contracts.</summary>
[MemoryDiagnoser]
[WarmupCount(5)]
[IterationCount(30)]
[InvocationCount(1)]
public class ContractWriteBatchBenchmark
{
    private const int Blocks = 20;

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private INodeRecordBuffer _nodeRecordBuffer = null!;
    private Address[] _addresses = null!;
    private FlatWorldStateScope _scope = null!;

    [Params(500)]
    public int AccountCount { get; set; }

    [Params(1, 4)]
    public int SlotsPerAccount { get; set; }

    [Params(false, true)]
    public bool AccountsChanged { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _config = new FlatDbConfig();
        _resourcePool = new ResourcePool(_config);
        _addresses = new Address[AccountCount];
        Parallel.For(0, AccountCount, i => _addresses[i] = DeriveAddress(i + 1));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _nodeRecordBuffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        ReadOnlySnapshotBundle readOnly = new(
            new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty(), _nodeRecordBuffer.PinAll());
        SnapshotBundle bundle = new(readOnly, _nodeRecordBuffer, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        _scope = new FlatWorldStateScope(
            currentStateId: new StateId(0, Keccak.EmptyTreeHash),
            snapshotBundle: bundle,
            codeDb: new NullCodeDb(),
            commitTarget: new CapturingCommitTarget(),
            configuration: _config,
            trieCacheWarmer: new NoopTrieWarmer(),
            logManager: NullLogManager.Instance);

        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + 1)));
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _scope.Dispose();
        _nodeRecordBuffer.Dispose();
    }

    [Benchmark(OperationsPerInvoke = Blocks)]
    public void BlockWriteBatch()
    {
        for (int block = 1; block <= Blocks; block++)
        {
            WriteBlock(block);
        }
    }

    private void WriteBlock(int block)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(_addresses[i], SlotsPerAccount);
            for (int s = 0; s < SlotsPerAccount; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1), (UInt256)(ulong)(i + s + block));
            }
        }

        if (!AccountsChanged) return;

        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + block + 1)));
        }
    }
}
