// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NonceManagerTests
{
    private const int SweepThreshold = 4;
    private const ulong HeadNumber = 1000;

    private ISpecProvider _specProvider;
    private TestReadOnlyStateProvider _stateProvider;
    private IBlockTree _blockTree;
    private ChainHeadInfoProvider _headInfo;
    private INonceManager _nonceManager;
    private IReadOnlyStateProvider _headState;
    private IChainHeadInfoProvider _chainHead;
    private IStateHeaderProvider _stateHeaderProvider;
    private IStateReader _stateReader;
    private BlockHeader _reorgSafeHeader;

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        _stateProvider = new TestReadOnlyStateProvider();
        _blockTree = Substitute.For<IBlockTree>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        _blockTree.Head.Returns(block);
        _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);

        _headInfo = new ChainHeadInfoProvider(
            new ChainHeadSpecProvider(_specProvider, _blockTree),
            _blockTree,
            _stateProvider);
        _nonceManager = new NonceManager(_headInfo, Substitute.For<IStateHeaderProvider>(), Substitute.For<IStateReader>());

        _headState = Substitute.For<IReadOnlyStateProvider>();
        _chainHead = Substitute.For<IChainHeadInfoProvider>();
        _chainHead.ReadOnlyStateProvider.Returns(_headState);
        _chainHead.HeadNumber.Returns(HeadNumber);
        _reorgSafeHeader = Build.A.BlockHeader.WithNumber(HeadNumber - Reorganization.MaxDepth).TestObject;
        _stateHeaderProvider = Substitute.For<IStateHeaderProvider>();
        _stateHeaderProvider.FinalizedBlockNumber.Returns(HeadNumber);
        _stateHeaderProvider.GetFinalizedHeader(_reorgSafeHeader.Number).Returns(_reorgSafeHeader);
        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(_reorgSafeHeader).Returns(true);
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {
        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(2UL));
            locker.Accept();
        }
    }

    [Test]
    [Explicit]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
    {
        const int reservationsCount = 1000;

        ConcurrentQueue<ulong> nonces = new();

        ParallelLoopResult result = Parallel.For(0, reservationsCount, i =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
            locker.Accept();
            nonces.Enqueue(nonce);
        });

        Assert.That(result.IsCompleted, Is.True);
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        nonces.Enqueue(nonce);
        Assert.That(nonce, Is.EqualTo((ulong)reservationsCount));
        Assert.That(nonces.OrderBy(n => n), Is.EqualTo(Enumerable.Range(0, reservationsCount + 1).Select(i => (ulong)i)));
    }

    [Test]
    public void should_pick_account_nonce_as_initial_value()
    {
        _headState.GetNonce(TestItem.AddressA).Returns(0UL);
        _nonceManager = CreateSweepingNonceManager();

        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        _headState.GetNonce(TestItem.AddressA).Returns(10UL);
        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(10UL));
        }
    }

    [Test]
    public void ReserveNonce_should_skip_nonce_if_TxWithNonceReceived()
    {
        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 4))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.TxWithNonceReceived(TestItem.AddressA, 2))
        {
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(3UL));
            locker.Accept();
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(5UL));
            locker.Accept();
        }
    }

    // 1. Four senders have raw nonce 0 accepted while the head nonce is 0.
    // 2. Their nonce moves to 1 at the head, and at the reorg-safe block only when the case says so.
    // 3. A fifth sender triggers the sweep.
    [TestCase(1UL, 1, TestName = "CoveredByReorgSafeNonce_DropsSenders")]
    [TestCase(0UL, SweepThreshold + 1, TestName = "CoveredOnlyByHeadNonce_KeepsSenders")]
    public void TxWithNonceReceived_AcceptedNonceBelowHeadNonce_DropsSenderOnlyOnceReorgSafe(ulong reorgSafeNonce, int expectedTracked)
    {
        NonceManager nonceManager = CreateSweepingNonceManager();
        for (int i = 0; i < SweepThreshold; i++)
        {
            AcceptRawNonce(nonceManager, TestItem.Addresses[i], 0);
            SetReorgSafeNonce(TestItem.Addresses[i], reorgSafeNonce);
        }

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(SweepThreshold), "precondition: every raw-tx sender is tracked");
        _headState.GetNonce(Arg.Any<Address>()).Returns(1UL);

        TriggerSweep(nonceManager);

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(expectedTracked), "a sender may only go once its nonce at the reorg-safe block covers every nonce it accepted");
    }

    // 1. A reserves and accepts nonce 5 while its head nonce is 5.
    // 2. The tx is mined: the head nonce moves to 6, the reorg-safe block still has nonce 5.
    // 3. A sweep runs.
    // 4. A reorg drops the block with the tx: the head nonce is back to 5.
    // 5. The next reservation must not hand out nonce 5 again.
    [Test]
    public void ReserveNonce_AfterSweepAndReorgBelowAcceptedNonce_DoesNotReissueIt()
    {
        NonceManager nonceManager = CreateSweepingNonceManager();
        _headState.GetNonce(TestItem.AddressA).Returns(5UL);
        SetReorgSafeNonce(TestItem.AddressA, 5);
        using (NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong accepted))
        {
            Assert.That(accepted, Is.EqualTo(5UL), "precondition: the first reservation starts at the head nonce");
            locker.Accept();
        }

        _headState.GetNonce(TestItem.AddressA).Returns(6UL);
        for (int i = 1; i < SweepThreshold; i++)
        {
            RejectRawNonce(nonceManager, TestItem.Addresses[i], 0);
        }

        TriggerSweep(nonceManager);
        Assert.That(nonceManager.TrackedAddressCount, Is.LessThanOrEqualTo(2), "precondition: the sweep ran and dropped the empty entries");

        _headState.GetNonce(TestItem.AddressA).Returns(5UL);

        using (nonceManager.ReserveNonce(TestItem.AddressA, out ulong next))
        {
            Assert.That(next, Is.EqualTo(6UL), "nonce 5 was handed out and accepted, so a reorg must not make it free again");
        }
    }

    // Without state at the reorg-safe block, a sender that had a nonce accepted is kept and a sender whose every
    // submission was rejected is dropped: the latter handed out nothing a fresh entry could clash with.
    [TestCase(ReorgSafeStateGap.HeaderMissing, TestName = "ReorgSafeHeaderMissing")]
    [TestCase(ReorgSafeStateGap.StatePruned, TestName = "ReorgSafeStatePruned")]
    [TestCase(ReorgSafeStateGap.NodeMissingOnRead, TestName = "ReorgSafeNodeMissingOnRead")]
    public void TxWithNonceReceived_ReorgSafeStateUnavailable_DropsOnlyNeverAcceptedSenders(ReorgSafeStateGap gap)
    {
        MakeReorgSafeStateUnavailable(gap);
        NonceManager nonceManager = CreateSweepingNonceManager();
        AcceptRawNonce(nonceManager, TestItem.AddressA, 0);
        AcceptRawNonce(nonceManager, TestItem.AddressB, 0);
        RejectRawNonce(nonceManager, TestItem.AddressC, 0);
        RejectRawNonce(nonceManager, TestItem.AddressD, 0);
        _headState.GetNonce(Arg.Any<Address>()).Returns(1UL);

        TriggerSweep(nonceManager);

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(3), "the two accepted senders stay next to the new one, the two rejected ones go");
    }

    [TestCase(true, TestName = "FinalizedBelowReorgDepth_ReadsAtFinalized")]
    [TestCase(false, TestName = "FinalizedAboveReorgDepth_ReadsAtReorgDepth")]
    public void TxWithNonceReceived_Sweep_ReadsStateAtLowerOfFinalizedAndReorgDepth(bool finalizedBelowReorgDepth)
    {
        ulong reorgDepthNumber = HeadNumber - Reorganization.MaxDepth;
        ulong finalizedNumber = finalizedBelowReorgDepth ? reorgDepthNumber - 10 : HeadNumber - 1;
        ulong expectedNumber = ulong.Min(finalizedNumber, reorgDepthNumber);
        _stateHeaderProvider.FinalizedBlockNumber.Returns(finalizedNumber);
        NonceManager nonceManager = CreateSweepingNonceManager();
        for (int i = 0; i < SweepThreshold; i++)
        {
            AcceptRawNonce(nonceManager, TestItem.Addresses[i], 0);
        }

        TriggerSweep(nonceManager);

        _stateHeaderProvider.Received(1).GetFinalizedHeader(expectedNumber);
    }

    // 1. A sends a raw tx with nonce 1 while its account nonce is 0, so nonce 1 is still pending.
    // 2. Three other senders confirm their raw txs and a fifth sender triggers the sweep.
    // 3. A's managed reservations must still step over nonce 1: 0, then 2.
    [Test]
    public void TxWithNonceReceived_should_keep_a_sender_with_a_pending_nonce_through_a_sweep()
    {
        NonceManager nonceManager = CreateSweepingNonceManager();
        AcceptRawNonce(nonceManager, TestItem.AddressA, 1);
        for (int i = 1; i < SweepThreshold; i++)
        {
            AcceptRawNonce(nonceManager, TestItem.Addresses[i], 0);
            _headState.GetNonce(TestItem.Addresses[i]).Returns(1UL);
            SetReorgSafeNonce(TestItem.Addresses[i], 1);
        }

        TriggerSweep(nonceManager);

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(2), "precondition: only the sender with a pending nonce survives next to the new one");

        using (NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong first))
        {
            Assert.That(first, Is.EqualTo(0UL), "the account nonce is still free");
            locker.Accept();
        }

        using (nonceManager.ReserveNonce(TestItem.AddressA, out ulong second))
        {
            Assert.That(second, Is.EqualTo(2UL), "the pending raw nonce must still be skipped after the sweep");
        }
    }

    // A submission in progress holds its entry's lock, so the sweep must leave that entry alone even when every
    // nonce it recorded is confirmed.
    [Test]
    public void TxWithNonceReceived_should_keep_a_sender_whose_submission_is_in_progress_through_a_sweep()
    {
        NonceManager nonceManager = CreateSweepingNonceManager();
        using NonceLocker inProgress = nonceManager.TxWithNonceReceived(TestItem.AddressA, 0);
        inProgress.Accept();
        for (int i = 1; i < SweepThreshold; i++)
        {
            AcceptRawNonce(nonceManager, TestItem.Addresses[i], 0);
        }

        _headState.GetNonce(Arg.Any<Address>()).Returns(1UL);
        for (int i = 0; i < SweepThreshold; i++)
        {
            SetReorgSafeNonce(TestItem.Addresses[i], 1);
        }

        TriggerSweep(nonceManager);

        Assert.That(nonceManager.TrackedAddressCount, Is.EqualTo(2), "the busy sender must survive the sweep next to the new one");
    }

    [Test]
    public void ReserveNonce_should_start_from_a_high_account_nonce_without_walking_up_to_it()
    {
        const ulong accountNonce = 1_000_000_000_000;
        _headState.GetNonce(TestItem.AddressA).Returns(accountNonce);
        NonceManager nonceManager = CreateSweepingNonceManager();

        using (nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(accountNonce), "a new sender starts at its account nonce");
        }
    }

    [Test]
    public void should_reuse_nonce_if_tx_rejected()
    {
        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
            locker.Accept();
        }

        using (_nonceManager.TxWithNonceReceived(TestItem.AddressA, 1)) { }

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(1UL));
            locker.Accept();
        }
    }

    [Test]
    [Repeat(2)]
    public void should_lock_on_same_account()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Run(() =>
        {
            using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong _);
        });
        task.Wait(TimeSpan.FromMilliseconds(1_000));
        Assert.That(task.IsCompleted, Is.EqualTo(false));
    }

    [Test]
    [Repeat(3)]
    public void should_not_lock_on_different_accounts()
    {
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(0UL));
        Task task = Task.Factory.StartNew(() =>
        {
            using NonceLocker locker2 = _nonceManager.ReserveNonce(TestItem.AddressB, out ulong nonce2);
            Assert.That(nonce2, Is.EqualTo(0UL));
        }, TaskCreationOptions.LongRunning);
        Assert.That(task.Wait(TimeSpan.FromMilliseconds(10_000)), Is.True);
    }

    private void MakeReorgSafeStateUnavailable(ReorgSafeStateGap gap)
    {
        switch (gap)
        {
            case ReorgSafeStateGap.HeaderMissing:
                _stateHeaderProvider.GetFinalizedHeader(_reorgSafeHeader.Number).Returns((BlockHeader)null);
                break;
            case ReorgSafeStateGap.StatePruned:
                _stateReader.HasStateForBlock(_reorgSafeHeader).Returns(false);
                break;
            case ReorgSafeStateGap.NodeMissingOnRead:
                _stateReader.TryGetAccount(_reorgSafeHeader, Arg.Any<Address>(), out Arg.Any<AccountStruct>())
                    .Returns(_ => throw new MissingTrieNodeException("pruned", null, TreePath.Empty, Keccak.Zero));
                break;
        }
    }

    private NonceManager CreateSweepingNonceManager() =>
        new(_chainHead, _stateHeaderProvider, _stateReader, minSweepThreshold: SweepThreshold);

    private void SetReorgSafeNonce(Address address, ulong nonce) =>
        _stateReader.TryGetAccount(_reorgSafeHeader, address, out Arg.Any<AccountStruct>()).Returns(callInfo =>
        {
            callInfo[2] = new AccountStruct(nonce, UInt256.Zero);
            return true;
        });

    private static void AcceptRawNonce(NonceManager nonceManager, Address address, ulong nonce)
    {
        using NonceLocker locker = nonceManager.TxWithNonceReceived(address, nonce);
        locker.Accept();
    }

    private static void RejectRawNonce(NonceManager nonceManager, Address address, ulong nonce)
    {
        using NonceLocker locker = nonceManager.TxWithNonceReceived(address, nonce);
    }

    private static void TriggerSweep(NonceManager nonceManager) =>
        RejectRawNonce(nonceManager, TestItem.Addresses[SweepThreshold], 0);

    public enum ReorgSafeStateGap
    {
        HeaderMissing,
        StatePruned,
        NodeMissingOnRead,
    }
}
