// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
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
/// and storage reads/writes into the generating <see cref="BlockAccessList"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TracedAccessWorldStateTests
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;
    private static readonly ILogManager Logger = LimboLogs.Instance;

    /// <summary>
    /// Creates a <see cref="TracedAccessWorldState"/> wrapping a real <see cref="WorldState"/>,
    /// with an optional genesis setup callback. Returns the traced state and a scope that must be disposed.
    /// </summary>
    private static (TracedAccessWorldState tws, IDisposable scope) CreateTracingState(
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
        TracedAccessWorldState tws = new(inner, parallel: false);
        IDisposable scope = tws.BeginScope(baseBlock);
        tws.SetIndex(0);
        return (tws, scope);
    }

    private static (TracedAccessWorldState tws, IDisposable scope) CreateParallelTracingState(
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

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(1).TestObject;
        BlockAccessList suggestedBal = new();
        balSetup(suggestedBal);

        BlockAccessListBasedWorldState balWorldState = new(inner, blockAccessIndex, Logger);
        Block block = Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(suggestedBal).TestObject;
        balWorldState.Setup(block);

        TracedAccessWorldState tws = new(balWorldState, parallel: true);
        IDisposable scope = tws.BeginScope(baseBlock);
        tws.SetIndex(blockAccessIndex);
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
                Assert.That(ac.BalanceChanges[0].PostBalance, Is.EqualTo((UInt256)expectedBalance));
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
                Assert.That(ac.NonceChanges[0].NewNonce, Is.EqualTo(expectedNonce));
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
                Assert.That(ac.CodeChanges[0].NewCode, Is.EquivalentTo(code));
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
                Assert.That(ac.StorageChanges[0].Slot, Is.EqualTo((UInt256)1));
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
            Assert.That(ac.StorageReads.First().Key, Is.EqualTo((UInt256)2));
        }
    }

    [Test]
    public void GetBalance_RecordsAccountRead()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 42));
        using (scope)
        {
            UInt256 balance = tws.GetBalance(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(balance, Is.EqualTo((UInt256)42));
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetNonce_RecordsAccountRead()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.IncrementNonce(TestItem.AddressA, 1, out _);
        });
        using (scope)
        {
            UInt256 nonce = tws.GetNonce(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(nonce, Is.EqualTo((UInt256)1));
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetCode_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x01];
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            byte[]? retrieved = tws.GetCode(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retrieved, Is.EquivalentTo(code));
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetCodeHash_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x02];
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            ValueHash256 hash = tws.GetCodeHash(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code)));
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void IsContract_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x03];
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            bool isContract = tws.IsContract(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(isContract, Is.True);
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [TestCase(false, TestName = "AccountExists")]
    [TestCase(true, TestName = "IsDeadAccount")]
    public void AccountExistsOrIsDeadAccount_RecordsAccountRead(bool checkDead)
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            bool result = checkDead
                ? tws.IsDeadAccount(TestItem.AddressA)
                : tws.AccountExists(TestItem.AddressA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(tws.GetGeneratingBlockAccessList().HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void ParallelValueCreatedAccount_IsTreatedAsExistingWithinTransaction()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateParallelTracingState(
            blockAccessIndex: 0,
            balSetup: bal =>
            {
                bal.AddAccountRead(TestItem.AddressB);
                AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressB)!;
                accountChanges.AddBalanceChange(new BalanceChange(-1, 0));
                accountChanges.EmptyBeforeBlock = true;
            });

        using (scope)
        {
            bool created = tws.AddToBalanceAndCreateIfNotExists(TestItem.AddressB, 1, Spec, out UInt256 oldBalance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(created, Is.True);
                Assert.That(oldBalance, Is.EqualTo(UInt256.Zero));
                Assert.That(tws.AccountExists(TestItem.AddressB), Is.True);
                Assert.That(tws.IsDeadAccount(TestItem.AddressB), Is.False);
            }
        }
    }

    [Test]
    public void ParallelZeroBalanceTransition_FallsBackToUnderlyingExistence()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateParallelTracingState(
            blockAccessIndex: 0,
            balSetup: bal =>
            {
                bal.AddAccountRead(TestItem.AddressA);
                AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressA)!;
                accountChanges.AddBalanceChange(new BalanceChange(-1, 1));
                accountChanges.AddNonceChange(new NonceChange(-1, 1));
                accountChanges.ExistedBeforeBlock = true;
            },
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 1);
                ws.IncrementNonce(TestItem.AddressA, 1, out _);
            });

        using (scope)
        {
            tws.SubtractFromBalance(TestItem.AddressA, 1, Spec, out UInt256 oldBalance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(oldBalance, Is.EqualTo(UInt256.One));
                Assert.That(tws.AccountExists(TestItem.AddressA), Is.True);
                Assert.That(tws.IsDeadAccount(TestItem.AddressA), Is.False);
            }
        }
    }

    [Test]
    public void ParallelEmptyCodeTransition_FallsBackToUnderlyingExistence()
    {
        byte[] existingCode = [0x60, 0x00];
        (TracedAccessWorldState tws, IDisposable scope) = CreateParallelTracingState(
            blockAccessIndex: 0,
            balSetup: bal =>
            {
                bal.AddAccountRead(TestItem.AddressA);
                AccountChanges accountChanges = bal.GetAccountChanges(TestItem.AddressA)!;
                accountChanges.AddBalanceChange(new BalanceChange(-1, 1));
                accountChanges.AddCodeChange(new CodeChange(-1, existingCode));
                accountChanges.ExistedBeforeBlock = true;
            },
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 1);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(existingCode), existingCode, Spec);
            });

        using (scope)
        {
            byte[] emptyCode = [];
            tws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(emptyCode), emptyCode, Spec);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tws.AccountExists(TestItem.AddressA), Is.True);
                Assert.That(tws.IsDeadAccount(TestItem.AddressA), Is.False);
                Assert.That(tws.GetCode(TestItem.AddressA), Is.Empty);
            }
        }
    }

    [Test]
    public void TryGetAccount_RecordsAccountRead()
    {
        (TracedAccessWorldState tws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 77));
        using (scope)
        {
            bool found = tws.TryGetAccount(TestItem.AddressA, out AccountStruct account);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(account.Balance, Is.EqualTo((UInt256)77));
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
                Assert.That(ac.BalanceChanges[0].PostBalance, Is.EqualTo((UInt256)balance));
            }
            if (expectedNonceChanges > 0)
            {
                Assert.That(ac.NonceChanges[0].NewNonce, Is.EqualTo((ulong)nonce));
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
                Assert.That(ac.BalanceChanges[0].PostBalance, Is.EqualTo(UInt256.Zero));
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
}
