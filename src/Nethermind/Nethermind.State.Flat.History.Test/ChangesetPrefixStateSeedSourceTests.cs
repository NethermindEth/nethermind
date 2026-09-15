// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetPrefixStateSeedSourceTests
{
    private static readonly IReleaseSpec Spec = Prague.Instance;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private TransactionChangesetIndex _index = null!;
    private Block _block = null!;
    private BlockHeader _parent = null!;
    private IWorldState _state = null!;
    private IDisposable _scope = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _index = new TransactionChangesetIndex(_columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        _parent = Build.A.BlockHeader.WithNumber(6).TestObject;
        _block = Build.A.Block.WithNumber(7).WithParent(_parent)
            .WithTransactions(Build.A.Transaction.WithNonce(0).TestObject, Build.A.Transaction.WithNonce(1).TestObject).TestObject;
        _state = TestWorldStateFactory.CreateForTest();
        _scope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 100, 1);
        _state.Commit(Spec);
        Capture();
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _columns.Dispose();
    }

    [Test]
    public void TheParentStateOfEveryOverlayEntry_IsReadBeforeTheSeedIsApplied()
    {
        IStateReader reader = Substitute.For<IStateReader>();
        ChangesetPrefixStateSeedSource source = new(_index, reader, _ => _parent);

        bool seeded = source.TrySeed(_block, 1, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded, Is.True);
            reader.Received(1).TryGetAccount(_parent, TestItem.AddressA, out Arg.Any<AccountStruct>());
            reader.Received(1).GetStorage(_parent, TestItem.AddressA, Arg.Is<UInt256>(i => i == 5), out Arg.Any<UInt256>());
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)42), "the warm-up is a pre-read only; the seed still applies");
        }
    }

    [Test]
    public void WithoutAParentHeader_TheSeedStillApplies()
    {
        IStateReader reader = Substitute.For<IStateReader>();
        ChangesetPrefixStateSeedSource source = new(_index, reader, _ => null);

        bool seeded = source.TrySeed(_block, 1, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded, Is.True);
            reader.DidNotReceiveWithAnyArgs().TryGetAccount(default, default!, out Arg.Any<AccountStruct>());
        }
    }

    private void Capture()
    {
        using TransactionChangesetIndex.BlockCapture capture = _index.StartBlock(7);
        capture.Tracer.StartNewBlockTrace(_block);
        ITxTracer first = capture.Tracer.StartNewTxTrace(_block.Transactions[0]);
        first.ReportBalanceChange(TestItem.AddressA, 100, 42);
        first.ReportStorageChange(new StorageCell(TestItem.AddressA, 5), [], [0x11]);
        capture.Tracer.EndTxTrace();
        ITxTracer second = capture.Tracer.StartNewTxTrace(_block.Transactions[1]);
        second.ReportNonceChange(TestItem.AddressB, 0, 1);
        capture.Tracer.EndTxTrace();
        capture.Tracer.EndBlockTrace();
        capture.Commit();
        _index.TryClaim(7, 7);
    }
}
