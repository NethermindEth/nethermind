// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Benchmarks that specifically measure the impact of read-cache separation
/// in StateProvider and PersistentStorageProvider.
///
/// The read-cache optimization eliminates JustCache journal entries for read-only
/// access, reducing Commit iteration overhead. These benchmarks capture the difference
/// between workloads that are read-heavy vs write-heavy.
/// </summary>
[MemoryDiagnoser]
public class ReadCacheBenchmarks
{
    private IContainer _container;
    private IWorldState _globalWorldState;

    private const int AccountCount = 2048;
    private const int ContractCount = 256;
    private const int SlotCount = ContractCount * 32;

    private Address[] _accounts;
    private (Address Account, UInt256 Slot)[] _slots;
    private IReleaseSpec _spec = new Prague();
    private BlockHeader _baseBlock;

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Build();

        IWorldState worldState = _globalWorldState = _container.Resolve<IMainProcessingContext>().WorldState;
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);

        Random rand = new(42);
        byte[] buf = new byte[20];

        _accounts = new Address[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            rand.NextBytes(buf);
            Address addr = new Address(buf.ToArray());
            worldState.AddToBalanceAndCreateIfNotExists(addr, (UInt256)rand.NextInt64() + 1, _spec);
            _accounts[i] = addr;
        }

        Address[] contracts = new Address[ContractCount];
        for (int i = 0; i < ContractCount; i++)
        {
            rand.NextBytes(buf);
            Address addr = new Address(buf.ToArray());
            worldState.AddToBalanceAndCreateIfNotExists(addr, (UInt256)rand.NextInt64() + 1, _spec);
            contracts[i] = addr;
        }

        _slots = new (Address, UInt256)[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            Address contract = contracts[rand.Next(ContractCount)];
            UInt256 slot = (UInt256)rand.NextInt64();
            rand.NextBytes(buf);
            worldState.Set(new StorageCell(contract, slot), buf.ToArray());
            _slots[i] = (contract, slot);
        }

        worldState.Commit(_spec);
        worldState.CommitTree(0);
        worldState.Reset();
        _baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
    }

    [GlobalCleanup]
    public void Teardown()
    {
        _container.Dispose();
    }

    /// <summary>
    /// Simulates a value transfer transaction: read sender, read recipient,
    /// modify sender (balance-nonce), modify recipient (balance), commit.
    /// The read-cache saves journal entries for the initial reads.
    /// </summary>
    [Benchmark]
    [Arguments(50)]
    [Arguments(200)]
    public void SimulateValueTransfers(int txCount)
    {
        Random rand = new(1);
        IWorldState ws = _globalWorldState;
        using IDisposable _ = ws.BeginScope(_baseBlock);

        for (int tx = 0; tx < txCount; tx++)
        {
            Address sender = _accounts[rand.Next(AccountCount)];
            Address recipient = _accounts[rand.Next(AccountCount)];

            // Read phase: check balances (read-only access)
            ws.GetBalance(sender);
            ws.GetBalance(recipient);

            // Write phase: transfer value
            ws.SubtractFromBalance(sender, 1, _spec);
            ws.IncrementNonce(sender);
            ws.AddToBalance(recipient, 1, _spec);
        }

        ws.Commit(_spec);
        ws.CommitTree(1);
        ws.Reset();
    }

    /// <summary>
    /// Simulates contract-heavy transactions: each tx reads multiple storage
    /// slots but only writes a few. This pattern benefits most from the
    /// storage read cache (fewer JustCache entries in _changes).
    /// </summary>
    [Benchmark]
    [Arguments(50)]
    [Arguments(200)]
    public void SimulateContractCalls(int txCount)
    {
        Random rand = new(1);
        IWorldState ws = _globalWorldState;
        using IDisposable _ = ws.BeginScope(_baseBlock);
        byte[] buf = new byte[32];

        for (int tx = 0; tx < txCount; tx++)
        {
            Address sender = _accounts[rand.Next(AccountCount)];
            ws.GetBalance(sender);
            ws.SubtractFromBalance(sender, 1, _spec);
            ws.IncrementNonce(sender);

            // Contract execution: 8 reads, 2 writes per tx (typical SLOAD/SSTORE ratio)
            int baseSlot = rand.Next(SlotCount - 10);
            for (int r = 0; r < 8; r++)
            {
                ws.Get(new StorageCell(_slots[(baseSlot + r) % SlotCount].Account,
                    _slots[(baseSlot + r) % SlotCount].Slot));
            }
            for (int w = 0; w < 2; w++)
            {
                rand.NextBytes(buf);
                (Address account, UInt256 slot) = _slots[(baseSlot + 8 + w) % SlotCount];
                ws.Set(new StorageCell(account, slot), buf);
            }
        }

        ws.Commit(_spec);
        ws.CommitTree(1);
        ws.Reset();
    }

    /// <summary>
    /// Measures Commit overhead with many reads and very few writes.
    /// In the old code, all reads create JustCache entries that Commit iterates.
    /// With read-cache, only the writes appear in _changes.
    /// </summary>
    [Benchmark]
    public void CommitWithManyReadsAndFewWrites()
    {
        Random rand = new(1);
        IWorldState ws = _globalWorldState;
        using IDisposable _ = ws.BeginScope(_baseBlock);

        // 500 account reads
        for (int i = 0; i < 500; i++)
        {
            ws.GetBalance(_accounts[rand.Next(AccountCount)]);
        }

        // 500 storage reads
        for (int i = 0; i < 500; i++)
        {
            (Address account, UInt256 slot) = _slots[rand.Next(SlotCount)];
            ws.Get(new StorageCell(account, slot));
        }

        // Only 10 writes
        for (int i = 0; i < 10; i++)
        {
            ws.AddToBalance(_accounts[rand.Next(AccountCount)], 1, _spec);
        }

        ws.Commit(_spec);
        ws.CommitTree(1);
        ws.Reset();
    }

    /// <summary>
    /// Measures multi-transaction pattern with snapshot/restore.
    /// Each tx takes a snapshot, does reads+writes, then commits.
    /// Some transactions revert (restore to snapshot), simulating failed txs.
    /// </summary>
    [Benchmark]
    public void MultiTxWithSnapshotRestore()
    {
        Random rand = new(1);
        IWorldState ws = _globalWorldState;
        using IDisposable _ = ws.BeginScope(_baseBlock);
        byte[] buf = new byte[32];

        for (int tx = 0; tx < 100; tx++)
        {
            Snapshot snap = ws.TakeSnapshot(newTransactionStart: true);

            Address sender = _accounts[rand.Next(AccountCount)];
            Address recipient = _accounts[rand.Next(AccountCount)];
            ws.GetBalance(sender);
            ws.SubtractFromBalance(sender, 1, _spec);
            ws.IncrementNonce(sender);
            ws.AddToBalance(recipient, 1, _spec);

            // 4 storage reads + 1 write per tx
            int baseSlot = rand.Next(SlotCount - 5);
            for (int r = 0; r < 4; r++)
            {
                (Address acct, UInt256 slot) = _slots[(baseSlot + r) % SlotCount];
                ws.Get(new StorageCell(acct, slot));
            }
            rand.NextBytes(buf);
            (Address wAcct, UInt256 wSlot) = _slots[(baseSlot + 4) % SlotCount];
            ws.Set(new StorageCell(wAcct, wSlot), buf);

            // 10% of transactions revert
            if (rand.NextDouble() < 0.1)
            {
                ws.Restore(snap);
            }
            else
            {
                ws.Commit(_spec, commitRoots: false);
            }
        }

        // Final commit with roots
        ws.Commit(_spec, commitRoots: true);
        ws.CommitTree(1);
        ws.Reset();
    }
}
