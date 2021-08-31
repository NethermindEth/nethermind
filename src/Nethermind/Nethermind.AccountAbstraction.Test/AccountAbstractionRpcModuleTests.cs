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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public partial class AccountAbstractionRpcModuleTests
    {
        private Contracts _contracts = new();
        private AbiEncoder _encoder = new();
        
        public class Contracts
        {
            internal AbiDefinition SingletonAbi;
            internal string SingletonCode;
            internal AbiDefinition SimpleWalletAbi;
            internal string SimpleWalletCode;
            internal AbiDefinition TestCounterAbi;
            internal string TestCounterCode;
            internal AbiDefinition TokenPaymasterAbi;
            internal string TokenPaymasterCode;

            private AbiEncoder _encoder = new();
            
            public Contracts()
            {
                using (StreamReader r = new StreamReader("TestContracts/Singleton.json"))
                {
                    string json = r.ReadToEnd();
                    JObject obj = JObject.Parse(json);

                    (SingletonAbi, SingletonCode) = LoadContract(obj);
                }
                
                using (StreamReader r = new StreamReader("TestContracts/SimpleWallet.json"))
                {
                    string json = r.ReadToEnd();
                    JObject obj = JObject.Parse(json);

                    (SimpleWalletAbi, SimpleWalletCode) = LoadContract(obj);
                }
                
                using (StreamReader r = new StreamReader("TestContracts/TestCounter.json"))
                {
                    string json = r.ReadToEnd();
                    JObject obj = JObject.Parse(json);

                    (TestCounterAbi, TestCounterCode) = LoadContract(obj);
                }
                
                using (StreamReader r = new StreamReader("TestContracts/TokenPaymaster.json"))
                {
                    string json = r.ReadToEnd();
                    JObject obj = JObject.Parse(json);

                    (TokenPaymasterAbi, TokenPaymasterCode) = LoadContract(obj);
                }
            }
            
            private (AbiDefinition, string) LoadContract(JObject obj)
            {
                AbiDefinitionParser parser = new();
                parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
                AbiDefinition contract = parser.Parse(obj["abi"]!.ToString());
                return (contract, obj["bytecode"]!.ToString());
            }
            
            public static long LargeGasLimit = 2_000_000;
            
            private static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;
            public static Address WalletOwner = TestItem.AddressA;

            public UInt256 GetCount(TestRpcBlockchain chain, Address counter, Address wallet)
            {
                Transaction getCountTransaction = Core.Test.Builders.Build.A.Transaction
                    .WithTo(counter)
                    .WithGasLimit(100_000)
                    .WithValue(0)
                    .WithData(_encoder.Encode(AbiEncodingStyle.IncludeSignature, TestCounterAbi.Functions["counters"].GetCallInfo().Signature, wallet))
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;
            
                UInt256 count = new UInt256(Bytes.FromHexString(chain.EthRpcModule.eth_call(new TransactionForRpc(getCountTransaction)).Data), true);

                return count;
            }

            public async Task<(Address, Address?, Address?)> Deploy(TestAccountAbstractionRpcBlockchain chain, string? miscContractCode = null)
            {
                bool createMiscContract = miscContractCode is not null;
                
                
                IList<Transaction> transactionsToInclude = new List<Transaction>();

                Transaction singletonTx = Core.Test.Builders.Build.A.Transaction.WithCode(Bytes.FromHexString(SingletonCode)).WithGasLimit(6_000_000).WithNonce(0).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                await chain.AddBlock(true, singletonTx);
                TxReceipt createSingletonTxReceipt = chain.Bridge.GetReceipt(singletonTx.Hash!);
                createSingletonTxReceipt.ContractAddress.Should().NotBeNull($"Contract transaction {singletonTx.Hash!} was not deployed.");
                chain.State.GetCode(createSingletonTxReceipt.ContractAddress!).Should().NotBeNullOrEmpty();

                Transaction? walletTx = Core.Test.Builders.Build.A.Transaction.WithCode(Bytes.FromHexString(SimpleWalletCode + $"000000000000000000000000{createSingletonTxReceipt.ContractAddress!.ToString().Substring(2, 40)}000000000000000000000000{WalletOwner.ToString().Substring(2, 40)}")).WithGasLimit(LargeGasLimit).WithNonce(1).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                transactionsToInclude.Add(walletTx!);
                
                Transaction? miscContractTx = createMiscContract ? Core.Test.Builders.Build.A.Transaction.WithCode(Bytes.FromHexString(miscContractCode!)).WithGasLimit(LargeGasLimit).WithNonce(2).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject : null;
                if (createMiscContract) transactionsToInclude.Add(miscContractTx!);
                
                await chain.AddBlock(true, transactionsToInclude.ToArray());

                TxReceipt createWalletTxReceipt = chain.Bridge.GetReceipt(walletTx.Hash!);
                TxReceipt? miscContractTxReceipt = createMiscContract ? chain.Bridge.GetReceipt(miscContractTx.Hash!) : null;
                createWalletTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {walletTx?.Hash!} was not deployed.");
                miscContractTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {miscContractTx?.Hash!} was not deployed.");

                chain.State.GetCode(createWalletTxReceipt?.ContractAddress!).Should().NotBeNullOrEmpty();
                if (createMiscContract) chain.State.GetCode(miscContractTxReceipt?.ContractAddress!).Should().NotBeNullOrEmpty();
                
                return (createSingletonTxReceipt.ContractAddress!, createWalletTxReceipt?.ContractAddress!, miscContractTxReceipt?.ContractAddress!);
            }
        }

        [Test]
        public async Task Should_deploy_contracts_successfully()
        {
            var chain = await CreateChain();
            (Address singletonAddress, Address? walletAddress, _) = await _contracts.Deploy(chain);
        }
        
        [Test]
        public async Task Should_execute_well_formed_op_successfully()
        {
            var chain = await CreateChain();
            (Address singletonAddress, Address? walletAddress, Address? counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterCode);

            byte[] countCalldata = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCount = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["exec"].GetCallInfo().Signature, counterAddress, countCalldata);
            byte[] execCounterCountFromSingleton = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromSingleton"].GetCallInfo().Signature, execCounterCount);
            
            UserOperation op = Build.A.UserOperation
                .WithTarget(walletAddress!)
                .WithCallData(execCounterCountFromSingleton)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(walletAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction);

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress, walletAddress!);
            countBefore.Should().Be(0);

            chain.SendUserOperation(op);
            await chain.AddBlock(true);

            UInt256 countAfter = _contracts.GetCount(chain, counterAddress, walletAddress!);
            countAfter.Should().Be(1);
        }

        public static void SignUserOperation(UserOperation op, PrivateKey privateKey)
        {
            AbiSignature abiSignature = new AbiSignature("userOperation", 
                AbiType.Address, 
                AbiType.UInt256,
                AbiType.Bytes32,
                AbiType.Bytes32,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.UInt256,
                AbiType.Address,
                AbiType.Bytes32);

            byte[] bytes = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, abiSignature,
                op.Target!,
                op.Nonce,
                Keccak.Compute(op.InitCode!),
                Keccak.Compute(op.CallData!),
                op.CallGas,
                op.VerificationGas,
                op.MaxFeePerGas,
                op.MaxPriorityFeePerGas,
                op.Paymaster!,
                Keccak.Compute(op.PaymasterData!));

            Keccak abiMessage = Keccak.Compute(bytes);

            Signer signer = new(1, privateKey, NullLogManager.Instance);
            Signature signature = signer.Sign(Keccak.Compute(
                    Bytes.Concat(
                        Encoding.UTF8.GetBytes("\x19"),
                        Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + abiMessage.Bytes.Length),
                        abiMessage.Bytes)
                    )
                );

            op.Signature = signature;
            op.Hash = UserOperation.CalculateHash(op);
        }
    }
}
