// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEthModuleFactoryTests
{
    // The eth module is pooled, so two instances of one factory must still send one at a time for one account.
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

        OptimismEthModuleFactory factory = new(
            new JsonRpcConfig(),
            Substitute.For<IBlockchainBridgeFactory>(),
            Substitute.For<IBlockFinder>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IEthCapabilitiesProvider>(),
            Substitute.For<IStateReader>(),
            Substitute.For<ITxPool>(),
            txSender,
            wallet,
            LimboLogs.Instance,
            Substitute.For<ISpecProvider>(),
            Substitute.For<IGasPriceOracle>(),
            Substitute.For<IEthSyncingInfo>(),
            Substitute.For<IFeeHistoryOracle>(),
            Substitute.For<IProtocolsManager>(),
            Substitute.For<IForkInfo>(),
            new BlocksConfig(),
            Substitute.For<IEthereumEcdsa>(),
            Substitute.For<IOptimismSpecHelper>(),
            new OptimismConfig(),
            new EthereumJsonSerializer(),
            Substitute.For<ITimestamper>(),
            Substitute.For<IBlockForRpcFactory>());

        static LegacyTransactionForRpc Request() => new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressA,
            Value = 1,
            Gas = 21000,
            GasPrice = 1,
        };

        Task<ResultWrapper<Hash256>> first = factory.Create().eth_sendTransaction(Request());
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResultWrapper<Hash256>> second = factory.Create().eth_sendTransaction(Request());
        bool secondSentWhileFirstHeld = await Task.WhenAny(secondArrived.Task, Task.Delay(TimeSpan.FromMilliseconds(500))) == secondArrived.Task;
        releaseFirst.SetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await first).Result.ResultType, Is.EqualTo(ResultType.Success), (await first).Result.Error);
            Assert.That((await second).Result.ResultType, Is.EqualTo(ResultType.Success), (await second).Result.Error);
            Assert.That(secondSentWhileFirstHeld, Is.False, "instances of one factory must share the lock of an account");
        }
    }
}
