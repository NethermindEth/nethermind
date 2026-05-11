// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
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
    /// Creates a <see cref="BlockAccessListBasedWorldState"/> backed by a concrete inner state,
    /// with a suggested <see cref="BlockAccessList"/> populated via <paramref name="balSetup"/>.
    /// The inner state is initialized via <paramref name="genesisSetup"/>.
    /// </summary>
    private static AccountChanges AddAccountRead(BlockAccessList bal, Address address)
    {
        bal.AddAccountRead(address);
        return bal.GetAccountChanges(address)!;
    }

    private static (BlockAccessListBasedWorldState bws, IDisposable scope) CreateBlockAccessListState(
        uint blockAccessIndex,
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
        bws.SetParentReader(inner);
        Block block = Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(suggestedBal).TestObject;
        bws.Setup(block);
        IDisposable scope = bws.BeginScope(baseBlock);
        return (bws, scope);
    }

    public enum AccountReadKind
    {
        Balance,
        Nonce,
        Code
    }

    private static byte[] ParentCode() => [0x60, 0x00];

    private static byte[] PriorCode() => [0xAA, 0xBB];

    private static void SetupParentAccountValue(IWorldState worldState, AccountReadKind readKind)
    {
        switch (readKind)
        {
            case AccountReadKind.Balance:
                worldState.CreateAccount(TestItem.AddressA, 100);
                break;
            case AccountReadKind.Nonce:
                worldState.CreateAccount(TestItem.AddressA, 0, 7);
                break;
            case AccountReadKind.Code:
                byte[] parentCode = ParentCode();
                worldState.CreateAccount(TestItem.AddressA, 0);
                worldState.InsertCode(TestItem.AddressA, ValueKeccak.Compute(parentCode), parentCode, Spec);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readKind), readKind, null);
        }
    }

    private static void AddPriorAccountValueChange(AccountChanges accountChanges, AccountReadKind readKind)
    {
        switch (readKind)
        {
            case AccountReadKind.Balance:
                accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, 100));
                accountChanges.AddBalanceChange(new(0, 200));
                break;
            case AccountReadKind.Nonce:
                accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddNonceChange(new(0, 3));
                break;
            case AccountReadKind.Code:
                accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, []));
                accountChanges.AddCodeChange(new(0, PriorCode()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readKind), readKind, null);
        }
    }

    private static void AssertAccountValue(BlockAccessListBasedWorldState bws, AccountReadKind readKind, bool usePriorValue)
    {
        switch (readKind)
        {
            case AccountReadKind.Balance:
                Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo(usePriorValue ? (UInt256)200 : (UInt256)100));
                break;
            case AccountReadKind.Nonce:
                Assert.That(bws.GetNonce(TestItem.AddressA), Is.EqualTo(usePriorValue ? (UInt256)3 : (UInt256)7));
                break;
            case AccountReadKind.Code:
                byte[] expectedCode = usePriorValue ? PriorCode() : ParentCode();
                Assert.That(bws.GetCode(TestItem.AddressA), Is.EqualTo(expectedCode));
                Assert.That(bws.GetCodeHash(TestItem.AddressA), Is.EqualTo(ValueKeccak.Compute(expectedCode)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(readKind), readKind, null);
        }
    }

    private static void AssertStorageValue(BlockAccessListBasedWorldState bws, in StorageCell cell, UInt256 expectedValue)
    {
        ReadOnlySpan<byte> retrieved = bws.Get(cell);
        Assert.That(new UInt256(retrieved, isBigEndian: true), Is.EqualTo(expectedValue));
    }

    private static void AssertInvalidBlockAccessList(TestDelegate action) =>
        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(action);

    [Test]
    public void GetBalance_ReturnsValueFromSuggestedBal()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal =>
                AddAccountRead(bal, TestItem.AddressA)
                    .AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100)),
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
        }
    }

    [TestCase(AccountReadKind.Balance, TestName = "GetBalance_WithoutPrestateSentinel_ReturnsParentBalance")]
    [TestCase(AccountReadKind.Nonce, TestName = "GetNonce_WithoutPrestateSentinel_ReturnsParentNonce")]
    [TestCase(AccountReadKind.Code, TestName = "GetCode_WithoutPrestateSentinel_ReturnsParentCode")]
    public void Account_value_without_prestate_sentinel_returns_parent_value(AccountReadKind readKind)
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => AddAccountRead(bal, TestItem.AddressA),
            genesisSetup: ws => SetupParentAccountValue(ws, readKind));

        using (scope)
        {
            AssertAccountValue(bws, readKind, usePriorValue: false);
        }
    }

    [TestCase(AccountReadKind.Balance, TestName = "GetBalance_WithPriorTxChange_ReturnsUpdatedBalance")]
    [TestCase(AccountReadKind.Nonce, TestName = "GetNonce_WithPriorTxChange_ReturnsUpdatedNonce")]
    [TestCase(AccountReadKind.Code, TestName = "GetCode_WithPriorTxChange_ReturnsPriorTxCode")]
    public void Account_value_with_prior_tx_change_returns_prior_value(AccountReadKind readKind)
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal => AddPriorAccountValueChange(AddAccountRead(bal, TestItem.AddressA), readKind),
            genesisSetup: ws => SetupParentAccountValue(ws, readKind));

        using (scope)
        {
            AssertAccountValue(bws, readKind, usePriorValue: true);
        }
    }

    [Test]
    public void TryGetAccount_WithPriorCodeChange_ReturnsExistingAccount()
    {
        byte[] priorTxCode = [0xAA, 0xBB];
        ValueHash256 expectedCodeHash = ValueKeccak.Compute(priorTxCode);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, []));
                accountChanges.AddCodeChange(new CodeChange(0, priorTxCode));
            });
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
    public void GetCode_ByHash_ReturnsPriorCodeChangeFromSuggestedBal()
    {
        byte[] priorTxCode = [0xAA, 0xBB];
        ValueHash256 expectedCodeHash = ValueKeccak.Compute(priorTxCode);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, []));
                accountChanges.AddCodeChange(new(0, priorTxCode));
            });

        using (scope)
        {
            byte[]? code = bws.GetCode(in expectedCodeHash);

            Assert.That(code, Is.EqualTo(priorTxCode));
        }
    }

    [Test]
    public void GetCode_ByHash_DoesNotReturnCodeChangeAtOrAfterCurrentIndex()
    {
        byte[] currentTxCode = [0xAA, 0xBB];
        byte[] futureTxCode = [0xCC, 0xDD];
        ValueHash256 currentTxCodeHash = ValueKeccak.Compute(currentTxCode);
        ValueHash256 futureTxCodeHash = ValueKeccak.Compute(futureTxCode);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.AddCodeChange(new(1, currentTxCode));
                accountChanges.AddCodeChange(new(2, futureTxCode));
            });

        using (scope)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bws.GetCode(in currentTxCodeHash), Is.Null);
                Assert.That(bws.GetCode(in futureTxCodeHash), Is.Null);
            }
        }
    }

    [Test]
    public void GetCode_ByHash_ReturnsNullWhenCodeIsOnlyInParentState()
    {
        byte[] parentCode = ParentCode();
        ValueHash256 parentCodeHash = ValueKeccak.Compute(parentCode);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => AddAccountRead(bal, TestItem.AddressA),
            genesisSetup: ws => SetupParentAccountValue(ws, AccountReadKind.Code));

        using (scope)
        {
            byte[]? code = bws.GetCode(in parentCodeHash);

            Assert.That(code, Is.Null);
        }
    }

    [Test]
    public void AccountExists_WithOnlyPrestateSentinelAndCurrentTxChanges_ReturnsFalseBeforeCurrentTx()
    {
        byte[] currentTxCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 23,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.ExistedBeforeBlock = false;
                accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, []));
                accountChanges.AddNonceChange(new(23, 1));
                accountChanges.AddCodeChange(new(23, currentTxCode));
            });

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void AccountExists_WithPriorTxCodeChange_ReturnsTrue()
    {
        byte[] priorTxCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressB.Bytes];
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 24,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.ExistedBeforeBlock = false;
                accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, 0));
                accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, []));
                accountChanges.AddNonceChange(new(23, 1));
                accountChanges.AddCodeChange(new(23, priorTxCode));
            });

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
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
            AssertStorageValue(bws, cell, 99);
        }
    }

    [Test]
    public void GetStorage_WithStorageReadOnlyDeclaration_ReturnsParentValue()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => bal.AddStorageRead(cell),
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x2A]);
            });

        using (scope)
        {
            AssertStorageValue(bws, cell, 0x2Au);
        }
    }

    [Test]
    public void GetStorage_MissingSlotDeclaration_ThrowsBeforeParentFallback()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => AddAccountRead(bal, TestItem.AddressA),
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x2A]);
            });

        using (scope)
        {
            AssertInvalidBlockAccessList(() => bws.Get(cell));
        }
    }

    [Test]
    public void TryGetAccount_OverlaysPriorChangesOnParentAccount()
    {
        byte[] parentCode = [0x60, 0x00];
        byte[] priorCode = [0x60, 0x01];
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 2,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.AddBalanceChange(new(1, 200));
                accountChanges.AddNonceChange(new(1, 8));
                accountChanges.AddCodeChange(new(1, priorCode));
            },
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 100, 7);
                ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute(parentCode), parentCode, Spec);
            });

        using (scope)
        {
            Assert.That(bws.TryGetAccount(TestItem.AddressA, out AccountStruct account), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(account.Balance, Is.EqualTo((UInt256)200));
                Assert.That(account.Nonce, Is.EqualTo((UInt256)8));
                Assert.That(account.CodeHash, Is.EqualTo(ValueKeccak.Compute(priorCode)));
            }
        }
    }

    [Test]
    public void GetAccountChanges_IgnoresStorageReadsWithoutStateChanges()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal =>
            {
                bal.AddStorageRead(cell);
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressB);
                accountChanges.AddBalanceChange(new(0, 1));
            },
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.CreateAccount(TestItem.AddressB, 0);
            });

        using (scope)
        using (ArrayPoolList<AddressAsKey> changes = bws.GetAccountChanges())
        {
            Assert.That((changes).Count, Is.EqualTo(1));
            Assert.That(changes[0].Value, Is.EqualTo(TestItem.AddressB));
        }
    }

    [Test]
    public void IsStorageEmpty_UsesParentStateAfterAccountMembershipValidation()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => AddAccountRead(bal, TestItem.AddressA),
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(new StorageCell(TestItem.AddressA, 1), [0x2A]);
            });

        using (scope)
        {
            Assert.That(bws.IsStorageEmpty(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void AccountExists_ParentAccountWithoutPriorChanges_ReturnsTrue()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: bal => AddAccountRead(bal, TestItem.AddressA),
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
                ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 0));
                ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 0));
                ac.ExistedBeforeBlock = false;
            });
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void AccountExists_MissingParentAccountCreatedByPriorChange_ReturnsTrue()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            balSetup: bal =>
            {
                AccountChanges accountChanges = AddAccountRead(bal, TestItem.AddressA);
                accountChanges.AddBalanceChange(new(0, 1));
            });

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void AccountExists_AddressNotInAccessList_ThrowsBeforeParentFallback()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: _ => { },
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressB, 1));

        using (scope)
        {
            AssertInvalidBlockAccessList(() => bws.AccountExists(TestItem.AddressB));
        }
    }

    [Test]
    public void GetBalance_AddressNotInAccessList_Throws()
    {
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            balSetup: _ => { },
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressB, 1));
        using (scope)
        {
            AssertInvalidBlockAccessList(() => bws.GetBalance(TestItem.AddressB));
        }
    }
}
