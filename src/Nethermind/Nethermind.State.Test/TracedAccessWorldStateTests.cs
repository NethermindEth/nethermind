// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
/// Tests for <see cref="TracedAccessWorldState"/> — verifies that state operations
/// correctly record account reads, balance changes, nonce changes, code changes,
/// and storage reads/writes into the generating <see cref="BlockAccessListAtIndex"/>.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class TracedAccessWorldStateTests(bool parallel)
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;

    private (TracedAccessWorldState tws, IDisposable scope) CreateTracingState(
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
        TracedAccessWorldState tws = new(inner, parallel: parallel);
        tws.SetGeneratingBlockAccessList(new());
        IDisposable scope = tws.BeginScope(baseBlock);
        tws.SetIndex(0);
        return (tws, scope);
    }

    [TestCase(true, 50u, 100u, 150u, TestName = "AddToBalance")]
    [TestCase(false, 30u, 100u, 70u, TestName = "SubtractFromBalance")]
    public void BalanceOp_RecordsBalanceChange(
        bool isAdd, uint delta, uint initialBalance, uint expectedBalance)
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, initialBalance));
        using (scope)
        {
            if (isAdd)
            {
                tws.AddToBalance(TestItem.AddressA, delta, Spec, out _);
            }
            else
            {
                tws.SubtractFromBalance(TestItem.AddressA, delta, Spec, out _);
            }

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.BalanceChange, Is.Not.Null);
                Assert.That(ac.BalanceChange!.Value.Value, Is.EqualTo((UInt256)expectedBalance));
            }
        }
    }

    [TestCase(true, 1ul, 1ul, TestName = "IncrementNonce")]
    [TestCase(false, 5ul, 5ul, TestName = "SetNonce")]
    public void NonceOp_RecordsNonceChange(
        bool isIncrement, ulong value, ulong expectedNonce)
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            if (isIncrement)
            {
                tws.IncrementNonce(TestItem.AddressA, value, out _);
            }
            else
            {
                tws.SetNonce(TestItem.AddressA, value);
            }

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.NonceChange, Is.Not.Null);
                Assert.That(ac.NonceChange!.Value.Value, Is.EqualTo(expectedNonce));
            }
        }
    }

    [Test]
    public void InsertCode_RecordsCodeChange()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            byte[] code = [0x60, 0x00];
            ValueHash256 codeHash = ValueKeccak.Compute(code);
            tws.InsertCode(TestItem.AddressA, codeHash, code, Spec);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.CodeChange, Is.Not.Null);
                Assert.That(ac.CodeChange!.Value.Code, Is.EqualTo(code));
            }
        }
    }

    [Test]
    public void TryGetAccount_UsesCurrentCodeChange_WhenInnerStateDoesNotMutateCode()
    {
        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.Commit(Spec, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;
        ReadOnlyAccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .TestObject;
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList.WithAccountChanges(accountChanges).TestObject;

        BlockAccessListBasedWorldState balWorldState = new(inner, LimboLogs.Instance);
        balWorldState.SetBlockAccessIndex(0);
        Block block = Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(suggestedBal).TestObject;
        balWorldState.Setup(block);
        // Reads not covered by the BAL fall through to the parent reader; in this fixture the
        // inner state itself (scoped against genesis) is the parent.
        balWorldState.SetParentReader(inner);

        TracedAccessWorldState tws = new(balWorldState, parallel: true);
        tws.SetGeneratingBlockAccessList(new());
        using (tws.BeginScope(baseBlock))
        {
            tws.SetIndex(0);
            byte[] code = [0x60, 0x00];
            ValueHash256 codeHash = ValueKeccak.Compute(code);
            tws.InsertCode(TestItem.AddressA, codeHash, code, Spec);

            bool exists = tws.TryGetAccount(TestItem.AddressA, out AccountStruct account);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exists, Is.True);
                Assert.That(account.CodeHash, Is.EqualTo(codeHash));
                Assert.That(account.HasCode, Is.True);
            }
        }
    }

    [Test]
    public void Set_RecordsStorageChange()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            tws.Set(cell, [0x01]);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.StorageChangeCount, Is.EqualTo(1));
                Assert.That(ac.ChangedSlots, Does.Contain((UInt256)1));
            }
        }
    }

    [TestCase(false, TestName = "Get_RecordsStorageRead")]
    [TestCase(true, TestName = "GetOriginal_RecordsStorageRead")]
    public void StorageRead_RecordsStorageRead(bool useGetOriginal)
    {
        StorageCell cell = new(TestItem.AddressA, 2);
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            _ = tws.Get(cell);
            if (useGetOriginal)
            {
                _ = tws.GetOriginal(cell);
            }

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            Assert.That(ac, Is.Not.Null);
            Assert.That(ac!.StorageReads, Has.Count.EqualTo(1));
            Assert.That(ac.StorageReads.First(), Is.EqualTo((UInt256)2));
        }
    }

    private static IEnumerable<TestCaseData> ReadRecordsAccountReadCases()
    {
        byte[] codeForCodeOp = [0x60, 0x01];
        byte[] codeForHashOp = [0x60, 0x02];
        byte[] codeForIsContractOp = [0x60, 0x03];

        yield return new TestCaseData(
            (Action<IWorldState>)(ws => ws.CreateAccount(TestItem.AddressA, 42)),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)42))))
            .SetName("GetBalance_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.IncrementNonce(TestItem.AddressA, 1, out _);
            }),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.GetNonce(TestItem.AddressA), Is.EqualTo(1UL))))
            .SetName("GetNonce_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(codeForCodeOp), codeForCodeOp, Spec);
            }),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.GetCode(TestItem.AddressA), Is.EqualTo(codeForCodeOp))))
            .SetName("GetCode_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(codeForHashOp), codeForHashOp, Spec);
            }),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.GetCodeHash(TestItem.AddressA), Is.EqualTo(ValueKeccak.Compute(codeForHashOp)))))
            .SetName("GetCodeHash_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(codeForIsContractOp), codeForIsContractOp, Spec);
            }),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.IsContract(TestItem.AddressA), Is.True)))
            .SetName("IsContract_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws => ws.CreateAccount(TestItem.AddressA, 0)),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.AccountExists(TestItem.AddressA), Is.True)))
            .SetName("AccountExists_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws => ws.CreateAccount(TestItem.AddressA, 0)),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.IsDeadAccount(TestItem.AddressA), Is.True)))
            .SetName("IsDeadAccount_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws => ws.CreateAccount(TestItem.AddressA, 77)),
            (Action<TracedAccessWorldState>)(tws =>
            {
                bool found = tws.TryGetAccount(TestItem.AddressA, out AccountStruct account);
                Assert.That(found, Is.True);
                Assert.That(account.Balance, Is.EqualTo((UInt256)77));
            }))
            .SetName("TryGetAccount_RecordsAccountRead");
    }

    [TestCaseSource(nameof(ReadRecordsAccountReadCases))]
    public void ReadOp_RecordsAccountRead(Action<IWorldState> setup, Action<TracedAccessWorldState> readAndAssert)
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(setup);
        using (scope)
        {
            using (Assert.EnterMultipleScope())
            {
                readAndAssert(tws);
                Assert.That(tws.GetGeneratingBlockAccessList()!.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [TestCase(200u, 5u, 1, 1, TestName = "NonZeroBalanceAndNonce")]
    [TestCase(0u, 0u, 0, 0, TestName = "ZeroBalanceAndNonce_OnlyAccountRead")]
    public void CreateAccount_RecordsChanges(
        uint balance, uint nonce, int expectedBalChanges, int expectedNonceChanges)
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState();
        using (scope)
        {
            tws.CreateAccount(TestItem.AddressA, balance, nonce);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.BalanceChange is null ? 0 : 1, Is.EqualTo(expectedBalChanges));
                Assert.That(ac.NonceChange is null ? 0 : 1, Is.EqualTo(expectedNonceChanges));
            }

            if (expectedBalChanges > 0)
            {
                Assert.That(ac!.BalanceChange!.Value.Value, Is.EqualTo((UInt256)balance));
            }
            if (expectedNonceChanges > 0)
            {
                Assert.That(ac!.NonceChange!.Value.Value, Is.EqualTo(nonce));
            }
        }
    }

    [Test]
    public void DeleteAccount_RecordsBalanceZeroed()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 50));
        using (scope)
        {
            tws.DeleteAccount(TestItem.AddressA);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            Assert.That(ac, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac!.BalanceChange, Is.Not.Null);
                Assert.That(ac.BalanceChange!.Value.Value, Is.EqualTo(UInt256.Zero));
            }
        }
    }

    [Test]
    public void AddAccountRead_AddsAccountToBAL()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            tws.AddAccountRead(TestItem.AddressA);

            Assert.That(tws.GetGeneratingBlockAccessList()!.HasAccount(TestItem.AddressA), Is.True);
            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac!.BalanceChange, Is.Null);
                Assert.That(ac.NonceChange, Is.Null);
            }
        }
    }

    [Test]
    public void TakeSnapshot_Restore_RollsBackBalanceChange()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Snapshot snap = tws.TakeSnapshot();
            tws.AddToBalance(TestItem.AddressA, 50, Spec, out _);

            Assert.That(tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA)!
                .BalanceChange, Is.Not.Null);

            tws.Restore(snap);

            // Balance change must be rolled back by the snapshot restore.
            Assert.That(tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA)!
                .BalanceChange, Is.Null);
        }
    }

    [Test]
    public void SubtractFromBalance_DoesNotRecordSystemUserZeroChange()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState();
        using (scope)
        {
            tws.SubtractFromBalance(Address.SystemUser, 0u, Spec, out _);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(Address.SystemUser);
            Assert.That(ac, Is.Null);
        }
    }

    // Repeated same-tx balance/nonce/code/storage writes must reuse the BAL-recorded value as
    // their "old" baseline (not the inner state) and collapse to a single entry at this index.

    [Test]
    public void RepeatedBalanceChanges_SameTx_UsesLatestBalValue()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 1000));
        using (scope)
        {
            tws.AddToBalance(TestItem.AddressA, 100, Spec, out UInt256 oldBalance1);
            tws.AddToBalance(TestItem.AddressA, 100, Spec, out UInt256 oldBalance2);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(oldBalance1, Is.EqualTo((UInt256)1000), "first old balance from inner state");
                Assert.That(oldBalance2, Is.EqualTo((UInt256)1100), "second old balance from BAL, not inner");
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.BalanceChange, Is.Not.Null);
                Assert.That(ac.BalanceChange!.Value.Value, Is.EqualTo((UInt256)1200));
            }
        }
    }

    [Test]
    public void RepeatedNonceChanges_SameTx_UsesLatestNonceValue()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            tws.IncrementNonce(TestItem.AddressA, 1, out ulong oldNonce1);
            tws.IncrementNonce(TestItem.AddressA, 1, out ulong oldNonce2);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(oldNonce1, Is.EqualTo(0UL), "first old nonce from inner");
                Assert.That(oldNonce2, Is.EqualTo(1UL), "second old nonce from BAL");
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.NonceChange, Is.Not.Null);
                Assert.That(ac.NonceChange!.Value.Value, Is.EqualTo(2ul));
            }
        }
    }

    [Test]
    public void RepeatedCodeChanges_SameTx_UsesLatestCode()
    {
        byte[] code1 = [0x60, 0x01];
        byte[] code2 = [0x60, 0x02];
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            tws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code1), code1, Spec);
            tws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code2), code2, Spec);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.CodeChange, Is.Not.Null);
                Assert.That(ac.CodeChange!.Value.Code, Is.EqualTo(code2));
            }
        }
    }

    [Test]
    public void RepeatedStorageWrites_SameTx_UsesLatestValue_InParallel()
    {
        if (!parallel) Assert.Ignore("Storage cache only used in parallel mode");

        StorageCell cell = new(TestItem.AddressA, 1);
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            tws.Set(cell, [0x01]);
            tws.Set(cell, [0x02]);

            AccountChangesAtIndex? ac = tws.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.StorageChangeCount, Is.EqualTo(1));
                Assert.That(ac.TryGetStorageChange((UInt256)1, out StorageChange? change), Is.True);
                // StorageChange.Value is EvmWord (BE wire form) — compare via the ctor's round-trip.
                Assert.That(change!.Value.Value, Is.EqualTo(new StorageChange(0, (UInt256)2).Value));
            }
        }
    }

    [Test]
    public void RepeatedStorageReadCache_IsInvalidatedByWrite()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.Set(cell, [0x01]);
        });

        using (scope)
        {
            Assert.That(new UInt256(tws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)1));
            Assert.That(new UInt256(tws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)1));

            tws.Set(cell, [0x02]);

            Assert.That(new UInt256(tws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)2));
        }
    }

}
