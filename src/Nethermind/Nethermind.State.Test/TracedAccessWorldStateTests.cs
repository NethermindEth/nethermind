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
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// Tests for <see cref="TracedAccessWorldState"/> — verifies that state operations
/// correctly record account reads, balance changes, nonce changes, code changes,
/// and storage reads/writes into the generating <see cref="BlockAccessList"/>.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class TracedAccessWorldStateTests(bool parallel)
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;

    /// <summary>
    /// Creates a <see cref="TracedAccessWorldState"/> wrapping a real <see cref="WorldState"/>,
    /// with an optional genesis setup callback. Returns the traced state and a scope that must be disposed.
    /// </summary>
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.BalanceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.BalanceChanges[0].Value, Is.EqualTo((UInt256)expectedBalance));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.NonceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.NonceChanges[0].Value, Is.EqualTo(expectedNonce));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.CodeChanges, Has.Count.EqualTo(1));
                Assert.That(ac.CodeChanges[0].Code, Is.EquivalentTo(code));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.StorageChanges, Has.Count.EqualTo(1));
                Assert.That(ac.StorageChanges[0].Key, Is.EqualTo((UInt256)1));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
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
                Assert.That(tws.GetNonce(TestItem.AddressA), Is.EqualTo((UInt256)1))))
            .SetName("GetNonce_RecordsAccountRead");

        yield return new TestCaseData(
            (Action<IWorldState>)(ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(codeForCodeOp), codeForCodeOp, Spec);
            }),
            (Action<TracedAccessWorldState>)(tws =>
                Assert.That(tws.GetCode(TestItem.AddressA), Is.EquivalentTo(codeForCodeOp))))
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
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.BalanceChanges, Has.Count.EqualTo(expectedBalChanges));
                Assert.That(ac.NonceChanges, Has.Count.EqualTo(expectedNonceChanges));
            }

            if (expectedBalChanges > 0)
            {
                Assert.That(ac.BalanceChanges[0].Value, Is.EqualTo((UInt256)balance));
            }
            if (expectedNonceChanges > 0)
            {
                Assert.That(ac.NonceChanges[0].Value, Is.EqualTo((ulong)nonce));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            Assert.That(ac, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac!.BalanceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.BalanceChanges[0].Value, Is.EqualTo(UInt256.Zero));
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

            Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac!.BalanceChanges, Is.Empty);
                Assert.That(ac.NonceChanges, Is.Empty);
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

            Assert.That(tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA)!
                .BalanceChanges, Has.Count.EqualTo(1));

            tws.Restore(snap);

            // Balance change must be rolled back by the snapshot restore.
            Assert.That(tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA)!
                .BalanceChanges, Is.Empty);
        }
    }

    [Test]
    public void SubtractFromBalance_DoesNotRecordSystemUserZeroChange()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState();
        using (scope)
        {
            tws.SubtractFromBalance(Address.SystemUser, 0u, Spec, out _);

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(Address.SystemUser);
            Assert.That(ac, Is.Null);
        }
    }

    // ── C1 regression: within a single tx, balance/nonce/code changes are replaced
    // via Pop+Push, keeping Count == 1. Verify the replacement uses BAL state, not inner. ──

    [Test]
    public void RepeatedBalanceChanges_SameTx_UsesLatestBalValue()
    {
        // Account starts at 1000. Two AddToBalance(100) calls in the same tx:
        //   1st: old=1000 (from inner), new=1100 → recorded at Index 0
        //   2nd: old=1100 (from BAL Count==1 entry), new=1200 → replaces at Index 0
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 1000));
        using (scope)
        {
            tws.AddToBalance(TestItem.AddressA, 100, Spec, out UInt256 oldBalance1);
            tws.AddToBalance(TestItem.AddressA, 100, Spec, out UInt256 oldBalance2);

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(oldBalance1, Is.EqualTo((UInt256)1000), "first old balance from inner state");
                Assert.That(oldBalance2, Is.EqualTo((UInt256)1100), "second old balance from BAL, not inner");
                Assert.That(ac, Is.Not.Null);
                // Within same tx, Pop+Push replaces entry: Count stays 1
                Assert.That(ac!.BalanceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.BalanceChanges[0].Value, Is.EqualTo((UInt256)1200));
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
            tws.IncrementNonce(TestItem.AddressA, 1, out UInt256 oldNonce1);
            tws.IncrementNonce(TestItem.AddressA, 1, out UInt256 oldNonce2);

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(oldNonce1, Is.EqualTo((UInt256)0), "first old nonce from inner");
                Assert.That(oldNonce2, Is.EqualTo((UInt256)1), "second old nonce from BAL");
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.NonceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.NonceChanges[0].Value, Is.EqualTo(2ul));
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
            // Second InsertCode should see code1 as old code (from BAL), not empty (from inner)
            tws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code2), code2, Spec);

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                // CodeChange doesn't Pop+Push, so both entries accumulate
                Assert.That(ac!.CodeChanges.Count, Is.GreaterThanOrEqualTo(1));
                Assert.That(ac.CodeChanges[ac.CodeChanges.Count - 1].Code, Is.EquivalentTo(code2));
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

            AccountChanges? ac = tws.GetGeneratingBlockAccessList().GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.StorageChanges, Has.Count.EqualTo(1));
                SlotChanges slotChanges = ac.StorageChanges[0];
                // Storage changes also Pop+Push at same Index, so Count stays 1
                Assert.That(slotChanges.Changes, Has.Count.EqualTo(1));
                Assert.That(slotChanges.Changes.Values[0].Value, Is.EqualTo((UInt256)2));
            }
        }
    }

    // ── C7 regression: GetBalance/GetNonce return null, not UInt256.MaxValue sentinel ──

    [Test]
    public void AccountChanges_GetBalance_ReturnsNull_WhenNoChangesExist()
    {
        AccountChanges ac = new(TestItem.AddressA);
        UInt256? balance = ac.GetBalance(0);
        Assert.That(balance, Is.Null, "GetBalance should return null when no balance changes exist, not UInt256.MaxValue");
    }

    [Test]
    public void AccountChanges_GetNonce_ReturnsNull_WhenNoChangesExist()
    {
        AccountChanges ac = new(TestItem.AddressA);
        UInt256? nonce = ac.GetNonce(0);
        Assert.That(nonce, Is.Null, "GetNonce should return null when no nonce changes exist, not UInt256.MaxValue");
    }

    [Test]
    public void AccountChanges_GetBalance_ReturnsPreBlockValue_WhenOnlyPreStateExists()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(-1, 500));
        UInt256? balance = ac.GetBalance(0);
        Assert.That(balance, Is.EqualTo((UInt256)500));
    }

    [Test]
    public void AccountChanges_GetNonce_ReturnsPreBlockValue_WhenOnlyPreStateExists()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddNonceChange(new NonceChange(-1, 3));
        UInt256? nonce = ac.GetNonce(0);
        Assert.That(nonce, Is.EqualTo((UInt256)3));
    }
}
