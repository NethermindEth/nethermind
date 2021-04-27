//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.JsonRpc.Test;
using Newtonsoft.Json;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc;
using Nethermind.Facade;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class MevRpcModuleTests
    {
        private class TestMevRpcBlockchain : TestBlockchain
        {
            public IMevRpcModule MevRpcModule { get; set; } = Substitute.For<IMevRpcModule>();
            public IEthRpcModule EthRpcModule { get; set; } = Substitute.For<IEthRpcModule>();
            // bad, TODO change to have a receipt field directly without bridge
            public IBlockchainBridge BlockchainBridge { get; set; } = Substitute.For<IBlockchainBridge>();
            public TestMevRpcBlockchain() {}
            // TODO add json converters, rewrite method
            public string TestMevRpcWithSerialization(string method, params string[] parameters)
            {
                List<JsonConverter> converters = new();
                return RpcTest.TestSerializedRequest(converters, MevRpcModule, method, parameters);
            }
        }

        private TestMevRpcBlockchain CreateChain()
        {
            return new TestMevRpcBlockchain();
        }

        private static IEnumerable<Keccak?> GetHashes(IEnumerable<Transaction> bundle2Txs) => bundle2Txs.Select(t => t.Hash);

        private static class Contracts
        {
            public static string CoinbaseCode = "0x608060405234801561001057600080fd5b5060c98061001f6000396000f3fe608060405260043610601f5760003560e01c80631b9265b814602a576025565b36602557005b600080fd5b348015603557600080fd5b50603c603e565b005b6000471415604b57600080fd5b4173ffffffffffffffffffffffffffffffffffffffff166108fc479081150290604051600060405180830381858888f193505050501580156090573d6000803e3d6000fd5b5056fea26469706673582212201a659d0c476c75fb54d198d4391208ef91f976bb9067f20795c85bd0cbcc449364736f6c63430008010033";

            public static string CoinbaseInvokePay = "0x1b9265b8";
            public static long CoinbaseLargeGasLimit = 9000000;
            public static int CoinbaseStartingBalanceInWei = 10000000;

            public static async Task<Address> DeployAndSeedCoinbase(TestMevRpcBlockchain chain)
            {
                Transaction createContractTx = Build.A.Transaction.WithCode(Bytes.FromHexString(Contracts.CoinbaseCode)).WithGasLimit(Contracts.CoinbaseLargeGasLimit).SignedAndResolved(TestItem.ContractCreatorPrivateKey).TestObject;
                ResultWrapper<Keccak> resultOfCreate = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(createContractTx));
                Assert.AreNotEqual(resultOfCreate.GetResult().ResultType, ResultType.Failure);
                // guarantee state change 
                await chain.AddBlock();

                Keccak createContractTxHash = (Keccak) resultOfCreate.GetData();
                TxReceipt createContractTxReceipt = chain.BlockchainBridge.GetReceipt(createContractTxHash);
                Assert.NotNull(createContractTxReceipt.ContractAddress);

                Transaction seedContractTx = Build.A.Transaction.WithTo(createContractTxReceipt.ContractAddress).WithValue(Contracts.CoinbaseStartingBalanceInWei).WithNonce(1).SignedAndResolved(TestItem.ContractCreatorPrivateKey).TestObject;
                ResultWrapper<Keccak> resultOfSeed = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(seedContractTx));
                Assert.AreNotEqual(resultOfSeed.GetResult().ResultType, ResultType.Failure);
                await chain.AddBlock();

                return createContractTxReceipt.ContractAddress!;
            }
        }

        [Test]
        public void Can_create()
        {
            MevConfig mevConfig = new();
        }
        
        [Test]
        public void Disabled_by_default()
        {
            MevConfig mevConfig = new();
            mevConfig.Enabled.Should().BeFalse();
        }
        
        [Test]
        public void Can_enabled_and_disable()
        {
            MevConfig mevConfig = new();
            mevConfig.Enabled = true;
            mevConfig.Enabled.Should().BeTrue();
            mevConfig.Enabled = false;
            mevConfig.Enabled.Should().BeFalse();
        }

        [Test]
        public async Task Should_serialize_response_properly() 
        {
            var chain = CreateChain();

            Transaction tx = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            string withBrackets = "[" + Rlp.Encode(tx, RlpBehaviors.None).Bytes.ToHexString() + "]";

            string result = chain.TestMevRpcWithSerialization("eth_CallBundle", withBrackets, Rlp.Encode(3).Bytes.ToHexString(true), Rlp.Encode(4).Bytes.ToHexString(true), Rlp.Encode(5).Bytes.ToHexString(true));

            result.Should().Be($"{{\"jsonrpc\":\"2.0\", \"result\": {{ \"{tx.Hash!}\": {{\"value\" : \"0x0\" }} }}, \"id\":1337}}");
        }

        [Test]
        public async Task Should_pick_one_and_only_one_highest_score_bundle_of_several_using_v1_score_with_no_vanilla_tx_to_include_in_block()
        {
            var chain = CreateChain();

            Address contractAddress = await Contracts.DeployAndSeedCoinbase(chain);

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(120)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.CoinbaseLargeGasLimit).WithData(Bytes.FromHexString(Contracts.CoinbaseInvokePay)).WithTo(contractAddress).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            // Transaction tx4 = TODO looper

            Transaction[] bundle1 = new Transaction[] { tx1, tx3 };
            byte[][] bundle1_bytes = new byte[][] { Rlp.Encode(tx1, RlpBehaviors.None).Bytes, Rlp.Encode(tx3, RlpBehaviors.None).Bytes };
            byte[][] bundle2_bytes = new byte[][] { Rlp.Encode(tx2, RlpBehaviors.None).Bytes, Rlp.Encode(tx3, RlpBehaviors.None).Bytes };
            ResultWrapper<bool> resultOfBundle1 = chain.MevRpcModule.eth_sendBundle(bundle1_bytes, 3);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle1.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle1.GetData());
            ResultWrapper<bool> resultOfBundle2 = chain.MevRpcModule.eth_sendBundle(bundle2_bytes, 3);
            Assert.AreNotEqual(resultOfBundle2.GetResult().ResultType, ResultType.Failure);
            Assert.IsTrue((bool) resultOfBundle2.GetData());

            await chain.AddBlock();

            GetHashes(chain.BlockTree.Head.Transactions.Take(bundle1_bytes.Length)).Should().Equal(GetHashes(bundle1));
        }

        [Test]
        public async Task Should_choose_between_higher_coinbase_reward_of_vanilla_and_bundle_block()
        {
            var chain = CreateChain();
            chain.TxPool.BlockGasLimit = 63000;
            Address contractAddress = await Contracts.DeployAndSeedCoinbase(chain);

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(150)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(200)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction tx4 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(50)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            byte[][] bundle_bytes = new byte[][] { Rlp.Encode(tx4, RlpBehaviors.None).Bytes };
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundle_bytes, 1);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle.GetData());

            ResultWrapper<Keccak> result1 = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(tx1));
            Assert.AreNotEqual(result1.GetResult().ResultType, ResultType.Failure);
            ResultWrapper<Keccak> result2 = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(tx2));
            Assert.AreNotEqual(result2.GetResult().ResultType, ResultType.Failure);
            ResultWrapper<Keccak> result3 = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(tx3));
            Assert.AreNotEqual(result3.GetResult().ResultType, ResultType.Failure);

            await chain.AddBlock();

            GetHashes(chain.BlockTree.Head.Transactions).Should().Equal(GetHashes(new Transaction[] { tx3, tx2, tx1 }));
        }

        [Test]
        public async Task Includes_0_transactions_from_bundle_with_1_or_more_transaction_failures()
        {
            // ignoring bundles with failed tx takes care of intersecting bundles
        }

        [Test]
        public async Task Call_bundle_sample()
        {

        }

        [Test]
        public async Task Should_include_bundle_transactions_uninterrupted_in_order_from_least_index_at_beginning_of_block()
        {

        }

        [Test]
        [Ignore("v0.2")]
        public async Task Should_merge_disjoint_bundles_with_v2_score()
        {
            
        }
    }
}