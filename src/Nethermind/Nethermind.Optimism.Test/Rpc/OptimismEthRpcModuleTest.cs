// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEthRpcModuleTest
{
    [Test]
    public async Task Sequencer_send_transaction_with_signature_will_not_try_to_sign()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast)
            .Returns(returnThis: (TestItem.KeccakA, AcceptTxResult.Accepted));

        EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(chainId: TestBlockchainIds.ChainId);
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithTxSender(txSender)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null /* explicitly using null to behave as Sequencer */,
                worldStateManager: Substitute.For<IWorldStateManager>(),
                ecdsa: new OptimismEthereumEcdsa(ethereumEcdsa),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

        Transaction tx = Build.A.Transaction
            .Signed(ecdsa: ethereumEcdsa, privateKey: TestItem.PrivateKeyA)
            .TestObject;
        string serialized = await rpcBlockchain.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(item: tx, behaviors: RlpBehaviors.None).Bytes.ToHexString());

        await txSender.Received().SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast);
        Assert.That(actual: serialized, expression: Is.EqualTo(expected: $$"""{"jsonrpc":"2.0","result":"{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}","id":67}"""));
    }
}

internal static class TestRpcBlockchainExt
{
    public static TestRpcBlockchain.Builder<TestRpcBlockchain> WithOptimismEthRpcModule(
        this TestRpcBlockchain.Builder<TestRpcBlockchain> @this,
        IJsonRpcClient? sequencerRpcClient,
        IWorldStateManager worldStateManager,
        IEthereumEcdsa ecdsa,
        ITxSealer sealer,
        IOptimismSpecHelper opSpecHelper)
    {
        return @this.WithEthRpcModule(blockchain => new OptimismEthRpcModule(
            blockchain.RpcConfig,
            blockchain.Bridge,
            blockchain.BlockFinder,
            blockchain.ReceiptFinder,
            blockchain.StateReader,
            blockchain.TxPool,
            blockchain.TxSender,
            blockchain.TestWallet,
            LimboLogs.Instance,
            blockchain.SpecProvider,
            blockchain.GasPriceOracle,
            new EthSyncingInfo(blockchain.BlockTree, blockchain.ReceiptStorage, new SyncConfig(),
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), blockchain.LogManager),
            blockchain.FeeHistoryOracle ??
            new FeeHistoryOracle(blockchain.BlockTree, blockchain.ReceiptStorage, blockchain.SpecProvider),
            new BlocksConfig().SecondsPerSlot,

            sequencerRpcClient, worldStateManager, ecdsa, sealer, opSpecHelper
        ));
    }
}
