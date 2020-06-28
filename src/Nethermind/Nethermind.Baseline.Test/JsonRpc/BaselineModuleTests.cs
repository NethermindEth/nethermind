using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
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
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA");
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);

            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }

        [Test]
        public async Task deploy_bytecode_deploys_the_contract()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            var result = await baselineModule.baseline_deployBytecode(
                TestItem.Addresses[0],
                File.ReadAllText("testBytecode"));
            result.Data.Should().NotBe(null);
            result.ErrorCode.Should().Be(0);
            result.Result.Error.Should().BeNull();
            result.Result.ResultType.Should().Be(ResultType.Success);

            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
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
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            var result = await baselineModule.baseline_deployBytecode(
                TestItem.Addresses[0],
                bytecode); // invalid input

            result.Data.Should().Be(null);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
            result.Result.Error.Should().NotBeNull();
            result.Result.ResultType.Should().Be(ResultType.Failure);
            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
            testRpc.BlockTree.Head.Transactions.Should().NotContain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().BeEmpty();
        }

        [Test]
        public async Task deploy_returns_an_error_when_file_is_missing()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            var result = await baselineModule.baseline_deploy(TestItem.Addresses[0], "MissingContract");
            result.Data.Should().Be(null);
            result.ErrorCode.Should().Be(ErrorCodes.ResourceNotFound);
            result.Result.Error.Should().NotBeEmpty();
            result.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public async Task insert_leaf_given_hash_is_emitting_an_event()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            Keccak insertLeafTxHash = (
                await baselineModule.baseline_insertLeaf(
                    TestItem.Addresses[1],
                    receipt.ContractAddress,
                    TestItem.KeccakH)).Data;
            await testRpc.AddBlock();

            ReceiptForRpc insertLeafReceipt = (await testRpc.EthModule.eth_getTransactionReceipt(insertLeafTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(128)]
        public async Task insert_leaves_given_hash_is_emitting_an_event(int leafCount)
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            Keccak[] leaves = Enumerable.Repeat(TestItem.KeccakH, leafCount).ToArray();
            Keccak insertLeavesTxHash = (await baselineModule.baseline_insertLeaves(
                TestItem.Addresses[1],
                receipt.ContractAddress,
                leaves)).Data;
            await testRpc.AddBlock();

            ReceiptForRpc insertLeafReceipt = (await testRpc.EthModule.eth_getTransactionReceipt(
                insertLeavesTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
            insertLeafReceipt.Logs[0].Data.Length.Should().Be(128 + leafCount * 32);
        }

        [Test]
        public async Task can_get_siblings_after_leaf_is_added()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(
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
        public async Task can_get_leaf_fails_on_not_tracked()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getLeaf(receipt.ContractAddress, 0);

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_leaf_fails_on_wrong_index()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getLeaf(receipt.ContractAddress, (UInt256) uint.MaxValue + 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_leaf_after_leaf_is_added()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getLeaf(receipt.ContractAddress, 0);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data.Hash.Should().NotBe(Keccak.Zero);
        }

        [Test]
        public async Task can_get_leaves_after_leaf_is_added()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            await baselineModule.baseline_track(receipt.ContractAddress);
            var result = await baselineModule.baseline_getLeaves(receipt.ContractAddress, 0, 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Success);
            result.Result.Error.Should().Be(null);
            result.ErrorCode.Should().Be(0);
            result.Data[0].Hash.Should().NotBe(Keccak.Zero);
            result.Data[1].Hash.Should().Be(Keccak.Zero);
        }

        [Test]
        public async Task can_get_leaves_fails_if_not_tracking()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getLeaves(receipt.ContractAddress, 0, 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_get_leaves_fails_if_any_index_invalid()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_track(receipt.ContractAddress);
            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getLeaves(receipt.ContractAddress, 0, (UInt256) uint.MaxValue + 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task can_work_with_many_trees()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            Keccak txHash2 = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            Keccak txHash3 = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();
            await testRpc.AddBlock();
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;
            ReceiptForRpc receipt2 = (await testRpc.EthModule.eth_getTransactionReceipt(txHash2)).Data;
            ReceiptForRpc receipt3 = (await testRpc.EthModule.eth_getTransactionReceipt(txHash3)).Data;

            receipt.Status.Should().Be(1);
            receipt2.Status.Should().Be(1);
            receipt3.Status.Should().Be(1);

            await baselineModule.baseline_insertLeaves(
                TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakG, TestItem.KeccakH);
            await baselineModule.baseline_insertLeaves(
                TestItem.Addresses[1], receipt2.ContractAddress, TestItem.KeccakE, TestItem.KeccakF);
            await baselineModule.baseline_insertLeaf(
                TestItem.Addresses[1], receipt3.ContractAddress, TestItem.KeccakG);
            await baselineModule.baseline_insertLeaf(
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
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                stateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            stateReader.GetCode(Arg.Any<Keccak>(), TestItem.AddressC).Returns(new byte[] {255});
            var result = await baselineModule.baseline_track(TestItem.AddressC);

            result.Result.ResultType.Should().Be(ResultType.Success);
        }

        [Test]
        public async Task second_track_request_will_fail()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

            await baselineModule.baseline_track(TestItem.AddressC);
            var result = await baselineModule.baseline_track(TestItem.AddressC);

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        }

        [Test]
        public async Task track_on_an_empty_code_account_will_fail()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());

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
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                stateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

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
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            MemDb memDb = new MemDb();

            IStateReader stateReader = Substitute.For<IStateReader>();
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                stateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                memDb,
                LimboLogs.Instance);

            for (int i = 0; i < trackedCount; i++)
            {
                stateReader.GetCode(null, null).ReturnsForAnyArgs(new byte[] {255});
                await baselineModule.baseline_track(TestItem.Addresses[i]); // any address (no need for tree there)    
            }

            BaselineModule restored = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                memDb,
                LimboLogs.Instance);

            var resultRestored = await restored.baseline_getTracked();
            resultRestored.Data.Length.Should().Be((int) trackedCount);
        }

        [Test]
        public async Task cannot_get_siblings_after_leaf_is_added_if_not_traced()
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(receipt.ContractAddress, 1);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBe(null);
            result.ErrorCode.Should().NotBe(0);
            result.Data.Should().BeNull();
        }

        [TestCase(-1L)]
        [TestCase(uint.MaxValue + 1L)]
        public async Task can_get_siblings_is_protected_against_overflow(long leafIndex)
        {
            SingleReleaseSpecProvider spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(
                testRpc.TxPoolBridge,
                testRpc.StateReader,
                testRpc.LogFinder,
                testRpc.BlockTree,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                LimboLogs.Instance);

            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;

            await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH);
            await testRpc.AddBlock();

            var result = await baselineModule.baseline_getSiblings(receipt.ContractAddress, leafIndex);
            await testRpc.AddBlock();

            result.Result.ResultType.Should().Be(ResultType.Failure);
            result.Result.Error.Should().NotBeNull();
            result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
            result.Data.Should().BeNull();
        }
    }
}