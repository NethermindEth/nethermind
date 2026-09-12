// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

/// <summary>
/// Parity tests for the journal-bypassing bulk BAL applier
/// (<see cref="IBalBulkWorldState.BulkApplyBal"/>, gated by <c>Blocks.ParallelBalBulkApply</c>):
/// for the same parent state and BAL, the bulk path must produce the same post-block state root
/// as the journaled replay (<see cref="BlockAccessListManager.ApplyStateChanges"/>) — on both the
/// flat and the trie scope providers — and must keep the block-level account-change tracking
/// (TxPool cache invalidation) populated.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public class BalBulkApplyTests(bool useFlat)
{
    private static readonly Address ContractAddress = TestItem.AddressB;
    private static readonly byte[] ContractCode = [0x60, 0x2A, 0x60, 0x00, 0x55];

    private static IEnumerable<TestCaseData> Scenarios()
    {
        yield return new TestCaseData(
            null,
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 25))
                    .WithNonceChanges(new NonceChange(1, 1))
                    .TestObject)
                .TestObject)
            .SetName("{m}(created account)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100)),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 150))
                    .TestObject)
                .TestObject)
            .SetName("{m}(balance only)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100)),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithNonceChanges(new NonceChange(1, 7))
                    .TestObject)
                .TestObject)
            .SetName("{m}(nonce only)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider =>
            {
                stateProvider.CreateAccount(ContractAddress, 1, 1);
                stateProvider.InsertCode(ContractAddress, ContractCode, Amsterdam.Instance);
                stateProvider.Set(new StorageCell(ContractAddress, 1), [0x2A]);
                stateProvider.Set(new StorageCell(ContractAddress, 2), [0x0B]);
            }),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(ContractAddress)
                    .WithStorageChanges(1, new StorageChange(1, 0x99u)) // overwrite
                    .WithStorageChanges(2, new StorageChange(1, 0x00u)) // zero out => delete
                    .WithStorageChanges(3, new StorageChange(1, 0x07u)) // create
                    .TestObject)
                .TestObject)
            .SetName("{m}(storage only: overwrite, zero out, create)");

        yield return new TestCaseData(
            null,
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(ContractAddress)
                    .WithNonceChanges(new NonceChange(1, 1))
                    .WithCodeChanges(new CodeChange(1, ContractCode))
                    .WithStorageChanges(1, new StorageChange(1, 0x2Au))
                    .TestObject)
                .TestObject)
            .SetName("{m}(code deploy with storage)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider =>
            {
                stateProvider.CreateAccount(TestItem.AddressA, 100);
                stateProvider.CreateAccount(TestItem.AddressC, 7); // survives, so the root is not the empty root
            }),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, UInt256.Zero))
                    .TestObject)
                .TestObject)
            .SetName("{m}(eip158 swept account)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider =>
            {
                stateProvider.CreateAccount(TestItem.AddressA, 100);
                stateProvider.Set(new StorageCell(TestItem.AddressA, 1), [0x2A]);
                stateProvider.CreateAccount(TestItem.AddressC, 7);
            }),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, UInt256.Zero))
                    .TestObject)
                .TestObject)
            .SetName("{m}(eip158 swept account with storage)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider =>
            {
                stateProvider.CreateAccount(TestItem.AddressA, 100);
                stateProvider.CreateAccount(ContractAddress, 1, 1);
                stateProvider.InsertCode(ContractAddress, ContractCode, Amsterdam.Instance);
                stateProvider.Set(new StorageCell(ContractAddress, 1), [0x2A]);
            }),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 60))
                    .WithNonceChanges(new NonceChange(1, 1))
                    .TestObject)
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(ContractAddress)
                    .WithBalanceChanges(new BalanceChange(2, 40))
                    .WithStorageChanges(1, new StorageChange(2, 0x99u))
                    .WithStorageChanges(5, new StorageChange(2, 0x05u))
                    .TestObject)
                .TestObject)
            .SetName("{m}(multiple accounts mixed)");

        yield return new TestCaseData(
            (Action<IWorldState>)(static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100)),
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads((UInt256)1)
                    .TestObject)
                .TestObject)
            .SetName("{m}(reads only row)");
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Bulk_apply_produces_the_journaled_root(Action<IWorldState>? genesisSetup, ReadOnlyBlockAccessList bal)
    {
        Hash256 journaledRoot = Apply(genesisSetup, bal, useBulk: false, out ArrayPoolList<AddressAsKey>? journaledChanges);
        Hash256 bulkRoot = Apply(genesisSetup, bal, useBulk: true, out ArrayPoolList<AddressAsKey>? bulkChanges);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulkRoot, Is.EqualTo(journaledRoot), "bulk-applied state root must equal the journaled one");
            Assert.That(
                AsSet(bulkChanges),
                Is.EquivalentTo(AsSet(journaledChanges)),
                "block-level account-change tracking (TxPool cache invalidation) must match the journaled path");
        }

        journaledChanges?.Dispose();
        bulkChanges?.Dispose();
    }

    [Test]
    public void Bulk_apply_preserves_pending_pre_block_writes()
    {
        // Mirrors AuRa post-merge preprocessing: system accounts are materialised on the MAIN
        // state (journaled, uncommitted) before the BAL apply runs. The bulk path must flush them
        // first — both so they survive (when not in the BAL) and so BAL parents read correctly.
        Action<IWorldState> genesisSetup = static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100);
        Action<IWorldState> preBlockWrites = static stateProvider =>
        {
            stateProvider.CreateAccount(TestItem.AddressD, 5); // not in the BAL — must survive the bulk apply
            stateProvider.CreateAccount(ContractAddress, 1);   // in the BAL — the BAL's final values win
        };
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(1, 150))
                .TestObject)
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(ContractAddress)
                .WithNonceChanges(new NonceChange(1, 1))
                .WithStorageChanges(1, new StorageChange(1, 0x2Au))
                .TestObject)
            .TestObject;

        Hash256 journaledRoot = Apply(genesisSetup, bal, useBulk: false, out ArrayPoolList<AddressAsKey>? journaledChanges, preBlockWrites);
        Hash256 bulkRoot = Apply(genesisSetup, bal, useBulk: true, out ArrayPoolList<AddressAsKey>? bulkChanges, preBlockWrites);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bulkRoot, Is.EqualTo(journaledRoot));
            Assert.That(AsSet(bulkChanges), Is.EquivalentTo(AsSet(journaledChanges)));
        }

        journaledChanges?.Dispose();
        bulkChanges?.Dispose();
    }

    [Test]
    public void Manager_dispatches_to_bulk_apply_when_configured()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(1, 60))
                .WithNonceChanges(new NonceChange(1, 1))
                .TestObject)
            .TestObject;
        Action<IWorldState> genesisSetup = static stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100);

        Hash256 journaledRoot = Apply(genesisSetup, bal, useBulk: false, out ArrayPoolList<AddressAsKey>? journaledChanges);
        journaledChanges?.Dispose();

        using Context ctx = new(useFlat);
        WorldState worldState = new(ctx.ScopeProvider, LimboLogs.Instance);
        Hash256 parentRoot = CommitGenesis(worldState, genesisSetup);
        using BlockAccessListManager manager = new(
            worldState,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true, ParallelBalBulkApply = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(
                NSubstitute.Substitute.For<IBlockhashProvider>(),
                new TestSingleReleaseSpecProvider(Amsterdam.Instance),
                LimboLogs.Instance,
                static ws => new EthereumCodeInfoRepository(ws)));

        using IDisposable scope = worldState.BeginScope(ParentHeader(parentRoot));
        manager.ApplyBlockStateChanges(bal, worldState, Amsterdam.Instance, shouldComputeStateRoot: true);

        Assert.That(worldState.StateRoot, Is.EqualTo(journaledRoot));
    }

    private Hash256 Apply(
        Action<IWorldState>? genesisSetup,
        ReadOnlyBlockAccessList bal,
        bool useBulk,
        out ArrayPoolList<AddressAsKey>? accountChanges,
        Action<IWorldState>? preBlockWrites = null)
    {
        using Context ctx = new(useFlat);
        IWorldState worldState = new WorldState(ctx.ScopeProvider, LimboLogs.Instance);
        Hash256 parentRoot = CommitGenesis(worldState, genesisSetup);

        using IDisposable scope = worldState.BeginScope(ParentHeader(parentRoot));
        preBlockWrites?.Invoke(worldState);
        if (useBulk)
        {
            // Mirrors BlockAccessListManager.ApplyBlockStateChanges: pending journal writes are
            // committed before the bulk batch bypasses the journal.
            worldState.Commit(Amsterdam.Instance);
            ((IBalBulkWorldState)worldState).BulkApplyBal(bal, Amsterdam.Instance);
            worldState.RecalculateStateRoot();
        }
        else
        {
            BlockAccessListManager.ApplyStateChanges(bal, worldState, Amsterdam.Instance, shouldComputeStateRoot: true);
        }

        accountChanges = worldState.GetAccountChanges();
        return worldState.StateRoot;
    }

    private static Hash256 CommitGenesis(IWorldState worldState, Action<IWorldState>? genesisSetup)
    {
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(worldState);
            worldState.Commit(Amsterdam.Instance, isGenesis: true);
            worldState.CommitTree(0);
            return worldState.StateRoot;
        }
    }

    private static BlockHeader ParentHeader(Hash256 parentRoot) =>
        Build.A.BlockHeader.WithNumber(0).WithStateRoot(parentRoot).TestObject;

    private static HashSet<AddressAsKey> AsSet(ArrayPoolList<AddressAsKey>? changes)
    {
        HashSet<AddressAsKey> result = [];
        if (changes is not null)
        {
            foreach (AddressAsKey address in changes.AsSpan())
            {
                result.Add(address);
            }
        }

        return result;
    }

    private sealed class Context : IDisposable
    {
        public IWorldStateScopeProvider ScopeProvider { get; }
        private readonly IContainer? _container;

        public Context(bool useFlat)
        {
            if (useFlat)
            {
                (ScopeProvider, _container) = TestWorldStateFactory.CreateFlatScopeProvider();
            }
            else
            {
                ScopeProvider = new TrieStoreScopeProvider(new TestRawTrieStore(new TestMemDb()), new TestMemDb(), LimboLogs.Instance);
            }
        }

        public void Dispose() => _container?.Dispose();
    }
}
