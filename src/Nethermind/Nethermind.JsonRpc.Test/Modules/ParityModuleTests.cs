using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class ParityModuleTests
    {
        private IParityModule _parityModule;

        [SetUp]
        public void Initialize()
        {
            var specProvider = MainNetSpecProvider.Instance;
            var ethereumEcdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            var txStorage = new InMemoryTxStorage();
            var txPool = new TxPool(txStorage, Timestamp.Default, ethereumEcdsa, specProvider, new TxPoolConfig(),
                LimboLogs.Instance);
            _parityModule = new ParityModule(new EthereumEcdsa(specProvider, LimboLogs.Instance),
                new TransactionDecoder(), txPool, LimboLogs.Instance);
            var blockNumber = 1;
            var transaction = Build.A.Transaction.Signed(ethereumEcdsa, TestItem.PrivateKeyD, blockNumber)
                .WithSenderAddress(Address.FromNumber(blockNumber)).TestObject;
            transaction.Signature.V = 37;
            txPool.AddTransaction(transaction, blockNumber);
        }

        [Test]
        public void parity_pendingTransactions()
        {
            string serialized = RpcTest.TestSerializedRequest(_parityModule, "parity_pendingTransactions");
            var expectedResult = "[{\"hash\":\"0xd4720d1b81c70ed4478553a213a83bd2bf6988291677f5d05c6aae0b287f947e\",\"nonce\":\"0x0\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"raw\":\"0x\",\"creates\":null,\"publicKey\":\"0x15a1cc027cfd2b970c8aa2b3b22dfad04d29171109f6502d5fb5bde18afe86dddd44b9f8d561577527f096860ee03f571cc7f481ea9a14cb48cc7c20c964373a\",\"chainId\":1,\"condition\":null,\"r\":\"0xef2effb79771cbe42fc7f9cc79440b2a334eedad6e528ea45c2040789def4803\",\"s\":\"0x515bdfe298808be2e07879faaeacd0ad17f3b13305b9f971647bbd5d5b584642\",\"v\":\"0x25\",\"standardV\":\"0x0\"}]";
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":{expectedResult}}}", serialized);
        }
    }
}