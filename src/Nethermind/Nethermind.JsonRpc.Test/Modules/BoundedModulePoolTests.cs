// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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

[NonParallelizable]
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
    public async Task Can_rent_available_module_when_another_pool_queue_is_full()
    {
        RpcLimits.Init(1, 0);
        BoundedModulePool<IEthRpcModule> busyPool = new(_modulePool.Factory, 1, -1);
        BoundedModulePool<IEthRpcModule> availablePool = new(_modulePool.Factory, 1, 0);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                IEthRpcModule active = await busyPool.GetModule(false);
                Task<IEthRpcModule> queued = busyPool.GetModule(false);
                try
                {
                    Assert.That(queued.IsCompleted, Is.False);
                    Assert.ThrowsAsync<LimitExceededException>(() => busyPool.GetModule(false));

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
        finally
        {
            RpcLimits.Init(0, 0);
        }
    }

    [Test]
    public void Queue_slot_is_released_after_rental_failure([Values(0, -2)] int timeout)
    {
        RpcLimits.Init(1, 0);
        BoundedModulePool<IEthRpcModule> emptyPool = new(_modulePool.Factory, 0, timeout);
        Type exceptionType = timeout == 0 ? typeof(ModuleRentalTimeoutException) : typeof(ArgumentOutOfRangeException);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                Assert.ThrowsAsync(exceptionType, () => emptyPool.GetModule(false));
            }
        }
        finally
        {
            RpcLimits.Init(0, 0);
        }
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

    [Test]
    public async Task Can_rent_and_return([Values] bool canBeShared)
    {
        IEthRpcModule ethRpcModule = await _modulePool.GetModule(canBeShared);
        _modulePool.ReturnModule(ethRpcModule);
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
