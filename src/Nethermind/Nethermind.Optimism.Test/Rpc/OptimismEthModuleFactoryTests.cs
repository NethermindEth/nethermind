// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Optimism.Rpc;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEthModuleFactoryTests
{
    // The factory creates several eth module instances (pooled and shared), so two of them must still send one at a time for one account.
    [Test]
    public async Task Module_instances_of_one_factory_send_one_at_a_time_for_one_account()
    {
        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;

        async ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)> Hold()
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstArrived.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondArrived.TrySetResult();
            }

            return (TestItem.KeccakA, AcceptTxResult.Accepted);
        }

        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ => Hold());
        IWallet wallet = Substitute.For<IWallet>();
        wallet.TrySignTransaction(Arg.Any<Transaction>(), Arg.Any<ulong>()).Returns(true);
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.Genesis.TestObject,
            Parameters = new ChainParameters(),
            Allocations = [],
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0 })
        };
        // The node's own wiring of a sequencer node. The test brings the pool sender, the wallet and a spec provider, as the
        // chain spec has no Optimism fork parameters.
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, new OptimismConfig()))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(London.Instance))
            .AddSingleton<ITxSender>(txSender)
            .AddSingleton<IWallet>(wallet)
            .Build();
        // The factory the eth_ module pool creates its instances with.
        IRpcModuleFactory<IEthRpcModule> factory = container.Resolve<IRpcModuleFactory<IEthRpcModule>>();
        OptimismEthRpcModule firstModule = (OptimismEthRpcModule)factory.Create();

        static LegacyTransactionForRpc Request() => new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressA,
            Value = 1,
            Gas = 21000,
            GasPrice = 1,
        };

        Task<ResultWrapper<Hash256>> first = firstModule.eth_sendTransaction(Request());
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResultWrapper<Hash256>> second = factory.Create().eth_sendTransaction(Request());
        bool secondWaited = OptimismEthRpcModuleTest.SecondSendWaitsForTheFirst(firstModule.NonceTracker, TestItem.AddressC, secondArrived.Task);
        releaseFirst.SetResult();
        ResultWrapper<Hash256> firstResult = await first.WaitAsync(TimeSpan.FromSeconds(30));
        ResultWrapper<Hash256> secondResult = await second.WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.Result.ResultType, Is.EqualTo(ResultType.Success), firstResult.Result.Error);
            Assert.That(secondResult.Result.ResultType, Is.EqualTo(ResultType.Success), secondResult.Result.Error);
            Assert.That(secondWaited, Is.True, "instances of one factory must share the lock of an account");
        }
    }
}
