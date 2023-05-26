// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

                for (int i = 0; i < entryPointNum; i++)
                {

                    byte[] entryPointConstructorBytes = Bytes.Concat(EntryPointAbi[i].Bytecode!, _encoder.Encode(AbiEncodingStyle.None, EntryPointAbi[i].Constructors[0].GetCallInfo().Signature, singletonFactoryAddress, 0, 2));
                    byte[] createEntryPointBytes = _encoder.Encode(AbiEncodingStyle.IncludeSignature, SingletonFactory.Functions["deploy"].GetCallInfo().Signature, entryPointConstructorBytes, Bytes.Zero32);

                    Transaction entryPointTx = Core.Test.Builders.Build.A.Transaction.WithTo(singletonFactoryAddress).WithData(createEntryPointBytes).WithGasLimit(6_000_000).WithNonce(chain.State.GetNonce(ContractCreatorPrivateKey.Address)).WithValue(0).SignedAndResolved(ContractCreatorPrivateKey).TestObject;
                    await chain.AddBlock(true, entryPointTx);

                    Address computedAddress = new(Keccak.Compute(Bytes.Concat(Bytes.FromHexString("0xff"), singletonFactoryAddress.Bytes, Bytes.Zero32, Keccak.Compute(entryPointConstructorBytes).Bytes)).BytesToArray().TakeLast(20).ToArray());

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
            createOp.CalculateRequestId(entryPointId, chainId);
            Assert.That(createOp.RequestId!, Is.EqualTo(idFromTransaction),
                "Request IDs do not match.");

            Assert.That(
                createOp.Signature, Is.EqualTo(Bytes.FromHexString("0xe4ef96c1ebffdae061838b79a0ba2b0289083099dc4d576a7ed0c61c80ed893273ba806a581c72be9e550611defe0bf490f198061b8aa63dd6acfc0b620e0c871c")),
                "signatures are different"
            );
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task Should_execute_well_formed_op_successfully_if_codehash_not_changed(bool changeCodeHash, bool success)
        {
            var chain = await CreateChain();
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);

            byte[] countCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature, counterAddress[0]!, 0, countCallData);

            UserOperation op = Build.A.UserOperation
                .WithSender(walletAddress[0]!)
                .WithCallData(execCounterCountFromEntryPoint)
                .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[0], chain.SpecProvider.ChainId)
                .TestObject;

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress[0]!, walletAddress[0]!);
            countBefore.Should().Be(0);

            chain.SendUserOperation(entryPointAddress[0], op);
            if (changeCodeHash)
            {
                chain.State.InsertCode(walletAddress[0]!, Bytes.Concat(chain.State.GetCode(walletAddress[0]!), 0x00), chain.SpecProvider.GenesisSpec);
                chain.State.Commit(chain.SpecProvider.GenesisSpec);
                chain.State.RecalculateStateRoot();
                chain.State.CommitTree(chain.BlockTree.Head!.Number);
                await chain.BlockTree.SuggestBlockAsync(new BlockBuilder().WithStateRoot(chain.State.StateRoot).TestObject);
            }
            await chain.AddBlock(true);

            if (success)
            {
                Assert.That(
                    () => _contracts.GetCount(chain, counterAddress[0]!, walletAddress[0]!),
                    Is.EqualTo(UInt256.One).After(2000, 50));
            }
            else
            {
                Assert.That(
                    () => _contracts.GetCount(chain, counterAddress[0]!, walletAddress[0]!),
                    Is.EqualTo(UInt256.Zero).After(2000, 50));
            }
        }

        [Test]
        public async Task Should_display_the_list_of_supported_entry_points()
        {
            var chain = await CreateChain();
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);
            chain.SupportedEntryPoints();
        }

        [Test]
        public async Task Should_execute_well_formed_op_successfully_for_all_entry_points()
        {
            var chain = await CreateChain();
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);

            for (int i = 0; i < entryPointNum; i++)
            {

                byte[] countCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                    _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
                byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                    _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature,
                    counterAddress[i]!, 0, countCallData);

                UserOperation op = Build.A.UserOperation
                    .WithSender(walletAddress[i]!)
                    .WithCallData(execCounterCountFromEntryPoint)
                    .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[i], chain.SpecProvider.ChainId)
                    .TestObject;

                /*
                Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                    .WithTo(walletAddress[i]!)
                    .WithGasLimit(1_000_000)
                    .WithGasPrice(2)
                    .WithValue(1.Ether())
                    .WithNonce((UInt256)(i))
                    .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
                await chain.AddBlock(true, fundTransaction);
                */

                UInt256 countBefore = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                countBefore.Should().Be(0);

                chain.SendUserOperation(entryPointAddress[i], op);
                await chain.AddBlock(true);

                UInt256 countAfter = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                countAfter.Should().Be(1);

            }

        }

        [Test]
        public async Task Should_execute_well_formed_op_successfully_for_all_entry_points_at_the_same_time()
        {
            var chain = await CreateChain();
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);

            for (int i = 0; i < entryPointNum; i++)
            {

                byte[] countCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                    _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
                byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                    _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature,
                    counterAddress[i]!, 0, countCallData);

                UserOperation op = Build.A.UserOperation
                    .WithSender(walletAddress[i]!)
                    .WithCallData(execCounterCountFromEntryPoint)
                    .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[i], chain.SpecProvider.ChainId)
                    .TestObject;

                /*
                Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                    .WithTo(walletAddress[i]!)
                    .WithGasLimit(1_000_000)
                    .WithGasPrice(2)
                    .WithValue(1.Ether())
                    .WithNonce((UInt256)(i))
                    .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
                await chain.AddBlock(true, fundTransaction);
                */

                UInt256 countBefore = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                countBefore.Should().Be(0);

                chain.SendUserOperation(entryPointAddress[i], op);
            }


            await chain.AddBlock(true);

            for (int i = 0; i < entryPointNum; i++)
            {
                UInt256 countAfter = _contracts.GetCount(chain, counterAddress[i]!, walletAddress[i]!);
                countAfter.Should().Be(1);
            }

            Console.WriteLine("2");
        }

        [Test]
        public async Task Should_succeed_at_creating_account_after_prefund()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 20_000_000;
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain);

            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress[0]);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress[0], walletConstructor, 0, 0);

            UserOperation createOp = Build.A.UserOperation
                .WithSender(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[0], chain.SpecProvider.ChainId)
                .TestObject;

            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(accountAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction);

            chain.SendUserOperation(entryPointAddress[0], createOp);
            await chain.AddBlock(true);

            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);
        }

        [Test]
        public async Task Should_batch_multiple_ops()
        {
            var chain = await CreateChain();
            chain.GasLimitCalculator.GasLimit = 30_000_000;
            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] counterAddress) = await _contracts.Deploy(chain, _contracts.TestCounterAbi.Bytecode!);

            byte[] countCalldata = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.TestCounterAbi.Functions["count"].GetCallInfo().Signature);
            byte[] execCounterCountFromEntryPoint = _encoder.Encode(AbiEncodingStyle.IncludeSignature, _contracts.SimpleWalletAbi.Functions["execFromEntryPoint"].GetCallInfo().Signature, counterAddress[0]!, 0, countCalldata);

            UserOperation op = Build.A.UserOperation
                .WithSender(walletAddress[0]!)
                .WithCallData(execCounterCountFromEntryPoint)
                .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[0], chain.SpecProvider.ChainId)
                .TestObject;

            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress[0]);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress[0], walletConstructor, 0, 0);

            UserOperation createOp = Build.A.UserOperation
                .WithSender(accountAddress!)
                .WithInitCode(walletConstructor)
                .WithCallData(execCounterCountFromEntryPoint)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[0], chain.SpecProvider.ChainId)
                .TestObject;

            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(accountAddress!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(0)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            Transaction fundTransaction2 = Core.Test.Builders.Build.A.Transaction
                .WithTo(walletAddress[0]!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(1.Ether())
                .WithNonce(1)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
            await chain.AddBlock(true, fundTransaction, fundTransaction2);

            UInt256 countBefore = _contracts.GetCount(chain, counterAddress[0]!, walletAddress[0]!);
            UInt256 countBefore1 = _contracts.GetCount(chain, counterAddress[0]!, accountAddress!);
            countBefore.Should().Be(0);
            countBefore1.Should().Be(0);

            chain.SendUserOperation(entryPointAddress[0], op);
            chain.SendUserOperation(entryPointAddress[0], createOp);
            await chain.AddBlock(true);

            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);

            UInt256 countAfter = _contracts.GetCount(chain, counterAddress[0]!, walletAddress[0]!);
            UInt256 countAfter1 = _contracts.GetCount(chain, counterAddress[0]!, accountAddress!);
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
                    new Address("0xb0894727fe4ff102e1f1c8a16f38afc7b859f215")));

            (Address[] entryPointAddress, Address?[] walletAddress, Address?[] paymasterAddress) =
                await _contracts.Deploy(chain, paymasterBytecode);

            byte[] walletConstructor = _contracts.GetWalletConstructor(entryPointAddress[0]);
            Address accountAddress = _contracts.GetAccountAddress(chain, entryPointAddress[0], walletConstructor, 0, 0);

            byte[] addStakeCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                _contracts.TokenPaymasterAbi.Functions["addStake"].GetCallInfo().Signature, 0);
            Transaction fundTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(paymasterAddress[0]!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithValue(2.Ether())
                .WithData(addStakeCallData)
                .WithNonce(chain.State.GetNonce(Contracts.ContractCreatorPrivateKey.Address))
                .SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
            await chain.AddBlock(true, fundTransaction);

            byte[] mintTokensCallData = _encoder.Encode(AbiEncodingStyle.IncludeSignature,
                _contracts.TokenPaymasterAbi.Functions["mintTokens"].GetCallInfo().Signature, accountAddress,
                1.Ether());
            Transaction mintTokensTransaction = Core.Test.Builders.Build.A.Transaction
                .WithTo(paymasterAddress[0]!)
                .WithGasLimit(100_000)
                .WithGasPrice(2)
                .WithData(mintTokensCallData)
                .WithValue(0)
                .WithNonce(chain.State.GetNonce(Contracts.ContractCreatorPrivateKey.Address))
                .SignedAndResolved(Contracts.ContractCreatorPrivateKey).TestObject;
            await chain.AddBlock(true, mintTokensTransaction);

            UserOperation createOp = Build.A.UserOperation
                .WithPaymaster(paymasterAddress[0]!)
                .WithSender(accountAddress)
                .WithInitCode(walletConstructor)
                .WithCallGas(10_000_000)
                .WithVerificationGas(2_000_000)
                .SignedAndResolved(TestItem.PrivateKeyA, entryPointAddress[0], chain.SpecProvider.ChainId)
                .TestObject;
            chain.SendUserOperation(entryPointAddress[0], createOp);
            await chain.AddBlock(true);

            chain.State.GetCode(accountAddress).Should().BeEquivalentTo(_contracts.SimpleWalletAbi.DeployedBytecode!);
        }

        public static void SignUserOperation(UserOperation op, PrivateKey privateKey, Address entryPointAddress, ulong chainId)
        {
            op.CalculateRequestId(entryPointAddress, chainId);

            Signer signer = new(chainId, privateKey, NullLogManager.Instance);
            Keccak hashedRequestId = Keccak.Compute(
                Bytes.Concat(
                    Encoding.UTF8.GetBytes("\x19"),
                    Encoding.UTF8.GetBytes("Ethereum Signed Message:\n" + op.RequestId!.Bytes.Length),
                    op.RequestId!.Bytes)
            );
            Signature signature = signer.Sign(hashedRequestId);

            op.Signature = Bytes.FromHexString(signature.ToString());
        }
    }
}
