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
/// Tests for <see cref="BlockAccessListBasedWorldState"/> — verifies that state reads
/// are served from the suggested <see cref="ReadOnlyBlockAccessList"/> and that missing entries throw.
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
        IDisposable scope = bws.BeginScope(baseBlock);
        return (bws, scope);
    }

    [Test]
    public void GetBalance_ReturnsValueFromSuggestedBal()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 100))
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
                .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 100), new BalanceChange(0, 200))
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
                .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0), new NonceChange(0, 3))
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
                .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 0))
                .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0))
                .WithCodeChanges(new CodeChange(Eip7928Constants.PrestateIndex, []), new CodeChange(0, priorTxCode))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal);
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
    public void AccountExists_WithOnlyPrestateSentinelAndCurrentTxChanges_ReturnsFalseBeforeCurrentTx()
    {
        byte[] currentTxCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 0))
            .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0), new NonceChange(23, 1))
            .WithCodeChanges(new CodeChange(Eip7928Constants.PrestateIndex, []), new CodeChange(23, currentTxCode))
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
            .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 0))
            .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0), new NonceChange(23, 1))
            .WithCodeChanges(new CodeChange(Eip7928Constants.PrestateIndex, []), new CodeChange(23, priorTxCode))
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
    public void AccountExists_ExistedBeforeBlock_ReturnsTrue()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0))
            .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 1))
            .TestObject;
        ac.SetExistedBeforeBlock(true);
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
        // calling AccountExists(1) must return false even though prestate is loaded at PrestateIndex.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(Eip7928Constants.PrestateIndex, 0), new BalanceChange(2, 100))
            .WithNonceChanges(new NonceChange(Eip7928Constants.PrestateIndex, 0))
            .TestObject;
        // ExistedBeforeBlock = false (default) — account did not exist at start of block.
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
