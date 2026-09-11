// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Reflection;
using System.Threading.Tasks;
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
using NSubstitute;

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
        Action<IWorldState>? genesisSetup = null,
        Func<IWorldState, IWorldState>? decorateParent = null,
        BalReadCoverage? readCoverage = null,
        ILogManager? logManager = null)
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

        BlockAccessListBasedWorldState bws = new(inner, logManager ?? Logger);
        bws.SetBlockAccessIndex(blockAccessIndex);
        Block block = Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(suggestedBal).TestObject;
        bws.Setup(block, readCoverage);
        IDisposable scope = inner.BeginScope(baseBlock);
        // The inner world state, scoped against the genesis root, is itself a valid parent reader
        // — reads against it answer pre-block state directly from the trie.
        bws.SetParentReader(decorateParent?.Invoke(inner) ?? inner);
        return (bws, scope);
    }

    [Test]
    public void DeclaredReads_PreserveOriginalValuesAndSnapshots([Values] bool decorate, [Values(0, 42)] int storedValue)
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(cell.Address)
                .WithStorageReads(cell.Index).TestObject).TestObject;
        IWorldState parent = null!;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(1, bal,
            ws =>
            {
                ws.CreateAccount(cell.Address, 100);
                ws.Set(cell, [(byte)storedValue]);
            },
            ws =>
            {
                parent = ws;
                return decorate ? new ParentDecorator(ws) : ws;
            });
        using (scope)
        {
            Snapshot before = parent.TakeSnapshot();
            Assert.That(bws.GetBalance(cell.Address), Is.EqualTo((UInt256)100));
            Assert.That(bws.AccountExists(cell.Address), Is.True);
            Assert.That(bws.IsDeadAccount(cell.Address), Is.False);
            if (!decorate) Assert.That(parent.TakeSnapshot(), Is.EqualTo(before), "account read must not journal");

            Assert.That(new UInt256(bws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)storedValue));
            Assert.That(new UInt256(bws.GetOriginal(cell), isBigEndian: true), Is.EqualTo((UInt256)storedValue));
            if (!decorate)
                Assert.That(parent.TakeSnapshot().StorageSnapshot.PersistentStorageSnapshot,
                    Is.EqualTo(before.StorageSnapshot.PersistentStorageSnapshot), "storage read must not journal");

            Snapshot snapshot = bws.TakeSnapshot();
            bws.Set(cell, [99]);
            bws.Restore(snapshot);
            Assert.That(new UInt256(bws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)storedValue));

            bws.ClearParentReader();
            parent.Set(cell, [77]);
            parent.AddToBalance(cell.Address, 100, Spec);
            parent.SetNonce(cell.Address, 3);
            parent.Commit(Spec);
            parent.CommitTree(1);
            bws.SetParentReader(decorate ? new ParentDecorator(parent) : parent);
            bws.Setup(Build.A.Block.WithBlockAccessList(bal).TestObject);
            Assert.That(new UInt256(bws.Get(cell), isBigEndian: true), Is.EqualTo((UInt256)77));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bws.GetBalance(cell.Address), Is.EqualTo((UInt256)200));
                Assert.That(bws.GetNonce(cell.Address), Is.EqualTo(3UL));
            }
        }
    }

    private sealed class ParentDecorator(IWorldState state) : WorldStateDecorator(state);

    [TestCase(false, false, 0)]
    [TestCase(true, false, 0)]
    [TestCase(false, true, 0)]
    [TestCase(true, true, 0)]
    [TestCase(false, true, 1)]
    [TestCase(true, true, 1)]
    public void TryGetAccount_PreservesParentExistence(bool decorate, bool createAccount, int balance)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject).TestObject;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(0, bal,
            ws =>
            {
                if (createAccount) ws.CreateAccount(TestItem.AddressA, (UInt256)balance);
            }, ws => decorate ? new ParentDecorator(ws) : ws);
        using (scope)
        {
            bool exists = bws.TryGetAccount(TestItem.AddressA, out AccountStruct account);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exists, Is.EqualTo(createAccount && balance != 0));
                Assert.That(account.Balance, Is.EqualTo(createAccount ? (UInt256)balance : UInt256.Zero));
                Assert.That(account.IsTotallyEmpty, Is.EqualTo(!createAccount || balance == 0));
            }
        }
    }

    [Test]
    public void DeclaredReads_CacheAlternatingSlotsUntilParentContextChanges([Values] bool replaceReader, [Values] bool useCoverage, [Values(0, 192)] int slotShift)
    {
        UInt256 firstSlot = UInt256.One << slotShift;
        UInt256 secondSlot = (UInt256)2 << slotShift;
        StorageCell[] cells = [new(TestItem.AddressA, firstSlot), new(TestItem.AddressA, secondSlot), new(TestItem.AddressB, firstSlot)];
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(firstSlot, secondSlot).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(firstSlot).TestObject).TestObject;
        using BalReadStoragePlan plan = new(bal);
        BalReadCoverage? coverage = useCoverage ? plan.CreateCoverage() : null;
        IWorldState parent = null!;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(1, bal,
            ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 100);
                ws.CreateAccount(TestItem.AddressB, 100);
                ws.Set(cells[1], [42]);
                ws.Set(cells[2], [77]);
            }, ws => parent = ws, coverage);
        using (scope)
        {
            LocalMetrics metrics = (LocalMetrics)typeof(WorldState)
                .GetField("_localMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent)!;
            long readsBefore = metrics.StorageTreeReads;
            Snapshot snapshot = parent.TakeSnapshot();
            ReadAlternatingSlots(0);
            bws.Restore(snapshot);
            bws.SetBlockAccessIndex(2);
            coverage?.StartSlice();
            ReadAlternatingSlots(0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(metrics.StorageTreeReads - readsBefore, Is.EqualTo(cells.Length));
                Assert.That(parent.TakeSnapshot(), Is.EqualTo(snapshot), "pure reads must not journal");
            }

            if (replaceReader) bws.ClearParentReader();
            parent.Set(cells[0], [99]);
            parent.Commit(Spec);
            parent.CommitTree(1);
            if (replaceReader) bws.SetParentReader(parent);
            bws.Setup(Build.A.Block.WithBlockAccessList(bal).TestObject, coverage);
            coverage?.StartSlice();
            readsBefore = metrics.StorageTreeReads;
            ReadAlternatingSlots(99);
            Assert.That(metrics.StorageTreeReads - readsBefore, Is.EqualTo(cells.Length));
        }

        void ReadAlternatingSlots(uint firstValue)
        {
            uint[] expected = [firstValue, 42, 77];
            for (int repeat = 0; repeat < 4; repeat++)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    ReadOnlySpan<byte> value = repeat % 2 == 0 ? bws.Get(cells[i]) : bws.GetOriginal(cells[i]);
                    Assert.That(new UInt256(value, isBigEndian: true), Is.EqualTo((UInt256)expected[i]));
                }
            }
            if (coverage is not null)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(coverage.ChargeableReadCount, Is.EqualTo((ulong)cells.Length));
                    Assert.That(plan.TryFindUncovered(out _), Is.False);
                }
            }
        }
    }

    [Test]
    public void AccountContext_FollowsAddressIndexAndBlockChanges()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, 10), new BalanceChange(1, 20)).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB)
                    .WithBalanceChanges(new BalanceChange(0, 30)).TestObject)
            .TestObject;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(1, bal);
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)10));
            Assert.That(bws.GetBalance(new Address(TestItem.AddressA.Bytes)), Is.EqualTo((UInt256)10));
            Assert.That(bws.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)30));
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)10));
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => bws.GetBalance(TestItem.AddressC));
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => bws.GetBalance(TestItem.AddressC));
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)10));

            bws.SetBlockAccessIndex(2);
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)20));

            ReadOnlyBlockAccessList next = Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, 40)).TestObject).TestObject;
            bws.Setup(Build.A.Block.WithBlockAccessList(next).TestObject);
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)40));
            bws.ClearParentReader();
            Assert.Throws<InvalidOperationException>(() => bws.GetBalance(TestItem.AddressA));
        }
    }

    [Test]
    public void AccountContext_DoesNotRetainPreviousBlockAfterSetupFails()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsTrace.Returns(true);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 10)).TestObject).TestObject;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            1, bal, logManager: new OneLoggerLogManager(new ILogger(logger)));
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)10));
            ReadOnlyBlockAccessList next = Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, 40)).TestObject).TestObject;
            logger.When(log => log.Trace(Arg.Any<string>()))
                .Do(_ => throw new InvalidOperationException("Injected reset failure"));
            Assert.Throws<InvalidOperationException>(() => bws.Setup(Build.A.Block.WithBlockAccessList(next).TestObject));
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)40));
        }
    }

    [Test]
    public void AccountContext_DoesNotCacheFailedParentReaderValidation()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 10)).TestObject).TestObject;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(1, bal);
        using (scope)
        {
            Assert.That(bws.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)10));
            bws.ClearParentReader();
            bws.Setup(Build.A.Block.WithBlockAccessList(bal).TestObject);

            Assert.Throws<InvalidOperationException>(() => bws.GetBalance(TestItem.AddressA));
            Assert.Throws<InvalidOperationException>(() => bws.GetBalance(TestItem.AddressA));
            Assert.Throws<InvalidOperationException>(() => bws.GetBalance(new Address(TestItem.AddressA.Bytes)));
        }
    }

    private static readonly uint[] CompositeReadIndices = [0, 1, 2, 3, 4, 31, 32, 33, 63, 64, uint.MaxValue];

    [Test]
    public void CompositeReads_UseEffectiveState(
        [Values("balance", "nonce", "code", "empty")] string kind,
        [ValueSource(nameof(CompositeReadIndices))] uint index,
        [Values(0, 1, 4, 32)] int count,
        [Values] bool decorate)
    {
        BalanceChange[] balances = new BalanceChange[count];
        NonceChange[] nonces = new NonceChange[count];
        CodeChange[] codes = new CodeChange[count];
        int last = -1;
        for (int i = 0; i < count; i++)
        {
            uint changeIndex = (uint)(2 * i + 1);
            bool nonempty = i % 2 == 0;
            balances[i] = new BalanceChange(changeIndex, nonempty && kind == "balance" ? 10u : 0u);
            nonces[i] = new NonceChange(changeIndex, nonempty && kind == "nonce" ? 10u : 0u);
            codes[i] = new CodeChange(changeIndex, nonempty && kind == "code" ? [0x00] : []);
            if (changeIndex < index) last = i;
        }
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithBalanceChanges(balances)
                .WithNonceChanges(nonces)
                .WithCodeChanges(codes)
                .TestObject).TestObject;
        IWorldState parent = null!;
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(index, bal,
            ws =>
            {
                ws.CreateAccount(TestItem.AddressA, kind is "code" or "empty" ? 20u : 0u,
                    kind is "balance" or "empty" ? 30UL : 0UL);
                if (kind is "nonce" or "empty")
                    ws.InsertCode(TestItem.AddressA, ValueKeccak.Compute([0x00]), new byte[] { 0x00 }, Spec);
            }, ws =>
            {
                parent = ws;
                return decorate ? new ParentDecorator(ws) : ws;
            });
        using (scope)
        {
            Snapshot before = parent.TakeSnapshot();
            using (Assert.EnterMultipleScope())
            {
                bool exists = last < 0 || (last % 2 == 0 && kind != "empty");
                Assert.That(bws.AccountExists(TestItem.AddressA), Is.EqualTo(exists));
                Assert.That(bws.IsDeadAccount(TestItem.AddressA), Is.EqualTo(!exists));
                Assert.That(bws.IsContract(TestItem.AddressA), Is.EqualTo(last < 0 ? kind is "nonce" or "empty" : last % 2 == 0 && kind == "code"));
            }
            if (!decorate) Assert.That(parent.TakeSnapshot(), Is.EqualTo(before), "composite reads must not journal");
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() => bws.AccountExists(TestItem.AddressB));
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() => bws.IsDeadAccount(TestItem.AddressB));
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(() => bws.IsContract(TestItem.AddressB));
        }
    }

    [Test]
    public void CompositeReads_PreserveVirtualOverrides([Values("balance", "nonce", "code")] string kind)
    {
        OverriddenAccountState bws = new(TestWorldStateFactory.CreateForTest(), kind);
        Assert.That(bws.AccountExists(TestItem.AddressA), Is.True);
        Assert.That(bws.IsDeadAccount(TestItem.AddressA), Is.False);
        bws.ReportMissing = true;
        Assert.That(bws.IsDeadAccount(TestItem.AddressA), Is.True);
    }

    private sealed class OverriddenAccountState(IWorldState state, string kind) : BlockAccessListBasedWorldState(state, Logger)
    {
        private readonly UInt256 _balance = kind == "balance" ? 1u : 0u;
        private readonly ulong _nonce = kind == "nonce" ? 1UL : 0UL;
        private readonly ValueHash256 _codeHash = kind == "code" ? ValueKeccak.Compute([0x00]) : Keccak.OfAnEmptyString.ValueHash256;
        public bool ReportMissing { get; set; }
        public override ref readonly UInt256 GetBalance(Address address) => ref _balance;
        public override ulong GetNonce(Address address) => _nonce;
        public override ref readonly ValueHash256 GetCodeHash(Address address) => ref _codeHash;
        public override bool AccountExists(Address address) => !ReportMissing && base.AccountExists(address);
    }

    [Test]
    public void GetCodeByHash_ConcurrentReadersKeepIndependentIndicesAndBlocks([Values] bool hasCodeChanges)
    {
        byte[] code = [0x00, 0x01];
        ValueHash256 hash = ValueKeccak.Compute(code);
        CodeChange[] changes = hasCodeChanges ? [new CodeChange(1, code), new CodeChange(5, code)] : [];
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithCodeChanges(changes).TestObject).TestObject;
        ReadOnlyBlockAccessList next = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithCodeChanges(new CodeChange(10, code)).TestObject).TestObject;
        Block originalBlock = Build.A.Block.WithBlockAccessList(bal).TestObject;
        Block nextBlock = Build.A.Block.WithBlockAccessList(next).TestObject;

        Parallel.For(0, 8, index =>
        {
            (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState((uint)index, bal);
            using (scope)
            {
                Assert.That(bws.GetCode(hash), Is.EqualTo(hasCodeChanges && index > 1 ? code : null));
                bws.Setup(nextBlock);
                Assert.That(bws.GetCode(hash), Is.Null);
                bws.Setup(originalBlock);
                Assert.That(bws.GetCode(hash), Is.EqualTo(hasCodeChanges && index > 1 ? code : null));
                bws.ClearParentReader();
                Assert.That(bws.GetCode(hash), Is.Null);
            }
        });
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
    public void GetStorage_MissingDeclaration_ThrowsBeforeParentFallback(
        [Values] bool missingAccount, [Values] bool original, [Values] bool useCoverage)
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(missingAccount ? TestItem.AddressB : TestItem.AddressA)
                .WithStorageReads((UInt256)2)
                .TestObject)
            .TestObject;
        using BalReadStoragePlan plan = new(bal);
        (BlockAccessListBasedWorldState bws, IDisposable scope) = CreateBlockAccessListState(
            blockAccessIndex: 0,
            suggestedBal: bal,
            genesisSetup: ws =>
            {
                ws.CreateAccount(TestItem.AddressA, 0);
                ws.Set(cell, [0x2A]);
            },
            readCoverage: useCoverage ? plan.CreateCoverage() : null);

        using (scope)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () =>
                {
                    if (original) bws.GetOriginal(cell);
                    else bws.Get(cell);
                });
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
