// Copyright(c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Nethermind.Baseline.Test.JsonRpc
{
    [TestFixture]
    public class BaselineModuleTests
    {
        private IAbiEncoder _abiEncoder = new AbiEncoder();
        private IFileSystem _fileSystem;

        [SetUp]
        public void SetUp()
        {
            _abiEncoder = new AbiEncoder();
            _fileSystem = Substitute.For<IFileSystem>();
            const string expectedFilePath = "contracts/MerkleTreeSHA.bin";
            _fileSystem.File.ReadAllLinesAsync(expectedFilePath).Returns(File.ReadAllLines(expectedFilePath));
        }

        [Test]
        public async Task deploy_deploys_the_contract()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA");
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);

            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(2);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }

        [Test]
        public async Task deploy_bytecode_deploys_the_contract()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_deployBytecode(
                TestItem.Addresses[0],
                File.ReadAllText("testBytecode"));
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);

            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(2);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }

        [TestCase(" ")]
        [TestCase(null)]
        [TestCase("x")]
        [TestCase("1")]
        [TestCase("123")]
        [TestCase("1g")]
        [TestCase("0x1")]
        public async Task deploy_bytecode_validates_input(string bytecode)
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_deployBytecode(
                TestItem.Addresses[0],
                bytecode); // invalid input

            result.Data.Should().Be(null);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
            result.Result.Error.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(2);
            testRpc.BlockTree.Head.Transactions.Should().NotContain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().BeEmpty();
        }

        [Test]
        public async Task deploy_returns_an_error_when_file_is_missing()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MissingContract");
            result.Data.Should().Be(null);
            result.ErrorCode.Should().Be(ErrorCodes.ResourceNotFound);
            result.Result.Error.Should().NotBeEmpty();
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public async Task insert_commit_given_hash_is_emitting_an_event()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), new MemDb(), new byte[] { }, 0, LimboNoErrorLogger.Instance);
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();


            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            Keccak insertLeafTxHash = (
                await baselineModule.baseline_insertCommit(
                    TestItem.Addresses[1],
                    receipt.ContractAddress,
                    TestItem.KeccakH)).Data;
            await testRpc.AddBlock();

            ReceiptForRpc insertLeafReceipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(insertLeafTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(128)]
        public async Task insert_commits_given_hash_is_emitting_an_event(int leafCount)
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            Keccak[] leaves = Enumerable.Repeat(TestItem.KeccakH, leafCount).ToArray();
            Keccak insertLeavesTxHash = (await baselineModule.baseline_insertCommits(
                TestItem.Addresses[1],
                receipt.ContractAddress,
                leaves)).Data;
            await testRpc.AddBlock();

            ReceiptForRpc insertLeafReceipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(
                insertLeavesTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
            insertLeafReceipt.Logs[0].Data.Length.Should().Be(128 + leafCount * 32);
        }

        [Test]
        public async Task can_get_siblings_after_commit_is_added()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(
                TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getSiblings(receipt.ContractAddress, 0);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data.Should().HaveCount(32);

            Keccak root = (await baselineModule.baseline_getRoot(receipt.ContractAddress)).Data;
            bool verificationResult =
                (await baselineModule.baseline_verify(
                    receipt.ContractAddress,
                    root,
                    TestItem.KeccakH,
                    result.Data))
                .Data;

            verificationResult.Should().Be(true);
        }

        [Test]
        public async Task can_get_commit_fails_on_not_tracked()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getCommit(receipt.ContractAddress, 0);

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_commit_fails_on_wrong_index()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getCommit(receipt.ContractAddress, (UInt256) uint.MaxValue + 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_commit_after_commit_is_added()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getCommit(receipt.ContractAddress, 0);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data.Hash.Should().NotBe(Keccak.Zero);
        }

        [Test]
        public async Task can_get_commits_after_commit_is_added()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getCommits(receipt.ContractAddress, new UInt256[] {0, 1});
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data[0].Hash.Should().Be(TestItem.KeccakH);
            result.Data[1].Hash.Should().Be(Keccak.Zero);
        }

        [Test]
        public async Task can_get_commits_fails_if_not_tracking()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getCommits(receipt.ContractAddress, new UInt256[] {0, 1});
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_commits_fails_if_any_index_invalid()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_track(receipt.ContractAddress);
            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getCommits(
                receipt.ContractAddress, new UInt256[] {0, (UInt256) uint.MaxValue + 1});
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_work_with_many_trees()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            Keccak txHash2 = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            Keccak txHash3 = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();
            await testRpc.AddBlock();
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;
            ReceiptForRpc receipt2 = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash2)).Data;
            ReceiptForRpc receipt3 = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash3)).Data;

            receipt.Status.Should().Be(1);
            receipt2.Status.Should().Be(1);
            receipt3.Status.Should().Be(1);

            await baselineModule.baseline_insertCommits(
                TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakG, TestItem.KeccakH);
            await baselineModule.baseline_insertCommits(
                TestItem.Addresses[1], receipt2.ContractAddress, TestItem.KeccakE, TestItem.KeccakF);
            await baselineModule.baseline_insertCommit(
                TestItem.Addresses[1], receipt3.ContractAddress, TestItem.KeccakG);
            await baselineModule.baseline_insertCommit(
                TestItem.Addresses[1], receipt3.ContractAddress, TestItem.KeccakH);

            await testRpc.AddBlock();
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            await baselineModule.baseline_track(receipt2.ContractAddress);
            await baselineModule.baseline_track(receipt3.ContractAddress);

            var result = await baselineModule.baseline_getSiblings(receipt.ContractAddress, 1);
            var result2 = await baselineModule.baseline_getSiblings(receipt2.ContractAddress, 1);
            var result3 = await baselineModule.baseline_getSiblings(receipt3.ContractAddress, 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Data.Should().HaveCount(32);

            result2.Result.ResultType.Should().Be(ResultType.Success);
            result2.Data.Should().HaveCount(32);

            result3.Result.ResultType.Should().Be(ResultType.Success);
            result3.Data.Should().HaveCount(32);

            for (int i = 1; i < 32; i++)
            {
                result.Data[i].Hash.Should().Be(Keccak.Zero);
                result2.Data[i].Hash.Should().Be(Keccak.Zero);
                result3.Data[i].Hash.Should().Be(Keccak.Zero);
            }

            result.Data[0].Hash.Should().NotBe(Keccak.Zero);
            result2.Data[0].Hash.Should().NotBe(Keccak.Zero);
            result3.Data[0].Hash.Should().NotBe(Keccak.Zero);

            result.Data[0].Hash.Should().NotBe(result2.Data[0].Hash);
            result.Data[0].Hash.Should().Be(result3.Data[0].Hash);
        }

        [Test]
        public async Task track_request_will_succeed()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = CreateBaselineModule(testRpc, stateReader);

            
            stateReader.GetCode(Arg.Any<Keccak>(), TestItem.AddressC).Returns(new byte[] {255});
            var result = await baselineModule.baseline_track(TestItem.AddressC);

            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task concurrent_track_requests_will_succeed()
        {
            Random random = new(42);

            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = CreateBaselineModule(testRpc, stateReader);

            int iterationsPerTask = 1000;
            Action trackAction = () =>
            {
                for (int i = 0; i < iterationsPerTask; i++)
                {
                    byte[] bytes = new byte[20];
                    random.NextBytes(bytes);
                    Address address = new(bytes);

                    stateReader.GetCode(Arg.Any<Keccak>(), address).Returns(new byte[] {255});
                    var result = baselineModule.baseline_track(address).Result; // safe to invoke Result here
                    result.Result.ResultType.Should().Be(ResultType.Success);
                }
            };

            Task task1 = new(trackAction);
            Task task2 = new(trackAction);
            Task task3 = new(trackAction);

            task1.Start();
            task2.Start();
            task3.Start();

            await Task.WhenAll(task1, task2, task3);
        }

        [Test]
        public async Task second_track_request_will_fail()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Address treeAddress = await Deploy(testRpc, baselineModule);
            
            var result =  await baselineModule.baseline_track(treeAddress);
            result.Result.ResultType.Should().Be(ResultType.Success);
            
            result = await baselineModule.baseline_track(treeAddress);

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }
        
        [Test]
        public async Task track_untrack_track_works()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Address treeAddress = await Deploy(testRpc, baselineModule);
            
            var result = await baselineModule.baseline_track(treeAddress);
            result.Result.ResultType.Should().Be(ResultType.Success);
            result = await baselineModule.baseline_untrack(treeAddress);
            result.Result.ResultType.Should().Be(ResultType.Success);
            result = await baselineModule.baseline_track(treeAddress);
            result.Result.ResultType.Should().Be(ResultType.Success);
            
            var countResult = await baselineModule.baseline_getCount(treeAddress);
            countResult.Result.ResultType.Should().Be(ResultType.Success);
        }
        
        [Test]
        public async Task track_untrack_will_cause_tracking_checks_to_start_failing()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Address treeAddress = await Deploy(testRpc, baselineModule);
            
            var result = await baselineModule.baseline_track(treeAddress);
            result.Result.ResultType.Should().Be(ResultType.Success);
            result = await baselineModule.baseline_untrack(treeAddress);
            var keccakResult = await baselineModule.baseline_insertCommit(TestItem.Addresses[0], TestItem.AddressC, Keccak.Zero);
            keccakResult.Result.ResultType.Should().Be(ResultType.Failure);
            
            keccakResult = await baselineModule.baseline_insertCommits(TestItem.Addresses[0], TestItem.AddressC, Keccak.Zero);
            keccakResult.Result.ResultType.Should().Be(ResultType.Failure);
        }
        
        [Test]
        public async Task untrack_fails_when_not_tracked()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_untrack(TestItem.AddressC);
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public async Task track_on_an_empty_code_account_will_fail()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            var result = await baselineModule.baseline_track(TestItem.AddressC);

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(123u)]
        public async Task can_return_tracked_list(uint trackedCount)
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = CreateBaselineModule(testRpc, stateReader);

            for (int i = 0; i < trackedCount; i++)
            {
                stateReader.GetCode(null, null).ReturnsForAnyArgs(new byte[] {255});
                await baselineModule.baseline_track(TestItem.Addresses[i]);
            }

            var result = (await baselineModule.baseline_getTracked());
            result.Data.Length.Should().Be((int) trackedCount);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(123u)]
        public async Task can_restore_tracking_list_on_startup(uint trackedCount)
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            MemDb memDb = new();
            MemDb baselineMetaDataDb = new();

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = new(
                testRpc.TxSender,
                stateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                memDb,
                baselineMetaDataDb,
                LimboLogs.Instance,
                testRpc.BlockProcessor,
                new DisposableStack());

            for (int i = 0; i < trackedCount; i++)
            {
                stateReader.GetCode(null, null).ReturnsForAnyArgs(new byte[] {255});
                await baselineModule.baseline_track(TestItem.Addresses[i]); // any address (no need for tree there)    
            }

            BaselineModule restored = new(
                testRpc.TxSender,
                stateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                memDb,
                baselineMetaDataDb,
                LimboLogs.Instance,
                testRpc.BlockProcessor,
                new DisposableStack());

            var resultRestored = await restored.baseline_getTracked();
            resultRestored.Data.Length.Should().Be((int) trackedCount);
        }

        [Test]
        public async Task cannot_get_siblings_after_commit_is_added_if_not_traced()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Address treeAddress = await Deploy(testRpc, baselineModule);

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], treeAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(treeAddress, 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBe(null);
            result.ErrorCode.Should().NotBe(0);
            result.Data.Should().BeNull();
        }

        private static async Task<Address> Deploy(TestRpcBlockchain testRpc, BaselineModule baselineModule)
        {
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;
            return receipt.ContractAddress;
        }

        [TestCase(-1L)]
        [TestCase(uint.MaxValue + 1L)]
        public async Task can_get_siblings_is_protected_against_overflow(long leafIndex)
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = CreateBaselineModule(testRpc);

            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertCommit(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(receipt.ContractAddress, leafIndex);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
            result.Data.Should().BeNull();
        }

        private async Task RunAll(TestRpcBlockchain testRpc, BaselineModule baselineModule, int taskId)
        {
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt;
            int tries = 100;
            do
            {
                receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;
                await Task.Delay(10);
                tries--;
            } while (receipt != null && tries > 0);

            if (receipt == null)
            {
                throw new InvalidOperationException($"Receipt is null in task {taskId}");
            }
            
            Address contract = receipt.ContractAddress;
            Console.WriteLine($"Task {taskId} operating on contract {contract}");
            
            await baselineModule.baseline_track(contract);

            for (int i = 0; i < 16; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                await baselineModule.baseline_insertCommit(TestItem.Addresses[taskId], contract, TestItem.Keccaks[i % TestItem.Keccaks.Length]);
                await testRpc.AddBlock();
                Block headBlock = testRpc.BlockTree.Head;
                BaselineTreeNode[] siblings = (await baselineModule.baseline_getSiblings(
                    contract, 0, new BlockParameter(headBlock.Number))).Data;
                Keccak root = (await baselineModule.baseline_getRoot(
                    contract, new BlockParameter(headBlock.Number))).Data;
                bool result = (await baselineModule.baseline_verify(
                    contract, root, TestItem.Keccaks[0], siblings, new BlockParameter(headBlock.Number))).Data;
                if (!result)
                {
                    throw new InvalidOperationException($"Failed to verify at {contract}, task {taskId}, iteration {i}, root {root}");
                }
                else
                {
                    Console.WriteLine($"Verified at {contract}, task {taskId}, iteration {i}, root {root}");
                }
            }
            
            Console.WriteLine($"Finishing task {taskId}");
        }
            
            
        [Test, Ignore("Not running well on CI")]
        public async Task Parallel_calls()
        {
            SingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance, 1);
            using TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest<BaseLineRpcBlockchain>(
                SealEngineType.NethDev).Build(spec, 100000.Ether());
            BaselineModule baselineModule = CreateBaselineModule(testRpc);
;            
            for (int i = 0; i < 255; i++)
            {
                testRpc.TestWallet.UnlockAccount(TestItem.Addresses[i], new SecureString());
                await testRpc.AddFunds(TestItem.Addresses[i], 100.Ether());    
            }

            // Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            // await testRpc.AddBlock();
            // ReceiptForRpc receipt = (await testRpc.EthRpcModule.eth_getTransactionReceipt(txHash)).Data;

            List<Task> tasks = new();
            for (int i = 0; i < 16; i++)
            {
                Task task = RunAll(testRpc, baselineModule, i);
                tasks.Add(task);
            }

            await Task.WhenAny(Task.Delay(30000), Task.WhenAny(tasks)).ContinueWith(t =>
            {
                foreach (Task task in tasks)
                {
                    if (task.IsFaulted)
                    {
                        ExceptionHelper.Rethrow(task.Exception!.InnerException);
                    }
                }
            });
            
            await Task.WhenAny(Task.Delay(30000), Task.WhenAll(tasks)).ContinueWith(t =>
            {
                foreach (Task task in tasks)
                {
                    if (task.IsFaulted)
                    {
                        ExceptionHelper.Rethrow(task.Exception!.InnerException);
                    }
                }
            });
        }

        private BaselineModule CreateBaselineModule(TestRpcBlockchain testRpc, IStateReader stateReader = null)
        {
            return new(
                testRpc.TxSender,
                stateReader ?? testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                new MemDb(),
                LimboLogs.Instance,
                testRpc.BlockProcessor,
                new DisposableStack());
        }
        
        private class BaseLineRpcBlockchain : TestRpcBlockchain
        {
            protected override async Task AddBlocksOnStart()
            {
                await AddFunds((TestItem.Addresses[0], 1.Ether()), (TestItem.Addresses[1], 1.Ether()));
            }
        }
    }
}
