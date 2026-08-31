// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
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
            Substitute.For<ILogIndexConfig>(),
            new ReceiptConfig(),
            new EthCapabilitiesProvider(
                blockTree.AsReadOnly(),
                Substitute.For<IStateBoundary>(),
                new SyncConfig(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory()),
             1, 1000);

        return Task.CompletedTask;
    }

    [Test]
    public async Task Ensure_concurrency() => await _modulePool.GetModule(false);

    [Test]
    public async Task Ensure_limited_exclusive()
    {
        await _modulePool.GetModule(false);
        Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
    }

    [Test]
    public async Task Ensure_returning_shared_does_not_change_concurrency()
    {
        IEthRpcModule shared = await _modulePool.GetModule(true);
        _modulePool.ReturnModule(shared);
        await _modulePool.GetModule(false);
        Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => _modulePool.GetModule(false));
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

    [TestCase(true)]
    [TestCase(false)]
    public async Task Can_rent_and_return(bool canBeShared)
    {
        IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
        _modulePool.ReturnModule(ethRpcModule);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Can_rent_and_return_in_a_loop(bool canBeShared)
    {
        for (int i = 0; i < 1000; i++)
        {
            IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
            _modulePool.ReturnModule(ethRpcModule);
        }
    }

    [Test]
    public async Task Creates_instances_lazily_up_to_capacity_and_never_beyond()
    {
        const int capacity = 2;
        BoundedModulePool<IEthRpcModule> pool = CreateCountingPool(capacity, out IRpcModuleFactory<IEthRpcModule> factory);
        factory.DidNotReceive().Create();

        IEthRpcModule first = await pool.GetModule(false);
        factory.Received(1).Create();
        IEthRpcModule second = await pool.GetModule(false);
        factory.Received(2).Create();

        pool.ReturnModule(first);
        pool.ReturnModule(second);
        pool.ReturnModule(await pool.GetModule(false));
        factory.Received(2).Create();

        await pool.GetModule(false);
        await pool.GetModule(false);
        Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => pool.GetModule(false));
        factory.Received(2).Create();

        pool.ReturnModule(await pool.GetModule(true));
        factory.Received(3).Create();
    }

    [Test]
    public void Preload_creates_every_instance_once()
    {
        const int capacity = 3;
        BoundedModulePool<IEthRpcModule> pool = CreateCountingPool(capacity, out IRpcModuleFactory<IEthRpcModule> factory);

        pool.Preload();
        pool.Preload();

        factory.Received(capacity + 1).Create();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Factory_failure_during_lazy_rent_does_not_consume_capacity(bool canBeShared)
    {
        const int capacity = 2;
        BoundedModulePool<IEthRpcModule> pool = CreateCountingPool(capacity, out IRpcModuleFactory<IEthRpcModule> factory);
        factory.Create().Returns(static _ => throw new InvalidOperationException("boom"), static _ => Substitute.For<IEthRpcModule>());

        Assert.ThrowsAsync<InvalidOperationException>(() => pool.GetModule(canBeShared));

        pool.ReturnModule(await pool.GetModule(canBeShared));
        for (int i = 0; i < capacity; i++)
        {
            await pool.GetModule(false);
        }
        Assert.ThrowsAsync<ModuleRentalTimeoutException>(() => pool.GetModule(false), "the failed instance must not count towards the capacity");
    }

    [Test]
    public async Task Factory_create_calls_are_serialized_per_pool()
    {
        const int capacity = 4;
        BoundedModulePool<IEthRpcModule> pool = CreateCountingPool(capacity, out IRpcModuleFactory<IEthRpcModule> factory);
        using ManualResetEventSlim firstEntered = new();
        using ManualResetEventSlim release = new();
        int active = 0;
        int maximumActive = 0;
        factory.Create().Returns(_ =>
        {
            int current = Interlocked.Increment(ref active);
            InterlockedMax(ref maximumActive, current);
            firstEntered.Set();
            Assert.That(release.Wait(TimeSpan.FromSeconds(10)), Is.True, "factory did not get released");
            Interlocked.Decrement(ref active);
            return Substitute.For<IEthRpcModule>();
        });

        Task<IEthRpcModule>[] rentals = new Task<IEthRpcModule>[capacity];
        for (int i = 0; i < rentals.Length; i++)
        {
            rentals[i] = Task.Run(() => pool.GetModule(false));
        }

        Assert.That(firstEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
        release.Set();
        IEthRpcModule[] modules = await Task.WhenAll(rentals).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (IEthRpcModule module in modules)
        {
            pool.ReturnModule(module);
        }

        Assert.That(maximumActive, Is.EqualTo(1));

        static void InterlockedMax(ref int location, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref location);
                if (observed >= value) return;
            }
            while (Interlocked.CompareExchange(ref location, value, observed) != observed);
        }
    }

    [Test]
    public void Auto_factory_disposes_the_child_scope_after_repeated_resolution_failures()
    {
        const int attempts = 10;
        TrackingDisposable.Reset();
        ContainerBuilder builder = new();
        builder.RegisterType<TrackingDisposable>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<FailingRpcModule>().As<IFailingRpcModule>().InstancePerLifetimeScope();
        using IContainer root = builder.Build();
        AutoRpcModuleFactory<IFailingRpcModule> factory = new(root);
        BoundedModulePool<IFailingRpcModule> pool = new(factory, exclusiveCapacity: 1, timeout: 1000);

        for (int i = 0; i < attempts; i++)
        {
            Assert.ThrowsAsync<DependencyResolutionException>(() => pool.GetModule(false));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrackingDisposable.Created, Is.EqualTo(attempts));
            Assert.That(TrackingDisposable.Disposed, Is.EqualTo(attempts));
        }
    }

    private static BoundedModulePool<IEthRpcModule> CreateCountingPool(int capacity, out IRpcModuleFactory<IEthRpcModule> factory)
    {
        factory = Substitute.For<IRpcModuleFactory<IEthRpcModule>>();
        factory.Create().Returns(static _ => Substitute.For<IEthRpcModule>());
        return new BoundedModulePool<IEthRpcModule>(factory, capacity, 50);
    }

    [RpcModule(ModuleType.Eth)]
    private interface IFailingRpcModule : IRpcModule;

    private sealed class FailingRpcModule : IFailingRpcModule
    {
        public FailingRpcModule(TrackingDisposable dependency)
        {
            _ = dependency;
            throw new InvalidOperationException("module activation failed");
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public static int Created;
        public static int Disposed;

        public TrackingDisposable() => Interlocked.Increment(ref Created);

        public static void Reset()
        {
            Created = 0;
            Disposed = 0;
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }
}
