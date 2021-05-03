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
    public partial class MevRpcModuleTests
    {
        private static IEnumerable<Keccak?> GetHashes(IEnumerable<Transaction> bundle2Txs) => bundle2Txs.Select(t => t.Hash);

        private static class Contracts
        {
            public static string CoinbaseCode = "608060405234801561001057600080fd5b5060c88061001f6000396000f3fe608060405260043610601f5760003560e01c80631b9265b814602a576025565b36602557005b600080fd5b348015603557600080fd5b50603c603e565b005b60004711604a57600080fd5b4173ffffffffffffffffffffffffffffffffffffffff166108fc479081150290604051600060405180830381858888f19350505050158015608f573d6000803e3d6000fd5b5056fea264697066735822122048c1d2b093a9310a62785518c7e2ffa69957ab33203d01532cfe333493c4e53f64736f6c63430008010033";
            public static string LooperCode = "608060405234801561001057600080fd5b506101e1806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c8063a92100cb14610030575b600080fd5b61003861003a565b005b6000805b6107d08110156100755760028161005591906100cf565b826100609190610079565b9150808061006d90610133565b91505061003e565b5050565b600061008482610129565b915061008f83610129565b9250827fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff038211156100c4576100c361017c565b5b828201905092915050565b60006100da82610129565b91506100e583610129565b9250817fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff048311821515161561011e5761011d61017c565b5b828202905092915050565b6000819050919050565b600061013e82610129565b91507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8214156101715761017061017c565b5b600182019050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fdfea2646970667358221220409141cb4095231dcf659c5fd79ec07ef76c17cbc4a7d99cf35360fade1113a264736f6c63430008010033";
            public static string ReverterCode = "6080604052348015600f57600080fd5b50607080601d6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063a9cc471814602d575b600080fd5b60336035565b005b600080fdfea2646970667358221220ac9d93061661e50d3b0b8a1c9f153485bf00459e1ef145ec811bf3ea0ccf134564736f6c63430008010033";
            public static string CallableCode = "608060405234801561001057600080fd5b50600a60008190555060d2806100276000396000f3fe6080604052348015600f57600080fd5b506004361060325760003560e01c80636d4ce63c146037578063b8e010de146051575b600080fd5b603d6059565b604051604891906079565b60405180910390f35b60576062565b005b60008054905090565b600f600081905550565b6073816092565b82525050565b6000602082019050608c6000830184606c565b92915050565b600081905091905056fea26469706673582212209613531dae74fcbd2a6751a86f2f3206d1c690011593ae904e06996b9b48741664736f6c63430008010033";

            public static string SetableCode = "608060405234801561001057600080fd5b5060006040516020016100239190610053565b6040516020818303038152906040528051906020012060008190555061008d565b61004d8161007b565b82525050565b60006020820190506100686000830184610044565b92915050565b600060ff82169050919050565b60006100868261006e565b9050919050565b6101b38061009c6000396000f3fe608060405234801561001057600080fd5b50600436106100365760003560e01c806360fe47b11461003b5780636d4ce63c14610057575b600080fd5b610055600480360381019061005091906100c7565b610075565b005b61005f6100a9565b60405161006c919061010e565b60405180910390f35b8060005460405160200161008a929190610129565b6040516020818303038152906040528051906020012060008190555050565b60008054905090565b6000813590506100c181610166565b92915050565b6000602082840312156100d957600080fd5b60006100e7848285016100b2565b91505092915050565b6100f981610152565b82525050565b6101088161015c565b82525050565b600060208201905061012360008301846100f0565b92915050565b600060408201905061013e60008301856100ff565b61014b60208301846100f0565b9392505050565b6000819050919050565b6000819050919050565b61016f8161015c565b811461017a57600080fd5b5056fea264697066735822122021b03dd7e3fc95090ba786ef57ed585f5dedf3ed6cc518e8a0e276636696330864736f6c63430008010033";

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
            public static string SetableInvokeGet = "0x6d4ce63c";
            public static string CallableInvokeSet = "0xb8e010de";
            public static string SetableInvokeSet1 = "0x60fe47b10000000000000000000000000000000000000000000000000000000000000001";
            public static string SetableInvokeSet2 = "0x60fe47b10000000000000000000000000000000000000000000000000000000000000002";
            public static string SetableInvokeSet3 = "0x60fe47b10000000000000000000000000000000000000000000000000000000000000003";
            public static uint CallableGetValueAfterSet = 15;
            public static string SetableGetValueAfterSets = "0x5cee8536622f876cddaed0988719d7302da5179cca15d43a35ba328cf0d69380";
            // WARNING be careful when using PrivateKeyC
            // make sure keys from A to D are funded with test ether
            public static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;

            public static async Task<Address> Deploy(TestMevRpcBlockchain chain, string code)
            {
                Transaction createContractTx = Build.A.Transaction.WithCode(Bytes.FromHexString(code)).WithGasLimit(Contracts.LargeGasLimit).SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
                // guarantee state change 
                await chain.AddBlock(true, createContractTx);

                TxReceipt createContractTxReceipt = chain.Bridge.GetReceipt(createContractTx.Hash!);
                Assert.NotNull(createContractTxReceipt.ContractAddress);

                return createContractTxReceipt.ContractAddress!;
            }
            public static async Task SeedCoinbase(TestMevRpcBlockchain chain, Address coinbaseAddress)
            {
                Transaction seedContractTx = Build.A.Transaction.WithTo(coinbaseAddress).WithValue(Contracts.CoinbaseStartingBalanceInWei).WithNonce(1).WithGasLimit(21000).SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
                await chain.AddBlock(true, seedContractTx);
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
            var chain = await CreateChain();

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CallableCode);

            Transaction getTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(1)).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeGet)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction setTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(1)).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeSet)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            string transactions = "[" + Rlp.Encode(setTx).Bytes.ToHexString() + "," + Rlp.Encode(getTx).Bytes.ToHexString() + "]";

            string result = chain.TestSerializedRequest(chain.MevRpcModule, "eth_callBundle", transactions, 
                Rlp.Encode(2).Bytes.ToHexString(true), 
                Rlp.Encode(0).Bytes.ToHexString(true), 
                Rlp.Encode(long.MaxValue).Bytes.ToHexString(true));

            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{setTx.Hash!}\":{{\"value\":\"0x0\"}},\"{getTx.Hash!}\":{{\"value\":\"{Rlp.Encode(Contracts.CallableGetValueAfterSet)}\"}}}},\"id\":1337}}");
        }

        [Test]
        public async Task Should_pick_one_and_only_one_highest_score_bundle_of_several_using_v1_score_with_no_vanilla_tx_to_include_in_block()
        {
            var chain = await CreateChain();
            chain.TxPool.BlockGasLimit = 10000000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CoinbaseCode);
            await Contracts.SeedCoinbase(chain, contractAddress);

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(120)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx1WithlowerGasPrice = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithData(Bytes.FromHexString(Contracts.CoinbaseInvokePay)).WithTo(contractAddress).WithNonce(1).WithGasPrice(new UInt256(0)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Address looperContractAddress = await Contracts.Deploy(chain, Contracts.LooperCode);
            Transaction tx4 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(110)).WithTo(looperContractAddress).WithData(Bytes.FromHexString(Contracts.LooperInvokeLoop)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            Transaction[] bundle1 = new Transaction[] { tx1, tx3 };
            byte[][] bundle1_bytes = new byte[][] { 
                Rlp.Encode(tx1).Bytes, 
                Rlp.Encode(tx3).Bytes 
            };
            byte[][] bundle2_bytes = new byte[][] { 
                Rlp.Encode(tx1WithlowerGasPrice).Bytes, 
                Rlp.Encode(tx3).Bytes 
            };
            byte[][] bundle3_bytes = new byte[][] { 
                Rlp.Encode(tx4).Bytes 
            };
            ResultWrapper<bool> resultOfBundle1 = chain.MevRpcModule.eth_sendBundle(bundle1_bytes, 3);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle1.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle1.GetData());
            ResultWrapper<bool> resultOfBundle2 = chain.MevRpcModule.eth_sendBundle(bundle2_bytes, 3);
            Assert.AreNotEqual(resultOfBundle2.GetResult().ResultType, ResultType.Failure);
            Assert.IsTrue((bool) resultOfBundle2.GetData());
            ResultWrapper<bool> resultOfBundle3 = chain.MevRpcModule.eth_sendBundle(bundle3_bytes, 3);
            Assert.AreNotEqual(resultOfBundle3.GetResult().ResultType, ResultType.Failure);
            Assert.IsTrue((bool) resultOfBundle3.GetData());

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(bundle1));
        }

        [Test]
        public async Task Should_push_out_tail_gas_price_tx()
        {
            var chain = await CreateChain();
            chain.TxPool.BlockGasLimit = 21000;

            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(150)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(200)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            byte[][] bundleBytes = new byte[][] { 
                Rlp.Encode(tx3).Bytes 
            };
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, 1);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle.GetData());

            await SendSignedTransaction(chain, tx2);
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new Transaction[] { tx3 }));
        }

        [Test]
        public async Task Should_choose_between_higher_coinbase_reward_of_vanilla_and_bundle_block()
        {
            var chain = await CreateChain();
            chain.TxPool.BlockGasLimit = 21000;

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx4 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(50)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            byte[][] bundleBytes = new byte[][] { 
                Rlp.Encode(tx4).Bytes 
            };
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, 1);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle.GetData());

            await SendSignedTransaction(chain, tx1);
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new Transaction[] { tx1 }));
        }

        [Test]
        public async Task Includes_0_transactions_from_bundle_with_1_or_more_transaction_failures()
        {
            // ignoring bundles with failed tx takes care of intersecting bundles
            var chain = await CreateChain();
            chain.TxPool.BlockGasLimit = 10000000;

            Transaction tx1 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(150)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;

            byte[][] bundleBytes = new byte[][] { 
                Rlp.Encode(tx2).Bytes, 
                Rlp.Encode(tx3).Bytes 
            };
            ResultWrapper<bool> resultOfBundle1 = chain.MevRpcModule.eth_sendBundle(bundleBytes, 2);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle1.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle1.GetData());

            await SendSignedTransaction(chain, tx1);
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new Transaction[] { tx1 }));
        }

        [Test]
        public async Task Should_include_bundle_transactions_uninterrupted_in_order_from_least_index_at_beginning_of_block()
        {
            var chain = await CreateChain();
            chain.TxPool.BlockGasLimit = 10000000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.SetableCode);

            var builder = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(new UInt256(0)).WithTo(contractAddress);
            Transaction set1 = builder.WithData(Bytes.FromHexString(Contracts.SetableInvokeSet1)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction set2 = builder.WithData(Bytes.FromHexString(Contracts.SetableInvokeSet2)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction set3 = builder.WithData(Bytes.FromHexString(Contracts.SetableInvokeSet3)).WithGasPrice(new UInt256(75)).SignedAndResolved(TestItem.PrivateKeyD).TestObject;
            Transaction get = Build.A.Transaction.WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.SetableInvokeGet)).TestObject;

            Transaction tx4 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(100)).SignedAndResolved(TestItem.PrivateKeyE).TestObject;
            Transaction tx5 = Build.A.Transaction.WithGasLimit(21000).WithGasPrice(new UInt256(50)).SignedAndResolved(TestItem.PrivateKeyF).TestObject;

            // send regular tx before bundle
            await SendSignedTransaction(chain, tx4);
            await SendSignedTransaction(chain, tx5);
            
            byte[][] bundleBytes = new byte[][] { 
                Rlp.Encode(set1).Bytes, 
                Rlp.Encode(set2).Bytes
            };
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, 1);
            Assert.AreNotEqual(ResultType.Failure, resultOfBundle.GetResult().ResultType);
            Assert.IsTrue((bool) resultOfBundle.GetData());

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new Transaction[] { set1, set2, tx4, set3, tx5 }));
            
            ResultWrapper<string> resultGet = chain.EthRpcModule.eth_call(new TransactionForRpc(get));
            Assert.AreNotEqual(resultGet.GetResult().ResultType, ResultType.Failure);
            ((string) resultGet.GetData()).Should().Be(Contracts.SetableGetValueAfterSets);
        }

        [Test]
        [Ignore("v0.2")]
        public async Task Should_merge_disjoint_bundles_with_v2_score() {}

        [Test]
        [Ignore("v0.2")]
        public async Task Should_discard_mempool_tx_in_v2_score() {}
        
        private static async Task<Keccak> SendSignedTransaction(TestMevRpcBlockchain chain, Transaction tx)
        {
            ResultWrapper<Keccak>? result = await chain.EthRpcModule.eth_sendRawTransaction(Rlp.Encode(tx).Bytes);
            Assert.AreNotEqual(result.GetResult().ResultType, ResultType.Failure);
            return result.Data;
        }
    }
}
