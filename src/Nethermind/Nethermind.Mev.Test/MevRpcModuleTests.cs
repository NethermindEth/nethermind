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
        public static IEnumerable<Keccak?> GetHashes(IEnumerable<Transaction> bundle2Txs) => bundle2Txs.Select(t => t.Hash);
        
        public static class Contracts
        {
            public static string CoinbaseCode = "0x608060405234801561001057600080fd5b5060d38061001f6000396000f3fe60806040526004361060265760003560e01c80631b9265b814602b578063d0e30db014603f575b600080fd5b348015603657600080fd5b50603d6047565b005b6045609b565b005b60004711605357600080fd5b4173ffffffffffffffffffffffffffffffffffffffff166108fc479081150290604051600060405180830381858888f193505050501580156098573d6000803e3d6000fd5b50565b56fea26469706673582212205dcd2a43207f6731b7960c534cfbfc9ace510632b212939aecfafedd53c290f564736f6c63430007040033";
            public static string LooperCode = "0x608060405234801561001057600080fd5b50610247806100206000396000f3fe608060405234801561001057600080fd5b506004361061002b5760003560e01c80630b7d796e14610030575b600080fd5b61004a6004803603810190610045919061009f565b61004c565b005b6000805b8281101561008557600281610065919061011e565b8261007091906100c8565b9150808061007d90610182565b915050610050565b505050565b600081359050610099816101fa565b92915050565b6000602082840312156100b157600080fd5b60006100bf8482850161008a565b91505092915050565b60006100d382610178565b91506100de83610178565b9250827fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff03821115610113576101126101cb565b5b828201905092915050565b600061012982610178565b915061013483610178565b9250817fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff048311821515161561016d5761016c6101cb565b5b828202905092915050565b6000819050919050565b600061018d82610178565b91507fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff8214156101c0576101bf6101cb565b5b600182019050919050565b7f4e487b7100000000000000000000000000000000000000000000000000000000600052601160045260246000fd5b61020381610178565b811461020e57600080fd5b5056fea26469706673582212204ac106f8c2714cc8c27caef3a9d5261f903748eef1189bb41c7ebdc8e749796864736f6c63430008010033";
            public static string ReverterCode = "0x6080604052348015600f57600080fd5b50607080601d6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063a9cc471814602d575b600080fd5b60336035565b005b600080fdfea2646970667358221220ac9d93061661e50d3b0b8a1c9f153485bf00459e1ef145ec811bf3ea0ccf134564736f6c63430008010033";
            public static string CallableCode = "0x608060405234801561001057600080fd5b50600a60008190555060d2806100276000396000f3fe6080604052348015600f57600080fd5b506004361060325760003560e01c80636d4ce63c146037578063b8e010de146051575b600080fd5b603d6059565b604051604891906079565b60405180910390f35b60576062565b005b60008054905090565b600f600081905550565b6073816092565b82525050565b6000602082019050608c6000830184606c565b92915050565b600081905091905056fea26469706673582212209613531dae74fcbd2a6751a86f2f3206d1c690011593ae904e06996b9b48741664736f6c63430008010033";
            public static string SetableCode = "0x608060405234801561001057600080fd5b5060006040516020016100239190610053565b6040516020818303038152906040528051906020012060008190555061008d565b61004d8161007b565b82525050565b60006020820190506100686000830184610044565b92915050565b600060ff82169050919050565b60006100868261006e565b9050919050565b6101b38061009c6000396000f3fe608060405234801561001057600080fd5b50600436106100365760003560e01c806360fe47b11461003b5780636d4ce63c14610057575b600080fd5b610055600480360381019061005091906100c7565b610075565b005b61005f6100a9565b60405161006c919061010e565b60405180910390f35b8060005460405160200161008a929190610129565b6040516020818303038152906040528051906020012060008190555050565b60008054905090565b6000813590506100c181610166565b92915050565b6000602082840312156100d957600080fd5b60006100e7848285016100b2565b91505092915050565b6100f981610152565b82525050565b6101088161015c565b82525050565b600060208201905061012360008301846100f0565b92915050565b600060408201905061013e60008301856100ff565b61014b60208301846100f0565b9392505050565b6000819050919050565b6000819050919050565b61016f8161015c565b811461017a57600080fd5b5056fea264697066735822122021b03dd7e3fc95090ba786ef57ed585f5dedf3ed6cc518e8a0e276636696330864736f6c63430008010033";
            public static string SecondCallReverter = "0x608060405234801561001057600080fd5b5060008060006101000a81548160ff02191690831515021790555060c3806100396000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c806374b09d0a14602d575b600080fd5b60336035565b005b6000151560008054906101000a900460ff1615151415606c5760016000806101000a81548160ff021916908315150217905550608b565b6001151560008054906101000a900460ff1615151415608a57600080fd5b5b56fea2646970667358221220d518f3b8de76fcdfc35ffc1279185c7a06d704b72a27a5fe093e12edde0c7ff164736f6c63430007040033";

            // about 25000 gas?
            public static string CoinbaseInvokePay = "0x1b9265b8";
            public static string CoinbaseDeposit = "0xd0e30db0";
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
            public static string SecondCallReverterInvokeFail = "0x74b09d0a";
            
            // WARNING be careful when using PrivateKeyC
            // make sure keys from A to D are funded with test ether
            private static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;

            public static async Task<Address> Deploy(TestMevRpcBlockchain chain, string code, ulong nonce = 0, int value = 1)
            {
                Transaction createContractTx = Build.A.Transaction.WithCode(Bytes.FromHexString(code)).WithGasLimit(LargeGasLimit).WithNonce(nonce).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                // guarantee state change 
                await chain.AddBlock(true, createContractTx);

                TxReceipt? createContractTxReceipt = chain.Bridge.GetReceipt(createContractTx.Hash!);
                createContractTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {createContractTx.Hash!} was not deployed.");
                
                return createContractTxReceipt!.ContractAddress!;
            }
        }
        
        private static async Task<Keccak> SendSignedTransaction(TestMevRpcBlockchain chain, Transaction tx)
        {
            ResultWrapper<Keccak>? result = await chain.EthRpcModule.eth_sendRawTransaction(Rlp.Encode(tx).Bytes);
            Assert.AreNotEqual(result.GetResult().ResultType, ResultType.Failure);
            return result.Data;
        }
        
        public static MevBundle SuccessfullySendBundle(TestMevRpcBlockchain chain, int blockNumber, params BundleTransaction[] txs)
        {
            byte[][] bundleBytes = txs.Select(t => Rlp.Encode(t).Bytes).ToArray();
            List<Keccak> revertingTxHashes = new();
            foreach (var tx in txs)
            {
                if (tx.CanRevert) revertingTxHashes.Add(tx.Hash!);
            }
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, blockNumber, default, default, revertingTxHashes.Count > 0 ? revertingTxHashes.ToArray() : null);
            resultOfBundle.GetResult().ResultType.Should().NotBe(ResultType.Failure);
            resultOfBundle.GetData().Should().Be(true);
            return new MevBundle(blockNumber, txs);
        }
        
        [Test]
        public async Task Should_execute_eth_callBundle_and_serialize_successful_response_properly() 
        {
            var chain = await CreateChain(2);

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CallableCode);
            
            Transaction getTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeGet)).WithValue(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            Transaction setTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CallableInvokeSet)).WithValue(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            string transactions = $"[\"{Rlp.Encode(setTx).Bytes.ToHexString()}\",\"{Rlp.Encode(getTx).Bytes.ToHexString()}\"]";

            string result = chain.TestSerializedRequest(chain.MevRpcModule, "eth_callBundle", transactions);

            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{setTx.Hash!}\":{{\"value\":\"0x\"}},\"{getTx.Hash!}\":{{\"value\":\"0x000000000000000000000000000000000000000000000000000000000000000f\"}}}},\"id\":67}}");
        }
        
        [Test]
        public async Task Should_execute_eth_callBundle_and_serialize_failed_response_properly() 
        {
            var chain = await CreateChain(2);
            Address reverterContractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            Transaction failedTx = Build.A.Transaction.WithGasLimit(Contracts.LargeGasLimit).WithGasPrice(1ul).WithTo(reverterContractAddress).WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail)).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            string transactions = $"[\"{Rlp.Encode(failedTx).Bytes.ToHexString()}\"]";
            string result = chain.TestSerializedRequest(chain.MevRpcModule, "eth_callBundle", transactions);
            result.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{failedTx.Hash!}\":{{\"error\":\"0x\"}}}},\"id\":67}}");
        }

        [Test]
        public async Task Should_pick_one_highest_scoring_bundle_from_several_with_no_pool_txs_with_1_maxMergedBundles()
        {
            var chain = await CreateChain(1);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.CoinbaseCode);
            // put money into contract
            Transaction seedContractTx = Build.A.Transaction.WithTo(contractAddress).WithData(Bytes.FromHexString(Contracts.CoinbaseDeposit)).WithValue(10_000000000000000000).WithNonce(1).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            await chain.AddBlock(true, seedContractTx);
            
            //Console.WriteLine((await chain.EthRpcModule.eth_getBalance(contractAddress)).Data!);

            BundleTransaction tx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(120ul)
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction tx1WithLowerGasPrice = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul).WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction tx3 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit).
                WithData(Bytes.FromHexString(Contracts.CoinbaseInvokePay))
                .WithTo(contractAddress)
                .WithNonce(1)
                .WithGasPrice(0ul)
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            Address looperContractAddress = await Contracts.Deploy(chain, Contracts.LooperCode, 2);
            BundleTransaction looperBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(100ul)
                .WithTo(looperContractAddress)
                .WithData(Bytes.FromHexString(Contracts.LooperInvokeLoop2000))
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

            SuccessfullySendBundle(chain, 4, tx1, tx3);
            SuccessfullySendBundle(chain, 4, tx1WithLowerGasPrice, tx3);
            SuccessfullySendBundle(chain, 4, looperBundleTx);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new []{tx1, tx3}));
        }

        [Test]
        public async Task Should_pick_bundle_if_better_than_pool_tx()
        {
            var chain = await CreateChain(1);
            chain.GasLimitCalculator.GasLimit = GasCostOf.Transaction;

            Transaction poolTx = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            BundleTransaction bundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(200ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] {bundleTx}));
        }

        [Test]
        public async Task Should_pick_pool_tx_if_better_than_bundle()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = GasCostOf.Transaction;

            BundleTransaction poolTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction bundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(50ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx }));
        }

        [Test]
        public async Task Should_reject_bundle_with_failures()
        {
            // ignoring bundles with failed tx takes care of intersecting bundles
            var chain = await CreateChain(2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Transaction poolTx = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction normalBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(500).WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 2, normalBundleTx, revertingBundleTx);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx }));
        }

        [Test]
        public async Task Should_include_bundle_transactions_uninterrupted_in_order_at_beginning_of_block()
        {
            var chain = await CreateChain(2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.SetableCode);
            
            TransactionBuilder<BundleTransaction> BuildTx() => Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithValue(0)
                .WithGasPrice(0ul)
                .WithTo(contractAddress);

            BundleTransaction set1 = BuildTx()
                .WithData(Bytes.FromHexString(Contracts.SetableInvokeSet1))
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction set2 = BuildTx()
                .WithData(Bytes.FromHexString(Contracts.SetableInvokeSet2))
                .WithNonce(1)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction set3 = BuildTx()
                .WithData(Bytes.FromHexString(Contracts.SetableInvokeSet3))
                .WithGasPrice(750ul)
                .WithNonce(2)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
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
        public async Task Should_include_multiple_bundles()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            BundleTransaction bundleTx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction bundleTx2 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Transaction poolTx = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(50ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            SuccessfullySendBundle(chain, 1, bundleTx1);
            SuccessfullySendBundle(chain, 1, bundleTx2);

            await chain.AddBlock(true, poolTx);

            GetHashes(chain.BlockTree.Head!.Transactions)
                .Should().Equal(GetHashes(new[] { bundleTx1, bundleTx2, poolTx }));
        }
        
        [Test]
        public async Task Should_accept_and_simulate_bundle_with_future_blockNumber_given()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            BundleTransaction bundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            Transaction poolTx1 = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Transaction poolTx2 = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(50ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            MevBundle bundle = SuccessfullySendBundle(chain, 2, bundleTx);
            await SendSignedTransaction(chain, poolTx1);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx1 }));

            await chain.BundlePool.WaitForSimulationToStart(bundle, CancellationToken.None);
            await SendSignedTransaction(chain, poolTx2);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx, poolTx2 }));
        }
        
        [Test]
        public async Task Should_reject_bundle_with_past_blockNumber_given()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            BundleTransaction bundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            Transaction poolTx1 = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Transaction poolTx2 = Build.A.Transaction
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(50ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

            await SendSignedTransaction(chain, poolTx1);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx1 }));
            
            // sending bundleTx for blockNumber 1 which has already been mined
            byte[][] bundleBytes = new[] {Rlp.Encode(bundleTx).Bytes.ToArray()};
            ResultWrapper<bool> resultOfBundle = chain.MevRpcModule.eth_sendBundle(bundleBytes, 1);
            resultOfBundle.GetResult().ResultType.Should().NotBe(ResultType.Failure);
            resultOfBundle.GetData().Should().Be(false);

            await SendSignedTransaction(chain, poolTx2);
            await chain.AddBlock(true);
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { poolTx2 }));
        }

        [Test]
        public async Task Should_include_bundles_by_eligible_gas_fee()
        {
            var chain = await CreateChain(2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            BundleTransaction poolAndBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            BundleTransaction expensiveBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(130ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction middleBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(120ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            BundleTransaction cheapBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(95ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
  
            await SendSignedTransaction(chain, poolAndBundleTx);

            SuccessfullySendBundle(chain, 1, poolAndBundleTx, cheapBundleTx);
            SuccessfullySendBundle(chain, 1, expensiveBundleTx);
            SuccessfullySendBundle(chain, 1, middleBundleTx);
            
            await chain.AddBlock(true);
            
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { expensiveBundleTx, middleBundleTx, poolAndBundleTx }));
        }

        [Test]
        public async Task Should_accept_reverting_bundle_with_RevertingTxHashes()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(500).WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            revertingBundleTx.CanRevert = true;

            SuccessfullySendBundle(chain, 2, revertingBundleTx);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingBundleTx }));
        }
        
        [Test]
        public async Task Should_accept_future_reverting_bundle_with_RevertingTxHashes()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;

            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(500ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            revertingBundleTx.CanRevert = true;

            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(100ul).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction).WithGasPrice(50ul).SignedAndResolved(TestItem.PrivateKeyD).TestObject;
            
            MevBundle bundle = SuccessfullySendBundle(chain, 3, revertingBundleTx);
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
            var chain = await CreateChain(5);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(500)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            revertingBundleTx.CanRevert = true;
            
            BundleTransaction normalBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(130ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundle(chain, 2, revertingBundleTx, normalBundleTx);
            
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingBundleTx, normalBundleTx }));
        }
        
        [Test]
        public async Task Should_not_include_bundle_if_wrong_transaction_is_in_RevertingTxHashes()
        {
            var chain = await CreateChain(5);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.ReverterCode);
            BundleTransaction revertingBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(Contracts.LargeGasLimit)
                .WithGasPrice(500).WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.ReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            BundleTransaction normalBundleTx = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(130ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            normalBundleTx.CanRevert = true;

            SuccessfullySendBundle(chain, 2, revertingBundleTx, normalBundleTx);
            
            await chain.AddBlock(true);

            // should not include anything in the block
            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal();
        }
        
        [Test]
        public async Task Should_choose_only_some_bundles_maximizing_profit_between_1_and_maxMergedBundle()
        {
            var chain = await CreateChain(3);
            // space for 4 simple transactions
            chain.GasLimitCalculator.GasLimit = 84000;
            
            // ordered by gas
            BundleTransaction bundleTx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(150ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction bundleTx2 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(130ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            Transaction poolTx1 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(120ul)
                .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
            
            Transaction poolTx2 = Build.A.Transaction.WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(110ul)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            
            BundleTransaction bundleTx3 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(100ul)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            SuccessfullySendBundle(chain, 1, bundleTx1);
            SuccessfullySendBundle(chain, 1, bundleTx2);
            SuccessfullySendBundle(chain, 1, bundleTx3);
            
            await SendSignedTransaction(chain, poolTx1);
            await SendSignedTransaction(chain, poolTx2);
            
            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { bundleTx1, bundleTx2, poolTx1, poolTx2 }));
        }
        
        [Test]
        //[Ignore("Whole bundle reverting bundle checking not yet implemented")]
        public async Task Should_reject_second_bundle_where_they_succeed_individually_but_fail_if_in_the_same_block()
        {
            var chain = await CreateChain(2);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.SecondCallReverter);
            BundleTransaction revertingOnSecondCallTx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(30ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction revertingOnSecondCallTx2 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(20ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx1);
            Console.WriteLine(chain.BundlePool.GetBundles(2, UInt256.Zero).Count());
            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx2);
            Console.WriteLine(chain.BundlePool.GetBundles(2, UInt256.Zero).Count());


            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingOnSecondCallTx1 }));
        }
        
        [Test]
        public async Task Should_reject_bundle_where_transactions_succeed_individually_but_fail_if_in_the_same_bundle()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.SecondCallReverter);
            BundleTransaction revertingOnSecondCallTx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(30ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .WithValue(0)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            
            BundleTransaction revertingOnSecondCallTx2 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(20ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .WithNonce(1)
                .WithValue(0).
                SignedAndResolved(TestItem.PrivateKeyA).TestObject;

            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx1, revertingOnSecondCallTx2);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal();
        }
        
        [Test]
        public async Task Should_accept_bundles_in_RevertingTxHashes_where_they_only_fail_if_included_in_block_together()
        {
            var chain = await CreateChain(3);
            chain.GasLimitCalculator.GasLimit = 10_000_000;
            
            Address contractAddress = await Contracts.Deploy(chain, Contracts.SecondCallReverter);
            BundleTransaction revertingOnSecondCallTx1 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(30ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            revertingOnSecondCallTx1.CanRevert = true;
            
            BundleTransaction revertingOnSecondCallTx2 = Build.A.TypedTransaction<BundleTransaction>()
                .WithGasLimit(4_000_000)
                .WithGasPrice(20ul)
                .WithTo(contractAddress)
                .WithData(Bytes.FromHexString(Contracts.SecondCallReverterInvokeFail))
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            revertingOnSecondCallTx2.CanRevert = true;

            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx2);
            SuccessfullySendBundle(chain, 2, revertingOnSecondCallTx1);

            await chain.AddBlock(true);

            GetHashes(chain.BlockTree.Head!.Transactions).Should().Equal(GetHashes(new[] { revertingOnSecondCallTx1, revertingOnSecondCallTx2 }));
        }
    }
}
