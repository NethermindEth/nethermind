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
            public static string LooperCode = "608060405234801561001057600080fd5b506101e1806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c8063a92100cb14610030575b600080fd5b61003861003a565b005b6000805b6107d08110156100755760028161005591906100cf565b826100609190610079565b9150808061006d90610133565b91505061003e565b5050565b600061008482610129565b915061008f83610129565b9250827fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff038211156100c4576100c361017c565b5b828201905092915050565b60006100da82610129565b91506100e583610129565b9250817fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff048311821515161561011e5761011d61017c565b5b828202905092915050565b6000819050919050565b600061013e82610129565b91507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8214156101715761017061017c565b5b600182019050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fdfea2646970667358221220409141cb4095231dcf659c5fd79ec07ef76c17cbc4a7d99cf35360fade1113a264736f6c63430008010033";
            public static string ReverterCode = "6080604052348015600f57600080fd5b50607080601d6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063a9cc471814602d575b600080fd5b60336035565b005b600080fdfea2646970667358221220ac9d93061661e50d3b0b8a1c9f153485bf00459e1ef145ec811bf3ea0ccf134564736f6c63430008010033";
            public static string CallableCode = "608060405234801561001057600080fd5b50600a60008190555060d2806100276000396000f3fe6080604052348015600f57600080fd5b506004361060325760003560e01c80636d4ce63c146037578063b8e010de146051575b600080fd5b603d6059565b604051604891906079565b60405180910390f35b60576062565b005b60008054905090565b600f600081905550565b6073816092565b82525050565b6000602082019050608c6000830184606c565b92915050565b600081905091905056fea26469706673582212209613531dae74fcbd2a6751a86f2f3206d1c690011593ae904e06996b9b48741664736f6c63430008010033";

            // about 25000 gas?
            public static string CoinbaseInvokePay = "0x1b9265b8";
            public static int CoinbaseStartingBalanceInWei = 10000000;
            public static long LargeGasLimit = 9000000;
            // 1203367 gas 
            public static string LooperInvokeLoop = "0xa92100cb";
            // 22000 gas about
            public static string ReverterInvokeFail = "0xa9cc4718";
            // 22000
            public static string CallableInvokeGet = "0x6d4ce63c";
            public static string CallableInvokeSet = "0xb8e010de";
            public static uint CallableGetValueAfterSet = 15;

            public static async Task<Address> Deploy(TestMevRpcBlockchain chain, string code)
            {
                Transaction createContractTx = Build.A.Transaction.WithCode(Bytes.FromHexString(code)).WithGasLimit(Contracts.LargeGasLimit).SignedAndResolved(TestItem.ContractCreatorPrivateKey).TestObject;
                ResultWrapper<Keccak> resultOfCreate = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(createContractTx));
                Assert.AreNotEqual(resultOfCreate.GetResult().ResultType, ResultType.Failure);
                // guarantee state change 
                await chain.AddBlock();

                Keccak createContractTxHash = (Keccak) resultOfCreate.GetData();
                TxReceipt createContractTxReceipt = chain.BlockchainBridge.GetReceipt(createContractTxHash);
                Assert.NotNull(createContractTxReceipt.ContractAddress);

                return createContractTxReceipt.ContractAddress!;
            }

            public static async Task<Address> DeployAndSeedCoinbase(TestMevRpcBlockchain chain)
            {
                Address contractAddress = await Contracts.Deploy(chain, Contracts.CoinbaseCode);

                Transaction seedContractTx = Build.A.Transaction.WithTo(contractAddress).WithValue(Contracts.CoinbaseStartingBalanceInWei).WithNonce(1).SignedAndResolved(TestItem.ContractCreatorPrivateKey).TestObject;
                ResultWrapper<Keccak> resultOfSeed = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(seedContractTx));
                Assert.AreNotEqual(resultOfSeed.GetResult().ResultType, ResultType.Failure);
                await chain.AddBlock();

                return contractAddress;
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
        public async Task Should_execute_eth_CallBundle_and_serialize_response_properly() 
        {
            var chain = CreateChain();

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CallableCode);

            Transaction getTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(1)).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeGet)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction setTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(1)).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeSet)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            string withBrackets = "[" + Rlp.Encode(getTx, RlpBehaviors.None).Bytes.ToHexString() + "," + Rlp.Encode(getTx, RlpBehaviors.None).Bytes.ToHexString() + "]";

            string result = chain.TestMevRpcWithSerialization("eth_CallBundle", withBrackets, Rlp.Encode(3).Bytes.ToHexString(true), Rlp.Encode(4).Bytes.ToHexString(true), Rlp.Encode(5).Bytes.ToHexString(true));

            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{setTx.Hash!}\":{{\"value\":\"0x0\"}},\"{getTx.Hash!}\":{{\"value\":\"{Rlp.Encode(Contracts.CallableGetValueAfterSet)}\"}}}},\"id\":1337}}");
        }

        [Test]
        public async Task Should_pick_one_and_only_one_highest_score_bundle_of_several_using_v1_score_with_no_vanilla_tx_to_include_in_block()
        {
            var chain = CreateChain();

            Address contractAddress = await Contracts.DeployAndSeedCoinbase(chain);

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(120)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithData(Bytes.FromHexString(Contracts.CoinbaseInvokePay)).WithTo(contractAddress).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            Address looperContractAddress = await Contracts.Deploy(chain, Contracts.LooperCode);
            Transaction tx4 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(1)).WithTo(looperContractAddress).WithData(Bytes.FromHexString(Contracts.LooperInvokeLoop)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            Transaction[] bundle1 = new Transaction[] { tx1, tx3 };
            byte[][] bundle1_bytes = new byte[][] { Rlp.Encode(tx1, RlpBehaviors.None).Bytes, Rlp.Encode(tx3, RlpBehaviors.None).Bytes };
            byte[][] bundle2_bytes = new byte[][] { Rlp.Encode(tx2, RlpBehaviors.None).Bytes, Rlp.Encode(tx3, RlpBehaviors.None).Bytes };
            byte[][] bundle3_bytes = new byte[][] { Rlp.Encode(tx4, RlpBehaviors.None).Bytes };
            ResultWrapper<bool> resultOfBundle1 = chain.MevRpcModule.eth_sendBundle(bundle1_bytes, 3);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle1.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle1.GetData());
            ResultWrapper<bool> resultOfBundle2 = chain.MevRpcModule.eth_sendBundle(bundle2_bytes, 3);
            Assert.AreNotEqual(resultOfBundle2.GetResult().ResultType, ResultType.Failure);
            Assert.IsTrue((bool) resultOfBundle2.GetData());
            ResultWrapper<bool> resultOfBundle3 = chain.MevRpcModule.eth_sendBundle(bundle3_bytes, 3);
            Assert.AreNotEqual(resultOfBundle3.GetResult().ResultType, ResultType.Failure);
            Assert.IsTrue((bool) resultOfBundle3.GetData());

            await chain.AddBlock();

            GetHashes(chain.BlockTree.Head.Transactions).Should().Equal(GetHashes(bundle1));
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
            var chain = CreateChain();
            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(150)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            byte[][] bundle_bytes = new byte[][] { Rlp.Encode(tx2, RlpBehaviors.None).Bytes, Rlp.Encode(tx3, RlpBehaviors.None).Bytes };
            ResultWrapper<bool> resultOfBundle1 = chain.MevRpcModule.eth_sendBundle(bundle_bytes, 2);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle1.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle1.GetData());

            ResultWrapper<Keccak> result = await chain.EthRpcModule.eth_sendTransaction(new TransactionForRpc(tx1));
            Assert.AreNotEqual(result.GetResult().ResultType, ResultType.Failure);

            await chain.AddBlock();

            GetHashes(chain.BlockTree.Head.Transactions).Should().Equal(GetHashes(new Transaction[] { tx1 }));
        }

        [Test]
        public async Task Should_include_bundle_transactions_uninterrupted_in_order_from_least_index_at_beginning_of_block()
        {
            // TODO more complex logic changes with contracts 
            var chain = CreateChain();
            chain.TxPool.BlockGasLimit = 10000000;

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(150)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(200)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction tx4 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(50)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;
            Transaction tx5 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(75)).WithNonce(1).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            byte[][] bundle_bytes = new byte[][] { Rlp.Encode(tx4, RlpBehaviors.None).Bytes, Rlp.Encode(tx5, RlpBehaviors.None).Bytes };
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

            GetHashes(chain.BlockTree.Head.Transactions).Should().Equal(GetHashes(new Transaction[] { tx4, tx5, tx3, tx2, tx1 }));
        }

        [Test]
        [Ignore("v0.2")]
        public async Task Should_merge_disjoint_bundles_with_v2_score()
        {
            
        }
    }
}