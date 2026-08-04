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

    // Regression: an account emptied by an earlier same-block selfdestruct must read as
    // non-existent later, else a same-block CREATE2 wrongly refunds its create-state gas.
    [Test]
    public void AccountExists_PreFundedAccountDrainedToZeroEarlierInBlock_ReturnsFalse()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 0))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.False);
        }
    }

    [Test]
    public void AccountExists_PreFundedAccountWithBalance_ReturnsTrue()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal,
            genesisSetup: ws => ws.CreateAccount(TestItem.AddressA, 100));
        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
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
            Assert.That(bws.GetNonce(TestItem.AddressA), Is.EqualTo(3UL));
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

    /// <summary>
    /// SLOAD on a slot declared in <c>storage_reads</c> (no in-block change) must fall through
    /// to the parent reader. Without the fall-through, the BAL-backed world state would
    /// incorrectly return an empty slot for storage that the block legitimately read.
    /// </summary>
    [Test]
    public void GetStorage_WithStorageReadOnlyDeclaration_ReturnsParentValue()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(cell.Index)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x2A]);
            });

        using (scope)
        {
            ReadOnlySpan<byte> retrieved = bws.Get(cell);
            Assert.That(new UInt256(retrieved, isBigEndian: true), Is.EqualTo((UInt256)0x2A));
        }
    }

    /// <summary>
    /// SLOAD on a slot not declared anywhere in the account's BAL entry must throw — the spec
    /// invariant is that every slot the block touches appears either in <c>storage_changes</c>
    /// or <c>storage_reads</c>. Falling through to parent state silently would let a malformed
    /// BAL pass validation.
    /// </summary>
    [Test]
    public void GetStorage_MissingSlotDeclaration_ThrowsBeforeParentFallback()
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x2A]);
            });

        using (scope)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => bws.Get(cell));
        }
    }

    /// <summary>
    /// TryGetAccount must overlay every BAL-prior change family (balance, nonce, code) on top
    /// of the parent-state account, not just the field the latest test happened to touch.
    /// Single-field overlay would let stale parent values leak through for the untouched fields.
    /// </summary>
    [Test]
    public void TryGetAccount_OverlaysPriorChangesOnParentAccount()
    {
        byte[] parentCode = [0x60, 0x00];
        byte[] priorCode = [0x60, 0x01];
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(1, 200))
                .WithNonceChanges(new NonceChange(1, 8))
                .WithCodeChanges(new CodeChange(1, priorCode))
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 2,
            suggestedBal: bal,
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
                Assert.That(account.Nonce, Is.EqualTo(8UL));
                Assert.That(account.CodeHash, Is.EqualTo(ValueKeccak.Compute(priorCode)));
            }
        }
    }

    /// <summary>
    /// GetAccountChanges filters out BAL entries that have no state changes — accounts touched
    /// only by storage reads or pure account reads are not "changed" for the purposes of the
    /// post-execution state-apply pass, so they must not be returned.
    /// </summary>
    [Test]
    public void GetAccountChanges_IgnoresStorageReadsWithoutStateChanges()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads((UInt256)1)
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithBalanceChanges(new BalanceChange(0, 1))
                    .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.CreateAccount(TestItem.AddressB, 0);
            });

        using (scope)
        using (ArrayPoolList<AddressAsKey> changes = bws.GetAccountChanges())
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].Value, Is.EqualTo(TestItem.AddressB));
        }
    }

    /// <summary>
    /// IsStorageEmpty must validate account membership in the BAL first, then delegate to the
    /// parent reader for the actual emptiness check — the BAL only carries within-block changes
    /// and never describes the pre-block storage shape, so the answer always comes from the
    /// parent trie.
    /// </summary>
    [Test]
    public void IsStorageEmpty_UsesParentStateAfterAccountMembershipValidation()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .TestObject)
            .TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
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

    /// <summary>
    /// An account missing from parent state but introduced by a prior-tx balance change must
    /// report as existing — the existence overlay covers all three change families (balance,
    /// nonce, code), not just code. Pairs with the code-only test
    /// <see cref="AccountExists_WithPriorTxCodeChange_ReturnsTrue"/>.
    /// </summary>
    [TestCase("balance", TestName = "AccountExists_MissingParentAccountCreatedByPriorBalanceChange_ReturnsTrue")]
    [TestCase("nonce", TestName = "AccountExists_MissingParentAccountCreatedByPriorNonceChange_ReturnsTrue")]
    public void AccountExists_MissingParentAccountCreatedByPriorChange_ReturnsTrue(string changeKind)
    {
        AccountChangesBuilder builder = Build.An.AccountChanges.WithAddress(TestItem.AddressA);
        builder = changeKind switch
        {
            "balance" => builder.WithBalanceChanges(new BalanceChange(0, 1)),
            "nonce" => builder.WithNonceChanges(new NonceChange(0, 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, null),
        };
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(builder.TestObject).TestObject;

        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 1,
            suggestedBal: bal);

        using (scope)
        {
            Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        }
    }
}
