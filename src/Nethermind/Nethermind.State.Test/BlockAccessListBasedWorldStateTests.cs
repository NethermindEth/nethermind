// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Tests for <see cref="BlockAccessListBasedWorldState"/>. Reads first try the suggested
/// <see cref="ReadOnlyBlockAccessList"/>; when the BAL doesn't carry an entry at the current
/// block-access index, they fall through to the attached parent-state reader. Reads for an
/// account that isn't declared in the BAL at all throw
/// <see cref="BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockAccessListBasedWorldStateTests
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;
    private static readonly ILogManager Logger = LimboLogs.Instance;

    private static (BlockAccessListBasedWorldState bws, IDisposable scope) CreateBlockAccessListState(
        uint blockAccessIndex,
        ReadOnlyBlockAccessList suggestedBal,
        Action<IWorldState>? genesisSetup = null)
    {
        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(inner);
            inner.Commit(Spec, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;

        BlockAccessListBasedWorldState bws = new(inner, Logger);
        bws.SetBlockAccessIndex(blockAccessIndex);
        Block block = Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(suggestedBal).TestObject;
        bws.Setup(block);
        IDisposable scope = inner.BeginScope(baseBlock);
        // The inner world state, scoped against the genesis root, is itself a valid parent reader
        // — reads against it answer pre-block state directly from the trie.
        bws.SetParentReader(inner);
        return (bws, scope);
    }

    [Test]
    public void GetBalance_FallsThroughToParentReader_WhenBalHasNoEntry()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void GetBalance_WithPriorTxChange_ReturnsUpdatedBalance()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 200))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)200));
        }
    }

    [Test]
    public void GetNonce_WithPriorTxChange_ReturnsUpdatedNonce()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithNonceChanges(new NonceChange(0, 3))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            Assert.That(bws.GetNonce(TestItem.AddressA), Is.EqualTo((UInt256)3));
        }
    }

    [Test]
    public void GetCode_WithPriorTxChange_ReturnsPriorTxCode()
    {
        byte[] priorTxCode = [0xAA, 0xBB];
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithCodeChanges(new CodeChange(0, priorTxCode))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            Assert.That(bws.GetCode(TestItem.AddressA), Is.EquivalentTo(priorTxCode));
        }
    }

    [Test]
    public void TryGetAccount_WithPriorCodeChange_ReturnsExistingAccount()
    {
        byte[] priorTxCode = [0xAA, 0xBB];
        ValueHash256 expectedCodeHash = ValueKeccak.Compute(priorTxCode);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithCodeChanges(new CodeChange(0, priorTxCode))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            bool exists = bws.TryGetAccount(TestItem.AddressA, out AccountStruct account);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exists, Is.True);
                Assert.That(account.CodeHash, Is.EqualTo(expectedCodeHash));
                Assert.That(account.HasCode, Is.True);
            }
        }
    }

    [Test]
    public void AccountExists_AccountCreatedByCurrentTxOnly_ReturnsFalseBeforeCurrentTx()
    {
        // Account is first touched at tx index 23. Before that index, the BAL has no entry
        // *and* the parent state has no account (genesisSetup not invoked), so AccountExists
        // must be false.
        byte[] currentTxCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithNonceChanges(new NonceChange(23, 1))
            .WithCodeChanges(new CodeChange(23, currentTxCode))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 23,
            suggestedBal: bal);

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void AccountExists_WithPriorTxCodeChange_ReturnsTrue()
    {
        byte[] priorTxCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithNonceChanges(new NonceChange(23, 1))
            .WithCodeChanges(new CodeChange(23, priorTxCode))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 24,
            suggestedBal: bal);

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void GetStorage_WithPriorTxChange_ReturnsPriorTxValue()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageChanges(cell.Index, new StorageChange(0, 99u))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            ReadOnlySpan<byte> retrieved = bws.Get(cell);
            Assert.That(new UInt256(retrieved, isBigEndian: true), Is.EqualTo((UInt256)99));
        }
    }

    [Test]
    public void GetStorage_DeclaredRead_FallsThroughToParentReader()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(cell.Index)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x42]);
                ws.Commit(Spec);
            });
        using (scope)
        {
            ReadOnlySpan<byte> retrieved = bws.Get(cell);
            Assert.That(retrieved.ToArray(), Is.EqualTo(new byte[] { 0x42 }));
        }
    }

    [Test]
    public void AccountExists_ExistedInParentState_ReturnsTrue()
    {
        // Account is declared in BAL but has no entries at index 0; existence comes from
        // the parent reader (set up via genesisSetup).
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 1));
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void AccountExists_CreatedAtLaterTx_ReturnsFalseForEarlierIndex()
    {
        // Account is first touched at tx 2 (balance change at index 2). For tx 1's world state,
        // calling AccountExists must return false: no entry in BAL strictly before index 1, and
        // the account doesn't exist in parent state.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(2, 100))
            .TestObject;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(ac).TestObject;

        (BlockAccessListBasedWorldState bwsAtIndex1, IDisposable scope1) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal);
        using (scope1)
        {
            Assert.That(bwsAtIndex1.AccountExists(TestItem.AddressA), Is.False);
        }

        (BlockAccessListBasedWorldState bwsAtIndex3, IDisposable scope3) = CreateBlockAccessListState(
            blockAccessIndex: 3,
            suggestedBal: bal);
        using (scope3)
        {
            Assert.That(bwsAtIndex3.AccountExists(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void GetBalance_AddressNotInAccessList_Throws()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: new ReadOnlyBlockAccessList());
        using (scope)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() =>
                bws.GetBalance(TestItem.AddressB));
        }
    }
}
