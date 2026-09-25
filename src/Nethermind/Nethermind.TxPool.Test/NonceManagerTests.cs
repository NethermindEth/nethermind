// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
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
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NonceManagerTests
{
    private ISpecProvider _specProvider;
    private TestReadOnlyStateProvider _stateProvider;
    private IBlockTree _blockTree;
    private ChainHeadInfoProvider _headInfo;
    private IStateReader _stateReader;
    private IStateHeaderProvider _stateHeaderProvider;
    private ConcurrentDictionary<Address, ulong> _finalizedNonces;
    private NonceManager _nonceManager;

    private static readonly TimeSpan EvictionTimeout = TimeSpan.FromMilliseconds(10_000);

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
        _finalizedNonces = new();
        _stateHeaderProvider = Substitute.For<IStateHeaderProvider>();
        _stateHeaderProvider.GetFinalizedHeader(Arg.Any<ulong>()).Returns(Build.A.BlockHeader.TestObject);
        _stateReader = Substitute.For<IStateReader>();
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        _stateReader.TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(ReadFinalizedAccount);
        _nonceManager = CreateNonceManager(_headInfo);
    }

    [TearDown]
    public void TearDown() => _nonceManager.Dispose();

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
        IReadOnlyStateProvider accountStateProvider = Substitute.For<IReadOnlyStateProvider>();
        accountStateProvider.GetNonce(TestItem.AddressA).Returns(0UL);
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.ReadOnlyStateProvider.Returns(accountStateProvider);
        _nonceManager.Dispose();
        _nonceManager = CreateNonceManager(headInfo);

        using (_nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce))
        {
            Assert.That(nonce, Is.EqualTo(0UL));
        }

        accountStateProvider.GetNonce(TestItem.AddressA).Returns(10UL);
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

    [Test]
    public void TxWithNonceReceived_releases_nonces_below_account_nonce()
    {
        for (ulong nonce = 0; nonce < 3; nonce++)
        {
            AcceptRawTx(TestItem.AddressA, nonce);
        }

        _stateProvider.CreateAccount(TestItem.AddressA, 0, 3);
        AcceptRawTx(TestItem.AddressA, 3);

        Assert.That(_nonceManager.TryGetUsedNonceCount(TestItem.AddressA, out int usedNonceCount), Is.True);
        Assert.That(usedNonceCount, Is.EqualTo(1));
    }

    /// <remarks>
    /// After the sweep the head nonce drops to 1, as a reorg would, so the next reservation shows whether the entry
    /// survived: a kept entry continues from its counter, an evicted one restarts from the head nonce.
    /// </remarks>
    [TestCase(0UL, 2, 1UL, 2UL, TestName = "Nonce_accepted_above_the_finalized_nonce_survives_a_head_reorg")]
    [TestCase(0UL, 2, 2UL, 1UL, TestName = "Entry_is_evicted_once_the_finalized_nonce_covers_every_accepted_nonce")]
    [TestCase(3UL, 0, 1UL, 3UL, TestName = "Entry_whose_counter_is_above_the_finalized_nonce_is_kept")]
    public async Task Sweep_evicts_only_what_the_finalized_nonce_covers(ulong reservationNonce, int acceptedReservations, ulong finalizedNonce, ulong expectedNonceAfterReorg)
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 0, reservationNonce);
        for (int i = 0; i < acceptedReservations; i++)
        {
            AcceptReservation(_nonceManager, TestItem.AddressA);
        }

        using (_nonceManager.ReserveNonce(TestItem.AddressA, out _)) { }
        _stateProvider.CreateAccount(TestItem.AddressA, 0, reservationNonce + (ulong)acceptedReservations);
        _finalizedNonces[TestItem.AddressA] = finalizedNonce;

        await ProcessHead();

        _stateProvider.CreateAccount(TestItem.AddressA, 0, 1);
        using NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(expectedNonceAfterReorg));
    }

    [Test]
    public async Task Only_never_used_entries_are_evicted_when_finalized_state_is_unavailable([Values] bool headerMissing)
    {
        AcceptReservation(_nonceManager, TestItem.AddressA);
        _finalizedNonces[TestItem.AddressA] = 1;
        using (_nonceManager.TxWithNonceReceived(TestItem.AddressB, 5)) { }
        if (headerMissing)
        {
            _stateHeaderProvider.GetFinalizedHeader(Arg.Any<ulong>()).Returns((BlockHeader)null);
        }
        else
        {
            _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(false);
        }

        await ProcessHead();

        using (Assert.EnterMultipleScope())
        {
            _stateReader.DidNotReceive().TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>());
            Assert.That(_nonceManager.TryGetUsedNonceCount(TestItem.AddressA, out _), Is.True, "an entry with an accepted nonce needs state to be evicted");
            Assert.That(_nonceManager.TryGetUsedNonceCount(TestItem.AddressB, out _), Is.False, "a never-used entry is evicted without state");
        }
    }

    [Test]
    public async Task Never_used_entries_are_evicted_without_a_state_read()
    {
        AcceptReservation(_nonceManager, TestItem.AddressA);
        _finalizedNonces[TestItem.AddressA] = 1;
        using (_nonceManager.TxWithNonceReceived(TestItem.AddressB, 5)) { }

        await ProcessHead();

        using (Assert.EnterMultipleScope())
        {
            _stateReader.Received(1).TryGetAccount(Arg.Any<BlockHeader>(), TestItem.AddressA, out Arg.Any<AccountStruct>());
            _stateReader.DidNotReceive().TryGetAccount(Arg.Any<BlockHeader>(), TestItem.AddressB, out Arg.Any<AccountStruct>());
            Assert.That(_nonceManager.TryGetUsedNonceCount(TestItem.AddressA, out _), Is.False);
            Assert.That(_nonceManager.TryGetUsedNonceCount(TestItem.AddressB, out _), Is.False);
        }
    }

    [TestCase(900UL, 900UL, TestName = "Sweep_reads_at_the_finalized_block")]
    [TestCase(990UL, 936UL, TestName = "Sweep_reads_at_most_max_reorg_depth_below_the_head")]
    public async Task Sweep_reads_the_finalized_nonce_at_a_finality_safe_block(ulong finalizedBlockNumber, ulong expectedBlockNumber)
    {
        _stateHeaderProvider.FinalizedBlockNumber.Returns(finalizedBlockNumber);
        AcceptReservation(_nonceManager, TestItem.AddressA);

        await ProcessHead(headNumber: 1000);

        _stateHeaderProvider.Received(1).GetFinalizedHeader(expectedBlockNumber);
    }

    [Test]
    public async Task Sweep_survives_a_failing_finalized_nonce_read([Values] bool stateMissing)
    {
        Address[] addresses = TrackAddressesFinalizedAt(1);

        Exception failure = stateMissing
            ? new MissingTrieNodeException("Finalized state was pruned", null, TreePath.Empty, Keccak.Zero)
            : new InvalidOperationException("Corrupted account");
        int reads = 0;
        _stateReader.TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(callInfo =>
        {
            if (Interlocked.Increment(ref reads) == 1) throw failure;
            return ReadFinalizedAccount(callInfo);
        });

        Task eviction = ProcessHead();
        await eviction;

        int tracked = addresses.Count(address => _nonceManager.TryGetUsedNonceCount(address, out _));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(eviction.IsCompletedSuccessfully, Is.True);
            Assert.That(tracked, Is.EqualTo(stateMissing ? addresses.Length : 1), "a missing state stops the sweep, any other failure skips only its entry");
        }
    }

    /// <remarks>
    /// The first read is held until <see cref="NonceManager.Dispose"/> unsubscribes, which it does after flagging the
    /// disposal and before waiting for the sweep, and then runs on well within the dispose bound, so a Dispose that
    /// does not wait returns while the read is still in flight.
    /// </remarks>
    [Test]
    public void Dispose_stops_a_running_sweep_and_waits_for_its_read()
    {
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        _nonceManager.Dispose();
        _nonceManager = CreateNonceManager(headInfo);
        TrackAddressesFinalizedAt(1);

        using ManualResetEventSlim readStarted = new();
        using ManualResetEventSlim unsubscribed = new();
        headInfo.When(h => h.HeadChanged -= Arg.Any<EventHandler<BlockReplacementEventArgs>>()).Do(_ => unsubscribed.Set());
        _stateReader.TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(callInfo =>
        {
            readStarted.Set();
            unsubscribed.Wait(EvictionTimeout);
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            return ReadFinalizedAccount(callInfo);
        });

        headInfo.HeadChanged += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
        Assert.That(readStarted.Wait(EvictionTimeout), Is.True, "the sweep must start reading");
        _nonceManager.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_nonceManager.Eviction.IsCompleted, Is.True, "Dispose returned before the in-flight read");
            _stateReader.Received(1).TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>());
        }
    }

    [Test]
    public void Dispose_does_not_wait_indefinitely_for_a_stuck_read()
    {
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        _nonceManager.Dispose();
        _nonceManager = CreateNonceManager(headInfo);
        TrackAddressesFinalizedAt(1);

        using ManualResetEventSlim readStarted = new();
        using ManualResetEventSlim release = new();
        _stateReader.TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(callInfo =>
        {
            readStarted.Set();
            release.Wait(EvictionTimeout);
            return ReadFinalizedAccount(callInfo);
        });

        headInfo.HeadChanged += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.TestObject));
        Assert.That(readStarted.Wait(EvictionTimeout), Is.True, "the sweep must start reading");
        Task disposal = Task.Run(_nonceManager.Dispose);
        bool disposed = disposal.Wait(TimeSpan.FromSeconds(5));
        release.Set();

        Assert.That(disposed, Is.True, "Dispose must return while a read is stuck");
    }

    [Test]
    public async Task Head_after_dispose_starts_no_sweep([Values] bool sweepQueuedBeforeDispose)
    {
        AcceptReservation(_nonceManager, TestItem.AddressA);
        _nonceManager.Dispose();

        if (sweepQueuedBeforeDispose) _nonceManager.EvictCaughtUpAddresses();
        else await ProcessHead();

        _stateHeaderProvider.DidNotReceive().GetFinalizedHeader(Arg.Any<ulong>());
    }

    [Test]
    public void Sweep_skips_address_whose_lock_is_held()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 0, 5);
        _finalizedNonces[TestItem.AddressA] = 5;

        using (NonceLocker locker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong reservedNonce))
        {
            Assert.That(reservedNonce, Is.EqualTo(5UL));
            Assert.That(ProcessHead().Wait(EvictionTimeout), Is.True, "eviction must not wait on a held lock");
            locker.Accept();
        }

        using NonceLocker nextLocker = _nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(6UL));
    }

    [Test]
    public void Reservation_racing_an_eviction_does_not_reuse_an_accepted_nonce()
    {
        ulong minedNonce = 0;
        bool sweepDuringNextRead = false;
        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        using NonceManager nonceManager = CreateNonceManager(headInfo);
        headInfo.ReadOnlyStateProvider.GetNonce(TestItem.AddressA).Returns(_ =>
        {
            ulong nonceBeforeHead = minedNonce;
            if (sweepDuringNextRead)
            {
                sweepDuringNextRead = false;
                minedNonce = 2;
                _finalizedNonces[TestItem.AddressA] = minedNonce;
                nonceManager.EvictCaughtUpAddresses();
            }

            return nonceBeforeHead;
        });

        AcceptReservation(nonceManager, TestItem.AddressA);
        AcceptReservation(nonceManager, TestItem.AddressA);
        sweepDuringNextRead = true;

        using NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
        Assert.That(nonce, Is.EqualTo(2UL));
    }

    [Test]
    public void Concurrent_reservations_and_evictions_never_share_a_nonce_or_a_lock([Values(1, 2)] int sweepers)
    {
        const int workers = 4;
        const int reservationsPerWorker = 20_000;
        long minedNonce = 0;
        long acceptedCount = 0;
        long holders = 0;
        long maxHolders = 0;
        ConcurrentBag<ulong> acceptedNonces = [];

        IChainHeadInfoProvider headInfo = Substitute.For<IChainHeadInfoProvider>();
        headInfo.ReadOnlyStateProvider.GetNonce(TestItem.AddressA).Returns(_ => (ulong)Interlocked.Read(ref minedNonce));
        _stateReader.TryGetAccount(Arg.Any<BlockHeader>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(callInfo =>
        {
            callInfo[2] = new AccountStruct((ulong)Interlocked.Read(ref minedNonce), UInt256.Zero);
            return true;
        });
        using NonceManager nonceManager = CreateNonceManager(headInfo);

        Task[] reservers = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < reservationsPerWorker; i++)
            {
                using NonceLocker locker = nonceManager.ReserveNonce(TestItem.AddressA, out ulong nonce);
                InterlockedMax(ref maxHolders, Interlocked.Increment(ref holders));
                if ((i + worker) % 4 != 0)
                {
                    locker.Accept();
                    acceptedNonces.Add(nonce);
                    Interlocked.Increment(ref acceptedCount);
                }

                Interlocked.Decrement(ref holders);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        Task allReserved = Task.WhenAll(reservers);
        Task[] sweeps = Enumerable.Range(0, sweepers).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!allReserved.IsCompleted)
            {
                // Only ever raised, so the head nonce a fresh manager reads is never below a finalized one already swept.
                InterlockedMax(ref minedNonce, Interlocked.Read(ref acceptedCount));
                nonceManager.EvictCaughtUpAddresses();
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        Assert.That(Task.WhenAll([allReserved, .. sweeps]).Wait(TimeSpan.FromMinutes(1)), Is.True, "reservations and sweeps must finish");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(acceptedNonces.Distinct().Count(), Is.EqualTo(acceptedNonces.Count), "duplicate nonces");
            Assert.That(maxHolders, Is.EqualTo(1), "concurrent holders of one address");
        }
    }

    private Address[] TrackAddressesFinalizedAt(ulong nonce)
    {
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD];
        foreach (Address address in addresses)
        {
            AcceptReservation(_nonceManager, address);
            _finalizedNonces[address] = nonce;
        }

        return addresses;
    }

    private NonceManager CreateNonceManager(IChainHeadInfoProvider headInfo) =>
        new(headInfo, _stateReader, _stateHeaderProvider, LimboLogs.Instance);

    private bool ReadFinalizedAccount(CallInfo callInfo)
    {
        bool exists = _finalizedNonces.TryGetValue(callInfo.ArgAt<Address>(1), out ulong nonce);
        callInfo[2] = exists ? new AccountStruct(nonce, UInt256.Zero) : default;
        return exists;
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref target);
        } while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    private void AcceptRawTx(Address address, ulong nonce)
    {
        using NonceLocker locker = _nonceManager.TxWithNonceReceived(address, nonce);
        locker.Accept();
    }

    private static void AcceptReservation(INonceManager nonceManager, Address address)
    {
        using NonceLocker locker = nonceManager.ReserveNonce(address, out _);
        locker.Accept();
    }

    private Task ProcessHead(ulong headNumber = 1)
    {
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.WithNumber(headNumber).TestObject));
        return _nonceManager.Eviction.WaitAsync(EvictionTimeout);
    }
}
