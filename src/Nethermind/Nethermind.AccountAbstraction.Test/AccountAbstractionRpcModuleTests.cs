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
using Nethermind.AccountAbstraction.Contracts;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Test.TestContracts;
using Nethermind.Blockchain.Contracts;
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
        private static int entryPointNum = 2;
        
        public class Contracts
        {
            internal AbiDefinition SingletonFactory;
            internal AbiDefinition[] EntryPointAbi = new AbiDefinition[entryPointNum];
            internal AbiDefinition SimpleWalletAbi;
            internal AbiDefinition TestCounterAbi;
            internal AbiDefinition TokenPaymasterAbi;

            private AbiEncoder _encoder = new();
            
            public Contracts()
            {
                SingletonFactory = LoadContract(typeof(SingletonFactory));
                // TODO: Implement a way to loop over the file names also
                EntryPointAbi[0] = LoadContract(typeof(EntryPoint));
                EntryPointAbi[1] = LoadContract(typeof(EntryPoint_2));
                SimpleWalletAbi = LoadContract(typeof(SimpleWallet));
                TestCounterAbi = LoadContract(typeof(TestCounter));
                TokenPaymasterAbi = LoadContract(typeof(TokenPaymaster));
            }
            
            private AbiDefinition LoadContract(Type contractType)
            {
                var parser = new AbiDefinitionParser();
                parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
                var json = parser.LoadContract(contractType);
                return parser.Parse(json);
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
            
            public Address GetAccountAddress(TestRpcBlockchain chain, Address entryPointAddress, byte[] bytecode, UInt256 salt, int epNum)
            {
                Transaction getAccountAddressTransaction = Core.Test.Builders.Build.A.Transaction
                    .WithTo(entryPointAddress)
                    .WithGasLimit(1_000_000)
                    .WithValue(0)
                    .WithData(_encoder.Encode(AbiEncodingStyle.IncludeSignature, EntryPointAbi[epNum].Functions["getSenderAddress"].GetCallInfo().Signature, bytecode, salt))
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;
            
                Address accountAddress = new(Bytes.FromHexString(chain.EthRpcModule.eth_call(new TransactionForRpc(getAccountAddressTransaction)).Data).SliceWithZeroPaddingEmptyOnError(12, 20));

                return accountAddress;
            }

            public byte[] GetWalletConstructor(Address entryPointAddress)
            {
                byte[] walletConstructorBytes = _encoder.Encode(AbiEncodingStyle.None, SimpleWalletAbi.Constructors[0].GetCallInfo().Signature, entryPointAddress, WalletOwner);
                return Bytes.Concat(SimpleWalletAbi.Bytecode!, walletConstructorBytes);
            }

            public async Task<(Address[], Address?[], Address?[])> Deploy(TestAccountAbstractionRpcBlockchain chain, byte[]? miscContractCode = null)
            {
                Transaction singletonFactoryTx = Core.Test.Builders.Build.A.Transaction.WithCode(SingletonFactory.Bytecode!).WithGasLimit(6_000_000).WithNonce(0).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                await chain.AddBlock(true, singletonFactoryTx);
                Address singletonFactoryAddress = chain.Bridge.GetReceipt(singletonFactoryTx.Hash!).ContractAddress!;

                Address[] computedAddresses = new Address[entryPointNum];
                Address?[] createWalletTxReceiptContractAddresses = new Address[entryPointNum];
                Address?[] miscContractTxReceiptContractAddresses = new Address[entryPointNum];

                for(int i=0; i<entryPointNum; i++)
                {

                    byte[] entryPointConstructorBytes = Bytes.Concat(EntryPointAbi[i].Bytecode!, _encoder.Encode(AbiEncodingStyle.None, EntryPointAbi[i].Constructors[0].GetCallInfo().Signature, singletonFactoryAddress, 0, 2));
                    byte[] createEntryPointBytes = _encoder.Encode(AbiEncodingStyle.IncludeSignature, SingletonFactory.Functions["deploy"].GetCallInfo().Signature, entryPointConstructorBytes, Bytes.Zero32);

                    Transaction entryPointTx = Core.Test.Builders.Build.A.Transaction.WithTo(singletonFactoryAddress).WithData(createEntryPointBytes).WithGasLimit(6_000_000).WithNonce(chain.State.GetNonce(ContractCreatorPrivateKey.Address)).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                    await chain.AddBlock(true, entryPointTx);

                    Address computedAddress = new(Keccak.Compute(Bytes.Concat(Bytes.FromHexString("0xff"), singletonFactoryAddress.Bytes, Bytes.Zero32, Keccak.Compute(entryPointConstructorBytes).Bytes)).Bytes.TakeLast(20).ToArray());

                    TxReceipt createEntryPointTxReceipt = chain.Bridge.GetReceipt(entryPointTx.Hash!);
                    createEntryPointTxReceipt.Error.Should().BeNullOrEmpty($"Contract transaction {computedAddress!} was not deployed.");
                    chain.State.GetCode(computedAddress).Should().NotBeNullOrEmpty();
                    
                    bool createMiscContract = miscContractCode is not null;
                    IList<Transaction> transactionsToInclude = new List<Transaction>();
                    
                    Transaction? walletTx = Core.Test.Builders.Build.A.Transaction.WithCode(GetWalletConstructor(computedAddress)).WithGasLimit(LargeGasLimit).WithNonce(chain.State.GetNonce(ContractCreatorPrivateKey.Address)).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                    transactionsToInclude.Add(walletTx!);
                    
                    Transaction? miscContractTx = createMiscContract ? Core.Test.Builders.Build.A.Transaction.WithCode(miscContractCode!).WithGasLimit(LargeGasLimit).WithNonce(chain.State.GetNonce(ContractCreatorPrivateKey.Address) + 1).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject : null;
                    if (createMiscContract) transactionsToInclude.Add(miscContractTx!);
                    
                    await chain.AddBlock(true, transactionsToInclude.ToArray());

                    TxReceipt createWalletTxReceipt = chain.Bridge.GetReceipt(walletTx.Hash!);
                    TxReceipt? miscContractTxReceipt = createMiscContract ? chain.Bridge.GetReceipt(miscContractTx!.Hash!) : null;
                    createWalletTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {walletTx?.Hash!} was not deployed.");
                    miscContractTxReceipt?.ContractAddress.Should().NotBeNull($"Contract transaction {miscContractTx?.Hash!} was not deployed.");

                    chain.State.GetCode(createWalletTxReceipt?.ContractAddress!).Should().NotBeNullOrEmpty();
                    if (createMiscContract) chain.State.GetCode(miscContractTxReceipt?.ContractAddress!).Should().NotBeNullOrEmpty();

                    computedAddresses[i] = computedAddress;
                    createWalletTxReceiptContractAddresses[i] = createWalletTxReceipt?.ContractAddress!;
                    miscContractTxReceiptContractAddresses[i] = miscContractTxReceipt?.ContractAddress!;
                }
                
                return (computedAddresses, createWalletTxReceiptContractAddresses, miscContractTxReceiptContractAddresses);
            }
        }

        [Test]
        public async Task Should_deploy_contracts_successfully()
        {
            var chain = await CreateChain();
            (Address[] entryPointAddresses, Address?[] walletAddresses, _) = await _contracts.Deploy(chain);
        }
        
        [Test]
        public void Should_sign_correctly()
        {
            UserOperation createOp = Build.A.UserOperation
                .WithSender(new Address("0x65f1326ef62E7b63B2EdF41840E37eB2a0F97515"))
                .WithNonce(7)
                .WithCallData(Bytes.FromHexString("0x80c5c7d000000000000000000000000017e4493e5dc3e0bafdb68147cf15f52f669ef91d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000600000000000000000000000000000000000000000000000000000000000000004278ddd3c00000000000000000000000000000000000000000000000000000000"))
                .WithCallGas(29129)
                .WithVerificationGas(100000)
                .WithPreVerificationGas(21000)
                .WithMaxFeePerGas(1000000007)
                .WithMaxPriorityFeePerGas(1000000000)
                .SignedAndResolved(
                    new PrivateKey("0xa31e1f30394cba49bca6783cf25679abae1e5fd7f70a95ef794b73e041a8c864"),
                    new Address("0x90f3E1105E63C877bF9587DE5388C23Cdb702c6B"), 
                    5
                    )
                .TestObject;
            
            Address entryPointId = new Address("0x90f3e1105e63c877bf9587de5388c23cdb702c6b");
            ulong chainId = 5;
            Keccak idFromTransaction =
                new Keccak("0x87c3605deda77b02b78e62157309985d94531cf7fbb13992c602c8555bece921");
            Keccak idFromUserOperation = createOp.CalculateRequestId(entryPointId, chainId);
            Assert.AreEqual(idFromTransaction, idFromUserOperation,
                "Request IDs do not match.");
            
            Assert.AreEqual(
                Bytes.FromHexString("0xe4ef96c1ebffdae061838b79a0ba2b0289083099dc4d576a7ed0c61c80ed893273ba806a581c72be9e550611defe0bf490f198061b8aa63dd6acfc0b620e0c871c"),
                createOp.Signature,
                "signatures are different"
            );
        }
        
        [Test]
        public async Task Should_execute_well_formed_op_successfully() {
            var chain = await CreateChain();
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);
            
            byte[] countCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature, counterAddress[i]!, 0, countCallData);
            
            UserOperation op = Build.A.UserOperation
                .WithSender(walletAddress[i]!)
                .WithCallData(execCounterCountFromEntryPoint)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;

            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(walletAddress[i]!)
                .WithGasLimit(1_000_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce((UInt256)(i + 1))
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction);

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
            countBefore.Should().Be(0);

            chain.SendUserOperation(entryPointAddress[i], op);
            await chain.AddBlock(true);

            UInt256 countAfter = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
            countAfter.Should().Be(1);

        }
        
        [Test]
        public async Task Should_succeed_at_creating_account_after_prefund()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 20_000_000;
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain);

            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress, walletConstructor, 0);
            
            UserOperation createOp = Build.A.UserOperation
                .WithSender(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, chain.EntryPointAddress, chain.SpecProvider.ChainId)
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
            (Address entryPointAddress, Address? walletAddress, Address? counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);
            
            byte[] countCalldata = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature, counterAddress!, 0, countCalldata);
            
            UserOperation op = Build.A.UserOperation
                .WithSender(walletAddress!)
                .WithCallData(execCounterCountFromEntryPoint)
                .SignedAndResolved(TestItem.PrivateKeyA, chain.EntryPointAddress, chain.SpecProvider.ChainId)
                .TestObject;
            
            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress, walletConstructor, 0);
            
            UserOperation createOp = Build.A.UserOperation
                .WithSender(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallData(execCounterCountFromEntryPoint)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, chain.EntryPointAddress, chain.SpecProvider.ChainId)
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

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress!, walletAddress!);
            UInt256 countBefore1 = _contracts.GetCount(chain, counterAddress!, accountAddress!);
            countBefore.Should().Be(0);
            countBefore1.Should().Be(0);

            chain.SendUserOperation(op);
            chain.SendUserOperation(createOp);
            await chain.AddBlock(true);
            
                byte[] countCalldata = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
                byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature, counterAddress[i]!, 0, countCalldata);
                
                UserOperation op = Build.A.UserOperation
                    .WithSender(walletAddress[i]!)
                    .WithCallData(execCounterCountFromEntryPoint)
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject;
                
                byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress[i]);
                Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress[i], walletConstructor, 0, i);
                
                UserOperation createOp = Build.A.UserOperation
                    .WithSender(accountAddress!)
                    .WithInitCode(walletConstructor)
                    .WithCallData(execCounterCountFromEntryPoint)
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
                    .WithTo(walletAddress[i]!)
                    .WithGasLimit(100_000)
                    .WithGasPrice(2)
                    .WithValue(1.Ether())
                    .WithNonce(1)
                    .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
                await chain.AddBlock(true, fundTransaction, fundTransaction2);

                UInt256 countBefore = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                UInt256 countBefore1 = _contracts.GetCount(chain, counterAddress[i]!, accountAddress!);
                countBefore.Should().Be(0);
                countBefore1.Should().Be(0);

                chain.SendUserOperation(entryPointAddress[i], op);
                chain.SendUserOperation(entryPointAddress[i], createOp);
                await chain.AddBlock(true);
                
                chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);

                UInt256 countAfter = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                UInt256 countAfter1 = _contracts.GetCount(chain, counterAddress[i]!, accountAddress!);
                countAfter.Should().Be(1);
                countAfter1.Should().Be(1);
                
        }
        
        [Test]
        public async Task Should_create_account_with_tokens()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 20_000_000;

            // string[] epAddresses = {"0xdb8b5f6080a8e466b64a8d7458326cb650b3353f", "0x90f3e1105e63c877bf9587de5388c23cdb702c6b"};

            byte[] paymasterBytecode = Bytes.Concat(
                _contracts.TokenPaymasterAbi.Bytecode!,
                _encoder.Encode(
                    AbiEncodingStyle.None,
                    _contracts.TokenPaymasterAbi.Constructors[0].GetCallInfo().Signature, "tst", 
                    chain.EntryPointAddress));
            
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] paymasterAddress) = await _contracts.Deploy(chain, paymasterBytecode);

            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress, walletConstructor, 0);

            byte[] addStakeCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TokenPaymasterAbi.Functions["addStake"].GetCallInfo().Signature, 0);
            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(paymasterAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(2.Ether())
                .WithData(addStakeCallData)
                .WithNonce(chain.State.GetNonce(Contracts.ContractCreatorPrivateKey.Address))
                .SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
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
            
            UserOperation createOp = Build.A.UserOperation
                .WithPaymaster(paymasterAddress!)
                .WithSender(accountAddress)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, chain.EntryPointAddress, chain.SpecProvider.ChainId)
                .TestObject;
            chain.SendUserOperation(createOp);
            await chain.AddBlock(true);

                chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);

            }
        }

        public static void SignUserOperation(UserOperation op, PrivateKey privateKey, Address entryPointAddress, ulong chainId)
        {
            Keccak requestId = op.CalculateRequestId(entryPointAddress, chainId);
            
            Signer signer = new(chainId, privateKey, NullLogManager.Instance);
            Keccak hashedRequestId = Keccak.Compute(
                Bytes.Concat(
                    Encoding.UTF8.GetBytes("\x19"),
                    Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + requestId.Bytes.Length),
                    requestId.Bytes)
            );
            Signature signature = signer.Sign(hashedRequestId);

            op.Signature = Bytes.FromHexString(signature.ToString());
        }
    }
}
