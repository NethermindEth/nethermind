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
            internal AbiDefinition SimpleWalletAbi;
            internal AbiDefinition TestCounterAbi;
            internal AbiDefinition TokenPaymasterAbi;

            private AbiEncoder _encoder = new();
            
            public Contracts()
            {
                SingletonAbi = LoadContract("TestContracts/Singleton.json");
                SimpleWalletAbi = LoadContract("TestContracts/SimpleWallet.json");
                TestCounterAbi = LoadContract("TestContracts/TestCounter.json");
                TokenPaymasterAbi = LoadContract("TestContracts/TokenPaymaster.json");
            }
            
            private AbiDefinition LoadContract(string path)
            {
                using (StreamReader r = new(path))
                {
                    string json = r.ReadToEnd();
                    AbiDefinitionParser parser = new();
                    parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
                    AbiDefinition contract = parser.Parse(json);
                    return contract;
                }
                
            }
            
            public static long LargeGasLimit = 5_000_000;
            
            public static PrivateKey ContractCreatorPrivateKey = TestItem.PrivateKeyC;
            public static PrivateKey WalletOwnerPrivateKey = TestItem.PrivateKeyA;
            public static Address WalletOwner = WalletOwnerPrivateKey.Address;

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
            
            public Address GetAccountAddress(TestRpcBlockchain chain, Address singletonAddress, byte[] bytecode, UInt256 salt)
            {
                Transaction getAccountAddressTransaction = Core.Test.Builders.Build.A.Transaction
                    .WithTo(singletonAddress)
                    .WithGasLimit(1_000_000)
                    .WithValue(0)
                    .WithData(_encoder.Encode(AbiEncodingStyle.IncludeSignature, SingletonAbi.Functions["getAccountAddress"].GetCallInfo().Signature, bytecode, salt))
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;
            
                Address accountAddress = new(Bytes.FromHexString(chain.EthRpcModule.eth_call(new TransactionForRpc(getAccountAddressTransaction)).Data).SliceWithZeroPaddingEmptyOnError(12, 20));

                return accountAddress;
            }

            public byte[] GetWalletConstructor(Address singletonAddress)
            {
                byte[] walletConstructorBytes = _encoder.Encode(AbiEncodingStyle.None, SimpleWalletAbi.Constructors[0].GetCallInfo().Signature, singletonAddress, WalletOwner);
                return Bytes.Concat(SimpleWalletAbi.Bytecode!, walletConstructorBytes);
            }

            public async Task<(Address, Address?, Address?)> Deploy(TestAccountAbstractionRpcBlockchain chain, byte[]? miscContractCode = null)
            {
                bool createMiscContract = miscContractCode is not null;
                IList<Transaction> transactionsToInclude = new List<Transaction>();

                Transaction singletonTx = Core.Test.Builders.Build.A.Transaction.WithCode(SingletonAbi.Bytecode!).WithGasLimit(6_000_000).WithNonce(0).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                await chain.AddBlock(true, singletonTx);
                TxReceipt createSingletonTxReceipt = chain.Bridge.GetReceipt(singletonTx.Hash!);
                createSingletonTxReceipt.ContractAddress.Should().NotBeNull($"Contract transaction {singletonTx.Hash!} was not deployed.");
                chain.State.GetCode(createSingletonTxReceipt.ContractAddress!).Should().NotBeNullOrEmpty();

                Transaction? walletTx = Core.Test.Builders.Build.A.Transaction.WithCode(GetWalletConstructor(createSingletonTxReceipt.ContractAddress!)).WithGasLimit(LargeGasLimit).WithNonce(1).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                transactionsToInclude.Add(walletTx!);
                
                Transaction? miscContractTx = createMiscContract ? Core.Test.Builders.Build.A.Transaction.WithCode(miscContractCode!).WithGasLimit(LargeGasLimit).WithNonce(2).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject : null;
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
            (Address singletonAddress, Address? walletAddress, Address? counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);

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
        
        [Test]
        public async Task Should_succeed_at_creating_account_after_prefund()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 20_000_000;
            (Address singletonAddress, Address? walletAddress, Address? counterAddress) = await _contracts.Deploy(chain);

            byte[] walletConstructor = _contracts.GetWalletConstructor(singletonAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, singletonAddress, walletConstructor, 0);
            
            UserOperation createOp = Build.A.UserOperation
                .WithTarget(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(accountAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction);

            chain.SendUserOperation(createOp);
            await chain.AddBlock(true);

            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);
        }
        
        [Test]
        public async Task Should_batch_multiple_ops()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 30_000_000;
            (Address singletonAddress, Address? walletAddress, Address? counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);
            
            byte[] countCalldata = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCount = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["exec"].GetCallInfo().Signature, counterAddress, countCalldata);
            byte[] execCounterCountFromSingleton = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromSingleton"].GetCallInfo().Signature, execCounterCount);
            
            UserOperation op = Build.A.UserOperation
                .WithTarget(walletAddress!)
                .WithCallData(execCounterCountFromSingleton)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
            
            byte[] walletConstructor = _contracts.GetWalletConstructor(singletonAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, singletonAddress, walletConstructor, 0);
            
            UserOperation createOp = Build.A.UserOperation
                .WithTarget(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallData(execCounterCountFromSingleton)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
            
            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(accountAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction fundTransaction2 = Core.Test.Builders.Build.A.Transaction
                .WithTo(walletAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(1)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction, fundTransaction2);

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress, walletAddress!);
            UInt256 countBefore1 = _contracts.GetCount(chain, counterAddress, accountAddress!);
            countBefore.Should().Be(0);
            countBefore1.Should().Be(0);

            chain.SendUserOperation(op);
            chain.SendUserOperation(createOp);
            await chain.AddBlock(true);
            
            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);

            UInt256 countAfter = _contracts.GetCount(chain, counterAddress, walletAddress!);
            UInt256 countAfter1 = _contracts.GetCount(chain, counterAddress, accountAddress!);
            countAfter.Should().Be(1);
            countAfter1.Should().Be(1);
        }
        
        [Test]
        public async Task Should_create_account_with_tokens()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 20_000_000;

            byte[] paymasterBytecode = Bytes.Concat(
                _contracts.TokenPaymasterAbi.Bytecode,
                _encoder.Encode(
                    AbiEncodingStyle.None,
                    _contracts.TokenPaymasterAbi.Constructors[0].GetCallInfo().Signature, "tst", 
                    new Address("0xd75a3a95360e44a3874e691fb48d77855f127069")));
            
            (Address singletonAddress, Address? walletAddress, Address? paymasterAddress) = await _contracts.Deploy(chain, paymasterBytecode);

            byte[] walletConstructor = _contracts.GetWalletConstructor(singletonAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, singletonAddress, walletConstructor, 0);
            
            UserOperation createOp = Build.A.UserOperation
                .WithPaymaster(paymasterAddress!)
                .WithTarget(accountAddress)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            byte[] addStakeCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TokenPaymasterAbi.Functions["addStake"].GetCallInfo().Signature);
            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(paymasterAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(2.Ether())
                .WithData(addStakeCallData)
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction);

            byte[] mintTokensCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TokenPaymasterAbi.Functions["mintTokens"].GetCallInfo().Signature, accountAddress, 1.Ether());
            Transaction mintTokensTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(paymasterAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithData(mintTokensCallData)
                .WithValue(0)
                .WithNonce(chain.State.GetNonce(Contracts.ContractCreatorPrivateKey.Address))
                .SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
            await chain.AddBlock(true, mintTokensTransaction);
            
            chain.SendUserOperation(createOp);
            await chain.AddBlock(true);

            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);
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
