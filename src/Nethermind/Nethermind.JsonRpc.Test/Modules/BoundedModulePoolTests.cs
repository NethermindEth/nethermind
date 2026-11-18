// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;
using BlockTree = Nethermind.Blockchain.BlockTree;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class BoundedModulePoolTests
{
    private BoundedModulePool<IEthRpcModule> _modulePool = null!;

    [SetUp]
    public Task Initialize()
    {
        ITxPool txPool = NullTxPool.Instance;

        BlockTree blockTree = Build.A
            .BlockTree()
            .TestObject;

        _modulePool = new BoundedModulePool<IEthRpcModule>(new EthModuleFactory(
            txPool,
            Substitute.For<ITxSender>(),
            NullWallet.Instance,
            blockTree,
            new JsonRpcConfig(),
            LimboLogs.Instance,
            Substitute.For<IStateReader>(),
            Substitute.For<IBlockchainBridgeFactory>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IGasPriceOracle>(),
            Substitute.For<IEthSyncingInfo>(),
            Substitute.For<IFeeHistoryOracle>(),
            Substitute.For<IProtocolsManager>(),
            new BlocksConfig(),
            Substitute.For<IForkInfo>(),
            new EthCapabilitiesProvider(
                blockTree.AsReadOnly(),
                Substitute.For<IStateBoundary>(),
                new SyncConfig(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory(),
            new HashesOnlyBlockReader(new MemDb(), new HeaderDecoder(), new ChainLevelInfoRepository(new MemDb()), Substitute.For<IHeaderStore>(), blockTree, Substitute.For<ISpecProvider>()),
            Substitute.For<IReceiptStorage>()),
             1, 1000);

        return Task.CompletedTask;
    }

    [Test]
    public async Task Ensure_concurrency() => await _modulePool.GetModule(false);

    [Test]
    public async Task Ensure_limited_exclusive()
    {
        await _modulePool.GetModule(false);
        await Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
    }

    [Test]
    public async Task Can_rent_available_module_when_another_pool_queue_is_full()
    {
        RpcLimits limits = new(queuedLimit: 1);
        BoundedModulePool<IEthRpcModule> busyPool = new(_modulePool.Factory, 1, 10_000, limits);
        BoundedModulePool<IEthRpcModule> availablePool = new(_modulePool.Factory, 1, 0, limits);
        for (int i = 0; i < 2; i++)
        {
            IEthRpcModule active = await busyPool.GetModule(false);
            Task<IEthRpcModule> queued = busyPool.GetModule(false);
            try
            {
                Assert.That(queued.IsCompleted, Is.False);
                await Assert.ThrowsAsync<LimitExceededException>(() => busyPool.GetModule(false));

                IEthRpcModule available = await availablePool.GetModule(false);
                availablePool.ReturnModule(available);
            }
            finally
            {
                busyPool.ReturnModule(active);
                busyPool.ReturnModule(await queued);
            }
        }
    }

    [TestCase(0, typeof(ModuleRentalTimeoutException))]
    [TestCase(-2, typeof(ArgumentOutOfRangeException))] // not a valid timeout: makes the wait itself throw
    public async Task Queue_slot_is_released_after_rental_failure(int timeout, Type expectedException)
    {
        BoundedModulePool<IEthRpcModule> emptyPool = new(_modulePool.Factory, 0, timeout, new RpcLimits(queuedLimit: 1));
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync(expectedException, () => emptyPool.GetModule(false));
        }
    }

    [Test]
    public async Task Cancelled_queued_rental_ends_at_once_and_frees_only_its_own_queue_slot()
    {
        BoundedModulePool<IEthRpcModule> pool = new(_modulePool.Factory, 1, Timeout.Infinite, new RpcLimits(queuedLimit: 1));
        IEthRpcModule held = await pool.GetModule(false);
        CancellationTokenSource abort = new();
        Task<IEthRpcModule> queued = pool.GetModule(false, abort.Token);
        // Twice, so that a second decrement of the slot (an undercount) shows up as one admission too many.
        for (int i = 0; i < 2; i++)
        {
            Assert.That(queued.IsCompleted, Is.False, "queued behind the held module");
            await abort.CancelAsync();
            OperationCanceledException? canceled = await Assert.CatchAsync<OperationCanceledException>(() => queued.WaitAsync(Watchdog));
            Assert.That(canceled!.CancellationToken, Is.EqualTo(abort.Token));

            abort.Dispose();
            abort = new();
            queued = pool.GetModule(false, abort.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queued, Is.Not.Null.And.Property(nameof(Task.IsCompleted)).False, "the freed slot admits exactly one waiter");
                Assert.That(pool.GetModule(false).Exception?.InnerException, Is.InstanceOf<LimitExceededException>(), "and no second one");
            }
        }

        pool.ReturnModule(held);
        Assert.That(await queued.WaitAsync(Watchdog), Is.SameAs(held));
        pool.ReturnModule(held);
        Assert.That(pool.GetModule(false).IsCompletedSuccessfully, Is.True);
        abort.Dispose();
    }

    [Test]
    public async Task Rental_granted_after_its_token_was_cancelled_gives_the_module_back()
    {
        BoundedModulePool<IEthRpcModule> pool = new(_modulePool.Factory, 1, Timeout.Infinite, new RpcLimits(queuedLimit: 1));
        IEthRpcModule held = await pool.GetModule(false);
        using CancellationTokenSource abort = new();
        ParkingSynchronizationContext parking = new();
        Task<IEthRpcModule> queued = parking.Start(() => pool.GetModule(false, abort.Token));

        // The return grants the wait, but its continuation is parked, so the token is cancelled before the rental sees the grant.
        pool.ReturnModule(held);
        await parking.Parked.WaitAsync(Watchdog);
        await abort.CancelAsync();
        parking.RunParked();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued.IsCanceled, Is.True);
            Assert.That(pool.GetModule(false).IsCompletedSuccessfully, Is.True, "the grant went back to the pool");
        }
    }

    [Test]
    public void Cancelled_token_does_not_affect_a_rental_that_does_not_queue([Values] bool canBeShared)
    {
        Task<IEthRpcModule> first = _modulePool.GetModule(canBeShared, new CancellationToken(canceled: true));
        Assert.That(first.IsCompletedSuccessfully, Is.True);
        _modulePool.ReturnModule(first.Result);
        Assert.That(_modulePool.GetModule(canBeShared, new CancellationToken(canceled: true)), Is.SameAs(first));
    }

    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    /// <summary>Holds the one continuation posted to it until the test runs it.</summary>
    private sealed class ParkingSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<Action> _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Parked => _parked.Task;
        public override void Post(SendOrPostCallback d, object? state) => _parked.SetResult(() => d(state));
        public void RunParked() => _parked.Task.Result();

        /// <summary>Calls <paramref name="start"/> with this context current, so the awaits it begins resume here.</summary>
        public T Start<T>(Func<T> start)
        {
            SynchronizationContext? outer = Current;
            SetSynchronizationContext(this);
            try
            {
                return start();
            }
            finally
            {
                SetSynchronizationContext(outer);
            }
        }
    }

    [Test]
    public async Task Ensure_returning_shared_does_not_change_concurrency()
    {
        IEthRpcModule shared = await _modulePool.GetModule(true);
        _modulePool.ReturnModule(shared);
        await _modulePool.GetModule(false);
        await Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
    }

    [Test]
    public async Task Ensure_unlimited_shared()
    {
        for (int i = 0; i < 1000; i++)
        {
            await _modulePool.GetModule(true);
        }
    }

    [Test]
    public async Task Ensure_that_shared_is_never_returned_as_exclusive()
    {
        IEthRpcModule sharedRpcModule = await _modulePool.GetModule(true);
        _modulePool.ReturnModule(sharedRpcModule);

        const int iterations = 1000;
        async Task rentReturnShared()
        {
            for (int i = 0; i < iterations; i++)
            {
                // TestContext.Out.WriteLine($"Rent shared {i}");
                IEthRpcModule ethRpcModule = await _modulePool.GetModule(true);
                Assert.That(ethRpcModule, Is.SameAs(sharedRpcModule));
                _modulePool.ReturnModule(ethRpcModule);
                // TestContext.Out.WriteLine($"Return shared {i}");
            }
        }

        async Task rentReturnExclusive()
        {
            for (int i = 0; i < iterations; i++)
            {
                // TestContext.Out.WriteLine($"Rent exclusive {i}");
                IEthRpcModule ethRpcModule = await _modulePool.GetModule(false);
                Assert.That(ethRpcModule, Is.Not.SameAs(sharedRpcModule));
                _modulePool.ReturnModule(ethRpcModule);
                // TestContext.Out.WriteLine($"Return exclusive {i}");
            }
        }

        Task a = Task.Run(rentReturnExclusive);
        Task b = Task.Run(rentReturnExclusive);
        Task c = Task.Run(rentReturnShared);
        Task d = Task.Run(rentReturnShared);

        await Task.WhenAll(a, b, c, d);
    }

    [Test]
    public async Task Can_rent_and_return([Values] bool canBeShared)
    {
        IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
        _modulePool.ReturnModule(ethRpcModule);
    }

    [Test]
    public async Task Available_module_is_rented_as_the_same_completed_task([Values] bool canBeShared)
    {
        Task<IEthRpcModule> first = _modulePool.GetModule(canBeShared);
        _modulePool.ReturnModule(await first);

        Task<IEthRpcModule> second = _modulePool.GetModule(canBeShared);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.IsCompletedSuccessfully, Is.True);
            Assert.That(second, Is.SameAs(first));
        }
    }

    [Test]
    public async Task Can_rent_and_return_in_a_loop([Values] bool canBeShared)
    {
        for (int i = 0; i < 1000; i++)
        {
            IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
            _modulePool.ReturnModule(ethRpcModule);
        }
    }
}
