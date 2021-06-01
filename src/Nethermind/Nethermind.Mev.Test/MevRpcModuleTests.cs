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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using Nethermind.JsonRpc.Test;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Int256;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc;
using Nethermind.Facade;
using NSubstitute;
using NUnit.Framework;
using Newtonsoft.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Evm;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Test

{
    [TestFixture]
    public partial class MevRpcModuleTests
    {
        private static IEnumerable<Keccak?> GetHashes(IEnumerable<Transaction> bundle2Txs) => bundle2Txs.Select(t => t.Hash);

        private static class Generators
        {
            public static IEnumerable<Transaction> GenerateRandomTransactions(uint n)
            {  
                var rand = new Random();
                UInt256 baseGasPrice = 50;
                List<Transaction> transactions = new();

                for(var i = 0; i < n; i++)
                {
                    int txType = rand.Next(15);
                    // if (txType <= 0) 
                    {
                        Transaction plain = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(baseGasPrice).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                        transactions.Add(plain);
                    }
                    // else 
                    // {
                    //     // TODO use abi encoder and looper 
                    // }
                }

                return transactions;
            }
        }

        private static class Contracts
        {
            public static string CoinbaseCode = "0x608060405234801561001057600080fd5b5060c88061001f6000396000f3fe608060405260043610601f5760003560e01c80631b9265b814602a576025565b36602557005b600080fd5b348015603557600080fd5b50603c603e565b005b60004711604a57600080fd5b4173ffffffffffffffffffffffffffffffffffffffff166108fc479081150290604051600060405180830381858888f19350505050158015608f573d6000803e3d6000fd5b5056fea264697066735822122097b59c58130e1eb15189fe1fcae5cc34202ea1866db4ff57fe4871083b41751864736f6c634300060c0033";
            public static string LooperCode = "0x608060405234801561001057600080fd5b50610247806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c80630b7d796e14610030575b600080fd5b61004a6004803603810190610045919061009f565b61004c565b005b6000805b8281101561008557600281610065919061011e565b8261007091906100c8565b9150808061007d90610182565b915050610050565b505050565b600081359050610099816101fa565b92915050565b6000602082840312156100b157600080fd5b60006100bf8482850161008a565b91505092915050565b60006100d382610178565b91506100de83610178565b9250827fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff03821115610113576101126101cb565b5b828201905092915050565b600061012982610178565b915061013483610178565b9250817fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff048311821515161561016d5761016c6101cb565b5b828202905092915050565b6000819050919050565b600061018d82610178565b91507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8214156101c0576101bf6101cb565b5b600182019050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b61020381610178565b811461020e57600080fd5b5056fea26469706673582212204ac106f8c2714cc8c27caef3a9d5261f903748eef1189bb41c7ebdc8e749796864736f6c63430008010033";
            public static string ReverterCode = "0x6080604052348015600f57600080fd5b50607080601d6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063a9cc471814602d575b600080fd5b60336035565b005b600080fdfea2646970667358221220ac9d93061661e50d3b0b8a1c9f153485bf00459e1ef145ec811bf3ea0ccf134564736f6c63430008010033";
            public static string CallableCode = "0x608060405234801561001057600080fd5b50600a60008190555060d2806100276000396000f3fe6080604052348015600f57600080fd5b506004361060325760003560e01c80636d4ce63c146037578063b8e010de146051575b600080fd5b603d6059565b604051604891906079565b60405180910390f35b60576062565b005b60008054905090565b600f600081905550565b6073816092565b82525050565b6000602082019050608c6000830184606c565b92915050565b600081905091905056fea26469706673582212209613531dae74fcbd2a6751a86f2f3206d1c690011593ae904e06996b9b48741664736f6c63430008010033";
            public static string SetableCode = "0x608060405234801561001057600080fd5b5060006040516020016100239190610053565b6040516020818303038152906040528051906020012060008190555061008d565b61004d8161007b565b82525050565b60006020820190506100686000830184610044565b92915050565b600060ff82169050919050565b60006100868261006e565b9050919050565b6101b38061009c6000396000f3fe608060405234801561001057600080fd5b50600436106100365760003560e01c806360fe47b11461003b5780636d4ce63c14610057575b600080fd5b610055600480360381019061005091906100c7565b610075565b005b61005f6100a9565b60405161006c919061010e565b60405180910390f35b8060005460405160200161008a929190610129565b6040516020818303038152906040528051906020012060008190555050565b60008054905090565b6000813590506100c181610166565b92915050565b6000602082840312156100d957600080fd5b60006100e7848285016100b2565b91505092915050565b6100f981610152565b82525050565b6101088161015c565b82525050565b600060208201905061012360008301846100f0565b92915050565b600060408201905061013e60008301856100ff565b61014b60208301846100f0565b9392505050565b6000819050919050565b6000819050919050565b61016f8161015c565b811461017a57600080fd5b5056fea264697066735822122021b03dd7e3fc95090ba786ef57ed585f5dedf3ed6cc518e8a0e276636696330864736f6c63430008010033";
            public static string SecondCallReverter = "0x608060405234801561001057600080fd5b5060008060006101000a81548160ff02191690831515021790555060c3806100396000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c806374b09d0a14602d575b600080fd5b60336035565b005b6000151560008054906101000a900460ff1615151415606c5760016000806101000a81548160ff021916908315150217905550608b565b6001151560008054906101000a900460ff1615151415608a57600080fd5b5b56fea2646970667358221220ef2ca85a4a8ff8b753ca79a5bc3140e22a84f03ea8a3b08eb67adab4405ba00264736f6c63430008010033";

            // about 25000 gas?
            public static string CoinbaseInvokePay = "0x1b9265b8";
            public static int CoinbaseStartingBalanceInWei = 10000000;
            public static long LargeGasLimit = 9_000_000;
            // 1203367 gas 
            public static string LooperInvokeLoop2000 = "0x0b7d796e00000000000000000000000000000000000000000000000000000000000007d0";
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
            public static string SecondCallReverterInvokeFail = "0x358AA13c52544ECCEF6B0ADD0f801012ADAD5eE3";
            
            // WARNING be careful when using PrivateKeyC
            // make sure keys from A to D are funded with test ether
            public static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;

            public static async Task<Address> Deploy(TestMevRpcBlockchain chain, string code, ulong nonce = 0)
            {
                Transaction createContractTx = Build.A.Transaction.WithCode(Bytes.FromHexString(code)).WithGasLimit(LargeGasLimit).WithNonce(nonce).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                // guarantee state change 
                await chain.AddBlock(true, createContractTx);

                TxReceipt? createContractTxReceipt = chain.Bridge.GetReceipt(createContractTx.Hash!);
                createContractTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {createContractTx.Hash!} was not deployed.");
                
                return createContractTxReceipt!.ContractAddress!;
            }
            
            public static async Task SeedCoinbase(TestMevRpcBlockchain chain, Address coinbaseAddress)
            {
                Transaction seedContractTx = Build.A.Transaction.WithTo(coinbaseAddress).WithValue(CoinbaseStartingBalanceInWei).WithNonce(1).WithGasLimit(GasCostOf.Transaction).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                await chain.AddBlock(true, seedContractTx);
            }
        }
        
        private static async Task<Keccak> SendSignedTransaction(TestMevRpcBlockchain chain, Transaction tx)
        {
            ResultWrapper<Keccak>? result = await chain.EthRpcModule.eth_sendRawTransaction(Rlp.Encode(tx).Bytes);
            Assert.AreNotEqual(result.GetResult().ResultType, ResultType.Failure);
            return result.Data;
        }
        
        private void SuccessfullySendBundle(TestMevRpcBlockchain chain, int blockNumber, params Transaction[] txs)
        {
            byte[][] bundleBytes = txs.Select(t => Rlp.Encode(t).Bytes).ToArray();
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, blockNumber);
            resultOfBundle.GetResult().ResultType.Should().NotBe(ResultType.Failure);
            resultOfBundle.GetData().Should().Be(true);
        }
        
        private MevBundle SuccessfullySendBundleWithRevertingTxHashes(TestMevRpcBlockchain chain, int blockNumber, Keccak[] revertingTxHashes = null, params Transaction[] txs)
        {
            byte[][] bundleBytes = txs.Select(t => Rlp.Encode(t).Bytes).ToArray();
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, blockNumber, default, default, revertingTxHashes);
            resultOfBundle.GetResult().ResultType.Should().NotBe(ResultType.Failure);
            resultOfBundle.GetData().Should().Be(true);
            return new MevBundle(txs, blockNumber, default, default, revertingTxHashes);
        }

        [Test]
        public void Can_create_config()
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
        public async Task Should_execute_eth_callBundle_and_serialize_successful_response_properly() 
        {
            var chain = await CreateChain(SelectorType.V2, 2);

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CallableCode);
            
            Transaction getTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeGet)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction setTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeSet)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            string transactions = $"[\"{Rlp.Encode(setTx).Bytes.ToHexString()}\",\"{Rlp.Encode(getTx).Bytes.ToHexString()}\"]";

            string result = chain.TestSerializedRequest(chain.MevRpcModule, "eth_callBundle", transactions);

            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{setTx.Hash!}\":{{\"value\":\"0x\"}},\"{getTx.Hash!}\":{{\"value\":\"0x\"}}}},\"id\":67}}");
        }
        
        [Test]
        public async Task Should_execute_eth_callBundle_and_serialize_failed_response_properly() 
        {
            var chain = await CreateChain(SelectorType.V2, 2);
            Address reverterContractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction failedTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(reverterContractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            string transactions = $"[\"{Rlp.Encode(failedTx).Bytes.ToHexString()}\"]";
            string result = chain.TestSerializedRequest(chain.MevRpcModule, "eth_callBundle", transactions);
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{failedTx.Hash!}\":{{\"error\":\"0x\"}}}},\"id\":67}}");
        }

        [Test]
        public async Task Should_pick_one_and_only_one_highest_score_bundle_of_several_with_no_vanilla_tx_to_include_in_block_with_1_maxMergedBundles()
        {
            var chain = await CreateChain(SelectorType.V2, 1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CoinbaseCode);
            await Contracts.SeedCoinbase(chain, contractAddress);
            Console.WriteLine(await chain.EthRpcModule.eth_getBalance(contractAddress));

            Transaction tx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(120ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx1WithLowerGasPrice = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction tx3 = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithData(Bytes.FromHexString(Contracts.CoinbaseInvokePay)).WithTo(contractAddress).WithNonce(1).WithGasPrice(0ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Address looperContractAddress = await Contracts.Deploy(chain, Contracts.LooperCode, 2);
            Transaction looperBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(40ul).WithTo(looperContractAddress).WithData(Bytes.FromHexString(Contracts.LooperInvokeLoop2000)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

            Transaction[] bundle1 = { tx1, tx3 };

            SuccessfullySendBundle(chain, 4, bundle1);
            SuccessfullySendBundle(chain, 4, tx1WithLowerGasPrice, tx3);
            SuccessfullySendBundle(chain, 4, looperBundleTx);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(bundle1));
        }

        [Test]
        public async Task Should_push_out_tail_gas_price_tx()
        {
            var chain = await CreateChain(SelectorType.V2, 1);
            chain.GasLimitCalculator.GasLimit = GasCostOf.Transaction;

            Transaction poolTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction bundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(200ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] {bundleTx}));
        }

        [Test]
        public async Task Should_choose_between_higher_coinbase_reward_of_vanilla_and_bundle_block()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = GasCostOf.Transaction;

            Transaction poolTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction bundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx }));
        }

        [Test]
        public async Task Includes_0_transactions_from_bundle_with_1_or_more_transaction_failures()
        {
            // ignoring bundles with failed tx takes care of intersecting bundles
            var chain = await CreateChain(SelectorType.V2, 2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Transaction poolTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction normalBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 2, normalBundleTx, revertingBundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx }));
        }

        [Test]
        public async Task Should_include_bundle_transactions_uninterrupted_in_order_from_least_index_at_beginning_of_block()
        {
            var chain = await CreateChain(SelectorType.V2, 2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.SetableCode);
            
            TransactionBuilder<Transaction> BuildTx() => Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(0ul).WithTo(contractAddress);

            Transaction set1 = BuildTx().WithData(Bytes.FromHexString(Contracts.SetableInvokeSet1)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction set2 = BuildTx().WithData(Bytes.FromHexString(Contracts.SetableInvokeSet2)).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction set3 = BuildTx().WithData(Bytes.FromHexString(Contracts.SetableInvokeSet3)).WithGasPrice(750ul).WithNonce(1).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            Transaction tx4 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(10ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction tx5 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(5ul).WithNonce(1).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            // send regular tx before bundle
            await SendSignedTransaction(chain, tx4);
            await SendSignedTransaction(chain, tx5);
            
            SuccessfullySendBundle(chain, 2, set1, set2, set3);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new Transaction[] { set1, set2, set3, tx4, tx5 }));
        }

        [Test]
        public async Task Should_merge_disjoint_bundles()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Transaction bundleTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction bundleTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx1);
            SuccessfullySendBundle(chain, 1, bundleTx2);


            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx1, bundleTx2, poolTx }));
        }
        
        [Test]
        public async Task Should_accept_and_simulate_bundle_with_future_blockNumber_given()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Transaction bundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 2, bundleTx);
            await SendSignedTransaction(chain, poolTx1);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx1 }));

            await SendSignedTransaction(chain, poolTx2);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx, poolTx2 }));
        }
        
        [Test]
        public async Task Should_reject_bundle_with_past_blockNumber_given()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Transaction bundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 2, bundleTx);
            await SendSignedTransaction(chain, poolTx1);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx1 }));

            await SendSignedTransaction(chain, poolTx2);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx, poolTx2 }));
        }

        [Test]
        public async Task Should_discard_mempool_tx()
        {
            var chain = await CreateChain(SelectorType.V2, 2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Transaction poolAndBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction expensiveBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(130ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction middleBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(120ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction cheapBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(95ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
  
            await SendSignedTransaction(chain, poolAndBundleTx);

            SuccessfullySendBundle(chain, 1, poolAndBundleTx, cheapBundleTx);
            SuccessfullySendBundle(chain, 1, expensiveBundleTx);
            SuccessfullySendBundle(chain, 1, middleBundleTx);
            
            await chain.AddBlock(true);
            
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { expensiveBundleTx, middleBundleTx, poolAndBundleTx }));
        }

        [Test]
        public async Task Should_discard_mempool_tx_if_bundle_comes_first()
        {
            var chain = await CreateChain(SelectorType.V2, 2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Transaction poolAndBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction expensiveBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(130ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction middleBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(120ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction cheapBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(95ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

            SuccessfullySendBundle(chain, 1, poolAndBundleTx, cheapBundleTx);
            SuccessfullySendBundle(chain, 1, expensiveBundleTx);
            SuccessfullySendBundle(chain, 1, middleBundleTx);
            
            await SendSignedTransaction(chain, poolAndBundleTx);
            
            await chain.AddBlock(true);
            
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { expensiveBundleTx, middleBundleTx, poolAndBundleTx }));
        }
        
        [Test]
        public async Task Should_accept_reverting_bundle_with_RevertingTxHashes()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundleWithRevertingTxHashes(chain, 2, new Keccak[] {revertingBundleTx.Hash!}, revertingBundleTx);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingBundleTx }));
        }
        
        [Test]
        public async Task Should_accept_future_reverting_bundle_with_RevertingTxHashes()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyD).TestObject;
            
            MevBundle bundle = SuccessfullySendBundleWithRevertingTxHashes(chain, 3, new Keccak[] { revertingBundleTx.Hash! }, revertingBundleTx);
            await SendSignedTransaction(chain, poolTx1);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx1 }));

            await chain.BundlePool.WaitForSimulationToStart(bundle, CancellationToken.None);
            await SendSignedTransaction(chain, poolTx2);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingBundleTx, poolTx2 }));
        }
        
        [Test]
        public async Task Should_accept_reverting_larger_bundle_with_one_reverting_tx_in_RevertingTxHashes()
        {
            var chain = await CreateChain(SelectorType.V2, 5);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction normalBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(130ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundleWithRevertingTxHashes(chain, 2, new Keccak[] {revertingBundleTx.Hash!}, revertingBundleTx, normalBundleTx);
            
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingBundleTx, normalBundleTx }));
        }
        
        [Test]
        public async Task Should_not_include_bundle_if_wrong_transaction_is_in_RevertingTxHashes()
        {
            var chain = await CreateChain(SelectorType.V2, 5);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction revertingBundleTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(500).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction normalBundleTx = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(130ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundleWithRevertingTxHashes(chain, 2, new Keccak[] {normalBundleTx.Hash!}, revertingBundleTx, normalBundleTx);
            
            await chain.AddBlock(true);

            // should not include anything in the block
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal();
        }
        
        [Test]
        public async Task Should_choose_positive_bundle_count_less_than_maxMergedBundle_if_it_gives_more_profit()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            // space for 4 simple transactions
            chain.GasLimitCalculator.GasLimit = 84000;
            
            // ordered by gas
            Transaction bundleTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(150ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction bundleTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(130ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(120ul).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(110ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction bundleTx3 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            SuccessfullySendBundle(chain, 1, bundleTx1);
            SuccessfullySendBundle(chain, 1, bundleTx2);
            SuccessfullySendBundle(chain, 1, bundleTx3);
            
            await SendSignedTransaction(chain, poolTx1);
            await SendSignedTransaction(chain, poolTx2);
            
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx1, bundleTx2, poolTx1, poolTx2 }));
        }
        
        [Test]
        public async Task Should_reject_second_bundle_where_they_succeed_individually_but_fail_if_in_the_same_block()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.SecondCallReverter);
            Transaction revertingOnSecondCallTx1 = Build.A.Transaction.WithGasLimit(4_000_000).WithGasPrice(30ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction revertingOnSecondCallTx2 = Build.A.Transaction.WithGasLimit(4_000_000).WithGasPrice(20ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx2);
            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx1);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingOnSecondCallTx1 }));
        }
        
        [Test]
        [Ignore("Whole bundle reverting bundle checking not yet implemented")]
        public async Task Should_accept_bundles_in_RevertingTxHashes_where_they_only_fail_if_included_in_block_together()
        {
            var chain = await CreateChain(SelectorType.V2, 3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.SecondCallReverter);
            Transaction revertingOnSecondCallTx1 = Build.A.Transaction.WithGasLimit(4_000_000).WithGasPrice(30ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction revertingOnSecondCallTx2 = Build.A.Transaction.WithGasLimit(4_000_000).WithGasPrice(20ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundleWithRevertingTxHashes(chain, 2, new Keccak[] {revertingOnSecondCallTx2.Hash!}, revertingOnSecondCallTx2);
            SuccessfullySendBundleWithRevertingTxHashes(chain, 2, new Keccak[] {revertingOnSecondCallTx1.Hash!}, revertingOnSecondCallTx1);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingOnSecondCallTx1, revertingOnSecondCallTx2 }));
        }
    }
}
