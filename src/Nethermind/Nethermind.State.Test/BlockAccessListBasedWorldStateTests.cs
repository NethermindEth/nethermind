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
/// are served from the suggested <see cref="BlockAccessList"/> and that missing entries throw.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockAccessListBasedWorldStateTests
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;
    private static readonly ILogManager Logger = LimboLogs.Instance;

    /// <summary>
    /// Creates a <see cref="BlockAccessListBasedWorldState"/> backed by a real inner state,
    /// with a suggested <see cref="BlockAccessList"/> populated via <paramref name="balSetup"/>.
    /// The inner state is initialized via <paramref name="genesisSetup"/>.
    /// </summary>
    private static AccountChanges AddAccountRead(BlockAccessList bal, Address address)
    {
        bal.AddAccountRead(address);
        return bal.GetAccountChanges(address)!;
    }

    private static (BlockAccessListBasedWorldState bws, IDisposable scope) CreateBlockAccessListState(
        int blockAccessIndex,
        Action<BlockAccessList> balSetup,
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
        BlockAccessList suggestedBal = new();
        balSetup(suggestedBal);

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
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal =>
                AddAccountRead(bal, TestItem.AddressA)
                    .AddBalanceChange(new BalanceChange(-1, 100)),
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void GetBalance_WithPriorTxChange_ReturnsUpdatedBalance()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges ac = AddAccountRead(bal, TestItem.AddressA);
                ac.AddBalanceChange(new BalanceChange(-1, 100));
                ac.AddBalanceChange(new BalanceChange(0, 200));
            },
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)200));
        }
    }

    [Test]
    public void GetNonce_WithPriorTxChange_ReturnsUpdatedNonce()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges ac = AddAccountRead(bal, TestItem.AddressA);
                ac.AddNonceChange(new NonceChange(-1, 0));
                ac.AddNonceChange(new NonceChange(0, 3));
            },
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
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
                AddAccountRead(bal, TestItem.AddressA)
                    .AddCodeChange(new CodeChange(0, priorTxCode)),
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            Assert.That(bws.GetCode(TestItem.AddressA), Is.EquivalentTo(priorTxCode));
        }
    }

    [Test]
    public void GetStorage_WithPriorTxChange_ReturnsPriorTxValue()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges ac = AddAccountRead(bal, TestItem.AddressA);
                bal.AddStorageRead(cell);
                ac.GetOrAddSlotChanges(cell.Index)
                    .AddStorageChange(new StorageChange(0, 99u));
            },
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
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal =>
            {
                // The account exists if we can resolve it in the BAL
                AccountChanges ac = AddAccountRead(bal, TestItem.AddressA);
                ac.AddNonceChange(new NonceChange(-1, 0));
                ac.AddBalanceChange(new BalanceChange(-1, 1));
                ac.ExistedBeforeBlock = true;
            },
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 1));
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void AccountExists_OnlyPrestateEntryAndNotExistedBeforeBlock_ReturnsFalse()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges ac = AddAccountRead(bal, TestItem.AddressA);
                ac.AddNonceChange(new NonceChange(-1, 0));
                ac.AddBalanceChange(new BalanceChange(-1, 0));
                ac.ExistedBeforeBlock = false;
            });
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void GetBalance_AddressNotInAccessList_Throws()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: _ => { }); // empty BAL
        using (scope)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() =>
                bws.GetBalance(TestItem.AddressB));
        }
    }
}
