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

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ParallelWorldStateTests
{
    private static readonly IReleaseSpec Spec = Amsterdam.Instance;
    private static readonly ILogManager Logger = LimboLogs.Instance;

    public enum ParallelScenario { NoChanges, PriorTxChange, CurrentTxChange }

    private static ParallelWorldState CreateStateCore(
        bool parallel,
        Action<ParallelWorldState>? genesisSetup,
        out BlockHeader baseBlock)
    {
        ParallelWorldState pws = (ParallelWorldState)TestWorldStateFactory.CreateForTest(parallel: parallel);
        using (pws.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(pws);
            pws.Commit(Spec, isGenesis: true);
            pws.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(pws.StateRoot).WithNumber(0).TestObject;
        }
        pws.TracingEnabled = true;
        pws.IsGenesis = false;
        return pws;
    }

    private static (ParallelWorldState pws, IDisposable scope) CreateTracingState(
        Action<ParallelWorldState>? genesisSetup = null)
    {
        ParallelWorldState pws = CreateStateCore(parallel: false, genesisSetup, out BlockHeader baseBlock);
        return (pws, pws.BeginScope(baseBlock));
    }

    private static (ParallelWorldState pws, IDisposable scope) CreateParallelState(
        BlockAccessList suggestedBal,
        int txCount = 3,
        Action<ParallelWorldState>? genesisSetup = null)
    {
        ParallelWorldState pws = CreateStateCore(parallel: true, genesisSetup, out BlockHeader baseBlock);
        IDisposable scope = pws.BeginScope(baseBlock);
        pws.LoadSuggestedBlockAccessList(suggestedBal, 0);
        pws.SetupGeneratedAccessLists(Logger, txCount);
        return (pws, scope);
    }

    private static BlockAccessList BuildSuggestedBal(params Address[] addresses)
    {
        BlockAccessList bal = new();
        foreach (Address addr in addresses)
        {
            bal.AddAccountRead(addr);
        }
        return bal;
    }

    [TestCase(true, 50u, 100u, 150u, TestName = "AddToBalance")]
    [TestCase(false, 30u, 100u, 70u, TestName = "SubtractFromBalance")]
    public void BalanceOp_TracingEnabled_RecordsBalanceChange(
        bool isAdd, uint delta, uint initialBalance, uint expectedBalance)
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, initialBalance));
        using (scope)
        {
            if (isAdd)
            {
                pws.AddToBalance(TestItem.AddressA, delta, Spec, blockAccessIndex: 0);
            }
            else
            {
                pws.SubtractFromBalance(TestItem.AddressA, delta, Spec, blockAccessIndex: 0);
            }

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
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
    public void NonceOp_TracingEnabled_RecordsNonceChange(
        bool isIncrement, ulong value, ulong expectedNonce)
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            if (isIncrement)
            {
                pws.IncrementNonce(TestItem.AddressA, value, blockAccessIndex: 0);
            }
            else
            {
                pws.SetNonce(TestItem.AddressA, value, blockAccessIndex: 0);
            }

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.NonceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.NonceChanges[0].NewNonce, Is.EqualTo(expectedNonce));
            }
        }
    }

    [Test]
    public void InsertCode_TracingEnabled_RecordsCodeChange()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            byte[] code = [0x60, 0x00];
            ValueHash256 codeHash = ValueKeccak.Compute(code);
            pws.InsertCode(TestItem.AddressA, codeHash, code, Spec, blockAccessIndex: 0);

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac, Is.Not.Null);
                Assert.That(ac!.CodeChanges, Has.Count.EqualTo(1));
                Assert.That(ac.CodeChanges[0].NewCode, Is.EquivalentTo(code));
            }
        }
    }

    [Test]
    public void Set_TracingEnabled_RecordsStorageChange()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.Set(cell, [0x01], blockAccessIndex: 0);

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
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
    public void StorageRead_TracingEnabled_RecordsStorageRead(bool useGetOriginal)
    {
        StorageCell cell = new(TestItem.AddressA, 2);
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            // GetOriginal requires a prior Get call within the same caching round.
            _ = pws.Get(cell, blockAccessIndex: 0);
            if (useGetOriginal)
            {
                _ = pws.GetOriginal(cell, blockAccessIndex: 0);
            }

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
            Assert.That(ac, Is.Not.Null);
            Assert.That(ac!.StorageReads, Has.Count.EqualTo(1));
            Assert.That(ac!.StorageReads.First().Key, Is.EqualTo((UInt256)2));
        }
    }

    [Test]
    public void GetBalance_TracingEnabled_RecordsAccountRead()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 42));
        using (scope)
        {
            UInt256 balance = pws.GetBalance(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(balance, Is.EqualTo((UInt256)42));
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetNonce_TracingEnabled_RecordsAccountRead()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.IncrementNonce(TestItem.AddressA, 1);
        });
        using (scope)
        {
            UInt256 nonce = pws.GetNonce(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(nonce, Is.EqualTo((UInt256)1));
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetCode_TracingEnabled_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x01];
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            byte[]? retrieved = pws.GetCode(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retrieved, Is.EquivalentTo(code));
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void GetCodeHash_TracingEnabled_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x02];
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            ValueHash256 hash = pws.GetCodeHash(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(hash, Is.EqualTo(ValueKeccak.Compute(code)));
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void IsContract_TracingEnabled_RecordsAccountRead()
    {
        byte[] code = [0x60, 0x03];
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
        {
            ws.CreateAccount(TestItem.AddressA, 0);
            ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, Spec);
        });
        using (scope)
        {
            bool isContract = pws.IsContract(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(isContract, Is.True);
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [TestCase(false, TestName = "AccountExists")]
    [TestCase(true, TestName = "IsDeadAccount")]
    public void AccountExistsOrIsDeadAccount_TracingEnabled_RecordsAccountRead(bool checkDead)
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            bool result = checkDead
                ? pws.IsDeadAccount(TestItem.AddressA, blockAccessIndex: 0)
                : pws.AccountExists(TestItem.AddressA, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [Test]
    public void TryGetAccount_TracingEnabled_RecordsAccountRead()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 77));
        using (scope)
        {
            bool found = pws.TryGetAccount(TestItem.AddressA, out AccountStruct account, blockAccessIndex: 0);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(account.Balance, Is.EqualTo((UInt256)77));
                Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            }
        }
    }

    [TestCase(200u, 5u, 1, 1, TestName = "NonZeroBalanceAndNonce")]
    [TestCase(0u, 0u, 0, 0, TestName = "ZeroBalanceAndNonce_OnlyAccountRead")]
    public void CreateAccount_TracingEnabled_RecordsChanges(
        uint balance, uint nonce, int expectedBalChanges, int expectedNonceChanges)
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState();
        using (scope)
        {
            pws.CreateAccount(TestItem.AddressA, balance, nonce, blockAccessIndex: 0);

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
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
    public void DeleteAccount_TracingEnabled_RecordsBalanceZeroed()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 50));
        using (scope)
        {
            pws.DeleteAccount(TestItem.AddressA, blockAccessIndex: 0);

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
            Assert.That(ac, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ac!.BalanceChanges, Has.Count.EqualTo(1));
                Assert.That(ac.BalanceChanges[0].PostBalance, Is.EqualTo(UInt256.Zero));
            }
        }
    }

    [Test]
    public void AddAccountRead_TracingEnabled_AddsAccountToBAL()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.AddAccountRead(TestItem.AddressA, blockAccessIndex: 0);

            Assert.That(pws.GeneratedBlockAccessList.HasAccount(TestItem.AddressA), Is.True);
            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA);
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
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Snapshot snap = pws.TakeSnapshot(blockAccessIndex: 0);
            pws.AddToBalance(TestItem.AddressA, 50, Spec, blockAccessIndex: 0);

            Assert.That(pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA)!
                .BalanceChanges, Has.Count.EqualTo(1));

            pws.Restore(snap, blockAccessIndex: 0);

            // Balance change must be rolled back by the snapshot restore.
            Assert.That(pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA)!
                .BalanceChanges, Is.Empty);
        }
    }

    [Test]
    public void TracingEnabled_NullBlockAccessIndex_Throws()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            Assert.Throws<ArgumentNullException>(() =>
                pws.AddToBalance(TestItem.AddressA, 1, Spec));
            Assert.Throws<ArgumentNullException>(() =>
                pws.GetBalance(TestItem.AddressA));
            Assert.Throws<ArgumentNullException>(() =>
                pws.GetNonce(TestItem.AddressA));
        }
    }

    [TestCase(ParallelScenario.NoChanges, 0, 100u)]
    [TestCase(ParallelScenario.PriorTxChange, 1, 200u)]
    [TestCase(ParallelScenario.CurrentTxChange, 1, 150u)]
    public void GetBalance_ParallelMode_ReturnsCorrectBalance(
        ParallelScenario scenario, int txIndex, uint expectedBalance)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            switch (scenario)
            {
                case ParallelScenario.PriorTxChange:
                    suggested.GetAccountChanges(TestItem.AddressA)!
                        .AddBalanceChange(new BalanceChange(0, 200));
                    break;
                case ParallelScenario.CurrentTxChange:
                    pws.AddToBalance(TestItem.AddressA, 50, Spec, blockAccessIndex: 1);
                    break;
            }

            Assert.That(pws.GetBalance(TestItem.AddressA, blockAccessIndex: txIndex),
                Is.EqualTo((UInt256)expectedBalance));
        }
    }

    [TestCase(ParallelScenario.NoChanges, 0, 0ul)]
    [TestCase(ParallelScenario.PriorTxChange, 1, 3ul)]
    [TestCase(ParallelScenario.CurrentTxChange, 1, 1ul)]
    public void GetNonce_ParallelMode_ReturnsCorrectNonce(
        ParallelScenario scenario, int txIndex, ulong expectedNonce)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            switch (scenario)
            {
                case ParallelScenario.PriorTxChange:
                    suggested.GetAccountChanges(TestItem.AddressA)!
                        .AddNonceChange(new NonceChange(0, 3));
                    break;
                case ParallelScenario.CurrentTxChange:
                    pws.IncrementNonce(TestItem.AddressA, 1, blockAccessIndex: 1);
                    break;
            }

            Assert.That(pws.GetNonce(TestItem.AddressA, blockAccessIndex: txIndex),
                Is.EqualTo((UInt256)expectedNonce));
        }
    }

    private static IEnumerable<TestCaseData> GetCode_ParallelModeCases()
    {
        byte[] preBlockCode = [0xCC];
        byte[] priorTxCode = [0xAA, 0xBB];
        byte[] currentTxCode = [0x60, 0x01, 0x60, 0x02];

        yield return new TestCaseData(ParallelScenario.NoChanges, 0, preBlockCode, preBlockCode)
            .SetName("GetCode_ParallelMode_NoChanges_ReturnsPreBlockCode");
        yield return new TestCaseData(ParallelScenario.PriorTxChange, 1, null, priorTxCode)
            .SetName("GetCode_ParallelMode_PriorTxChange_ReturnsPriorTxCode");
        yield return new TestCaseData(ParallelScenario.CurrentTxChange, 1, null, currentTxCode)
            .SetName("GetCode_ParallelMode_CurrentTxChange_ReturnsNewCode");
    }

    [TestCaseSource(nameof(GetCode_ParallelModeCases))]
    public void GetCode_ParallelMode_ReturnsCorrectCode(
        ParallelScenario scenario, int txIndex, byte[]? genesisCode, byte[] expectedCode)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                if (genesisCode is not null)
                    ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(genesisCode), genesisCode, Spec);
            });
        using (scope)
        {
            switch (scenario)
            {
                case ParallelScenario.PriorTxChange:
                    suggested.GetAccountChanges(TestItem.AddressA)!
                        .AddCodeChange(new CodeChange(0, expectedCode));
                    break;
                case ParallelScenario.CurrentTxChange:
                    pws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(expectedCode), expectedCode, Spec,
                        isGenesis: false, blockAccessIndex: txIndex);
                    break;
            }

            Assert.That(pws.GetCode(TestItem.AddressA, blockAccessIndex: txIndex),
                Is.EquivalentTo(expectedCode));
        }
    }

    private static IEnumerable<TestCaseData> GetStorage_ParallelModeCases()
    {
        StorageCell cell = new(TestItem.AddressA, 1);

        yield return new TestCaseData(cell, ParallelScenario.NoChanges, 0, (uint)0x55, (uint)0x55)
            .SetName("GetStorage_ParallelMode_NoChanges_ReturnsPreBlockValue");
        yield return new TestCaseData(cell, ParallelScenario.PriorTxChange, 1, (uint)0, (uint)99)
            .SetName("GetStorage_ParallelMode_PriorTxChange_ReturnsPriorTxValue");
        yield return new TestCaseData(cell, ParallelScenario.CurrentTxChange, 1, (uint)0, (uint)0x42)
            .SetName("GetStorage_ParallelMode_CurrentTxChange_ReturnsNewValue");
    }

    [TestCaseSource(nameof(GetStorage_ParallelModeCases))]
    public void GetStorage_ParallelMode_ReturnsCorrectValue(
        StorageCell cell, ParallelScenario scenario, int txIndex, uint genesisValue, uint expectedValue)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        suggested.AddStorageRead(cell);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                if (genesisValue != 0)
                    ws.Set(cell, [(byte)genesisValue]);
            });
        using (scope)
        {
            switch (scenario)
            {
                case ParallelScenario.PriorTxChange:
                    {
                        SlotChanges sc = suggested.GetAccountChanges(TestItem.AddressA)!
                            .GetOrAddSlotChanges(cell.Index);
                        sc.AddStorageChange(new StorageChange(0, expectedValue));
                        break;
                    }
                case ParallelScenario.CurrentTxChange:
                    {
                        // Mirrors real EVM behaviour: SLOAD must precede SSTORE in the same tx.
                        _ = pws.Get(cell, blockAccessIndex: 1);
                        pws.Set(cell, [(byte)expectedValue], blockAccessIndex: 1);
                        break;
                    }
            }

            ReadOnlySpan<byte> retrieved = pws.Get(cell, blockAccessIndex: txIndex);
            Assert.That(new UInt256(retrieved, isBigEndian: true), Is.EqualTo((UInt256)expectedValue));
        }
    }

    [TestCase(true, TestName = "ExistedBeforeBlock")]
    [TestCase(false, TestName = "CreatedInCurrentTx")]
    public void AccountExists_ParallelMode_ReturnsTrue(bool existsInGenesis)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: existsInGenesis ? ws => ws.CreateAccount(TestItem.AddressA, 1) : null);
        using (scope)
        {
            if (!existsInGenesis)
            {
                pws.CreateAccount(TestItem.AddressA, 10, 1, blockAccessIndex: 1);
            }

            int txIndex = existsInGenesis ? 0 : 1;
            Assert.That(pws.AccountExists(TestItem.AddressA, blockAccessIndex: txIndex), Is.True);
        }
    }

    [Test]
    public void GetBalance_ParallelMode_AddressNotInAccessList_Throws()
    {
        BlockAccessList suggested = new(); // empty – AddressB not declared
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(suggested);
        using (scope)
        {
            Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(() =>
                pws.GetBalance(TestItem.AddressB, blockAccessIndex: 0));
        }
    }

    [Test]
    public void SubtractFromBalance_ParallelMode_DoesNotRecordSystemUserZeroChange()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(new());
        using (scope)
        {
            pws.SubtractFromBalance(Address.SystemUser, 0u, Spec, blockAccessIndex: 0);

            AccountChanges? ac = pws.GeneratedBlockAccessList.GetAccountChanges(Address.SystemUser);
            Assert.That(ac, Is.Null);
        }
    }

    [TestCase(true, 100u, 3ul, TestName = "ExistingAccount")]
    [TestCase(false, 0u, 0ul, TestName = "NonExistentAccount")]
    public void LoadPreBlockState_AccountState_AvailableAtBlockStart(
        bool existsInGenesis, uint expectedBalance, ulong expectedNonce)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: existsInGenesis
                ? ws => ws.CreateAccount(TestItem.AddressA, 100, 3)
                : null);
        using (scope)
        {
            using (Assert.EnterMultipleScope())
            {
                // GetBalance/GetNonce at blockAccessIndex=0 use strictly-before-0 semantics,
                // so they read the sentinel entry added at index –1 by LoadPreBlockState.
                Assert.That(pws.GetBalance(TestItem.AddressA, blockAccessIndex: 0),
                    Is.EqualTo((UInt256)expectedBalance));
                Assert.That(pws.GetNonce(TestItem.AddressA, blockAccessIndex: 0),
                    Is.EqualTo((UInt256)expectedNonce));
            }
        }
    }

    [Test]
    public void LoadPreBlockState_WithCode_CodeAvailableAtBlockStart()
    {
        byte[] genesisCode = [0xDE, 0xAD, 0xBE, 0xEF];
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(genesisCode), genesisCode, Spec);
            });
        using (scope)
        {
            Assert.That(pws.GetCode(TestItem.AddressA, blockAccessIndex: 0),
                Is.EquivalentTo(genesisCode));
        }
    }

    [Test]
    public void LoadPreBlockState_WithStorageSlot_StorageAvailableAtBlockStart()
    {
        StorageCell cell = new(TestItem.AddressA, 7);
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        suggested.AddStorageRead(cell); // causes LoadPreBlockState to load slot
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x42]);
            });
        using (scope)
        {
            ReadOnlySpan<byte> value = pws.Get(cell, blockAccessIndex: 0);
            Assert.That(new UInt256(value, isBigEndian: true), Is.EqualTo((UInt256)0x42));
        }
    }

    [Test]
    public void MergeIntermediateBalsUpTo_TracingOnly_IsNoOp()
    {
        (ParallelWorldState pws, IDisposable scope) = CreateTracingState(ws =>
            ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            pws.AddToBalance(TestItem.AddressA, 50, Spec, blockAccessIndex: 0);

            // ParallelExecutionEnabled is false in tracing-only mode – method must return early.
            Assert.DoesNotThrow(() => pws.MergeIntermediateBalsUpTo(0));

            // GeneratedBlockAccessList was written to directly.
            Assert.That(pws.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA)!
                .BalanceChanges, Has.Count.EqualTo(1));
        }
    }

    [TestCase((ushort)0, 150u, TestName = "Index0_AssignsIntermediate0")]
    [TestCase((ushort)1, 130u, TestName = "Index1_MergesIntermediate1")]
    public void MergeIntermediateBalsUpTo_ParallelMode_BalanceChangeAccessible(
        ushort mergeUpTo, uint expectedPostBalance)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            // Tx 0: +50 → post-balance 150 in intermediate[0]
            pws.AddToBalance(TestItem.AddressA, 50, Spec, blockAccessIndex: 0);
            // Tx 1: +30 → post-balance 130 in intermediate[1] (reads pre-block balance 100)
            pws.AddToBalance(TestItem.AddressA, 30, Spec, blockAccessIndex: 1);

            pws.MergeIntermediateBalsUpTo(0); // GeneratedBAL ← intermediate[0]
            if (mergeUpTo >= 1)
                pws.MergeIntermediateBalsUpTo(1); // merges intermediate[1] in

            Assert.That(pws.GeneratedBlockAccessList
                .GetAccountChanges(TestItem.AddressA)!
                .BalanceChangeAtIndex(mergeUpTo)!.Value.PostBalance,
                Is.EqualTo((UInt256)expectedPostBalance));
        }
    }

    [TestCase(100u, 150u, TestName = "BalanceIncrease")]
    [TestCase(100u, 70u, TestName = "BalanceDecrease")]
    public void ApplyStateChanges_Balance_AppliedToInnerWorldState(
        uint initialBalance, uint finalBalance)
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        // Inject the final balance change into the suggested BAL before loading prestate.
        // LoadPreBlockState runs inside CreateParallelState and adds the –1 sentinel, so
        // the effective list becomes {–1: initialBalance, 0: finalBalance}.
        suggested.GetAccountChanges(TestItem.AddressA)!
            .AddBalanceChange(new(0, finalBalance));

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, initialBalance));
        using (scope)
        {
            pws.ApplyStateChanges(Spec, shouldComputeStateRoot: false);

            // Disable tracing so GetBalance reads from the inner world state directly.
            pws.TracingEnabled = false;
            Assert.That(pws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)finalBalance));
        }
    }

    [Test]
    public void ApplyStateChanges_Nonce_AppliedToInnerWorldState()
    {
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        suggested.GetAccountChanges(TestItem.AddressA)!
            .AddNonceChange(new(0, 7ul));

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.ApplyStateChanges(Spec, shouldComputeStateRoot: false);

            pws.TracingEnabled = false;
            Assert.That(pws.GetNonce(TestItem.AddressA), Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public void ApplyStateChanges_Code_AppliedToInnerWorldState()
    {
        byte[] newCode = [0x60, 0x60];
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        suggested.GetAccountChanges(TestItem.AddressA)!
            .AddCodeChange(new(0, newCode));

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.ApplyStateChanges(Spec, shouldComputeStateRoot: false);

            pws.TracingEnabled = false;
            Assert.That(pws.GetCode(TestItem.AddressA), Is.EquivalentTo(newCode));
        }
    }

    [Test]
    public void ApplyStateChanges_Storage_AppliedToInnerWorldState()
    {
        StorageCell cell = new(TestItem.AddressA, 3);
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        // Register the slot directly in StorageChanges. LoadPreBlockState's StorageChanges
        // loop will snapshot it at index –1; do NOT also call AddStorageRead or both loops
        // would try to insert the –1 sentinel for the same slot.
        SlotChanges slotChanges = suggested.GetAccountChanges(TestItem.AddressA)!
            .GetOrAddSlotChanges(cell.Index);
        slotChanges.AddStorageChange(new(0, 0xABu));

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.ApplyStateChanges(Spec, shouldComputeStateRoot: false);

            pws.TracingEnabled = false;
            ReadOnlySpan<byte> stored = pws.Get(cell);
            Assert.That(new UInt256(stored, isBigEndian: true), Is.EqualTo((UInt256)0xAB));
        }
    }

    [Test]
    public void ApplyStateChanges_PrestateOnlyChange_DoesNotModifyInnerWorldState()
    {
        // The suggested BAL has only the –1 sentinel (added by LoadPreBlockState);
        // no tx-level changes → ApplyStateChanges must leave the inner world state untouched.
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            pws.ApplyStateChanges(Spec, shouldComputeStateRoot: false);

            pws.TracingEnabled = false;
            Assert.That(pws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
        }
    }

    public enum ValidateScenario
    {
        Matching,
        BalanceMismatch,
        SuggestedHasMissing,
        SuggestedHasSurplus,
    }

    [TestCase(ValidateScenario.Matching, false)]
    [TestCase(ValidateScenario.BalanceMismatch, true)]
    [TestCase(ValidateScenario.SuggestedHasSurplus, true)]
    [TestCase(ValidateScenario.SuggestedHasMissing, true)]
    public void ValidateBlockAccessList_Scenarios_MatchingOrThrows(
        ValidateScenario scenario, bool expectThrow)
    {
        const ushort txIndex = 1;
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);

        switch (scenario)
        {
            case ValidateScenario.Matching:
                // Suggested expects the same balance (100 + 50 = 150) that generated will produce.
                suggested.GetAccountChanges(TestItem.AddressA)!
                    .AddBalanceChange(new(txIndex, 150u));
                break;
            case ValidateScenario.BalanceMismatch:
                // Suggested expects 200 but generated will produce 150.
                suggested.GetAccountChanges(TestItem.AddressA)!
                    .AddBalanceChange(new(txIndex, 200u));
                break;
            case ValidateScenario.SuggestedHasSurplus:
                // AddressB appears in suggested with a tx-level change but is never touched by generated.
                suggested.AddAccountRead(TestItem.AddressB);
                suggested.GetAccountChanges(TestItem.AddressB)!
                    .AddBalanceChange(new(txIndex, 50u));
                break;
            case ValidateScenario.SuggestedHasMissing:
                break;
        }

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 100);
                if (scenario == ValidateScenario.SuggestedHasSurplus)
                    ws.CreateAccount(TestItem.AddressB, 40);
            });
        using (scope)
        {
            if (scenario != ValidateScenario.SuggestedHasSurplus)
            {
                // Generates BalanceChange(txIndex, 150) for A in intermediate[txIndex].
                pws.AddToBalance(TestItem.AddressA, 50, Spec, blockAccessIndex: txIndex);
            }

            if (scenario == ValidateScenario.SuggestedHasMissing)
            {
                suggested.RemoveAccountChanges(TestItem.AddressA);
            }

            pws.MergeIntermediateBalsUpTo(0);
            pws.MergeIntermediateBalsUpTo(txIndex);

            BlockHeader block = Build.A.BlockHeader.TestObject;
            if (expectThrow)
            {
                Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(() =>
                    pws.ValidateBlockAccessList(block, txIndex, gasRemaining: long.MaxValue));
            }
            else
            {
                Assert.DoesNotThrow(() =>
                    pws.ValidateBlockAccessList(block, txIndex, gasRemaining: long.MaxValue));
            }
        }
    }

    [TestCase(0L, true, TestName = "InsufficientGas_Throws")]
    [TestCase(long.MaxValue, false, TestName = "SufficientGas_Passes")]
    public void ValidateBlockAccessList_GasCheck_BehavesCorrectly(
        long gasRemaining, bool expectThrow)
    {
        // A storage read in the suggested BAL but not in generated means suggested read more cold
        // slots than generated did. The validator charges ColdSLoad per surplus read.
        const ushort txIndex = 0;
        StorageCell cell = new(TestItem.AddressA, 9);
        BlockAccessList suggested = BuildSuggestedBal(TestItem.AddressA);
        suggested.AddStorageRead(cell); // 1 surplus read vs generated (which has none)

        (ParallelWorldState pws, IDisposable scope) = CreateParallelState(
            suggested, genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 0));
        using (scope)
        {
            pws.MergeIntermediateBalsUpTo(0); // GeneratedBAL ← empty intermediate[0]

            BlockHeader block = Build.A.BlockHeader.TestObject;
            if (expectThrow)
            {
                Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(() =>
                    pws.ValidateBlockAccessList(block, txIndex, gasRemaining));
            }
            else
            {
                Assert.DoesNotThrow(() =>
                    pws.ValidateBlockAccessList(block, txIndex, gasRemaining));
            }
        }
    }
}
