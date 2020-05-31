using System.IO;
using System.IO.Abstractions;
using System.Security;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.JsonRpc;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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

            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA");
            await testRpc.AddBlock();

            testRpc.BlockTree.Head.Number.Should().Be(5);
            testRpc.BlockTree.Head.Transactions.Should().Contain(tx => tx.IsContractCreation);

            var code = testRpc.StateReader
                .GetCode(testRpc.BlockTree.Head.StateRoot, ContractAddress.From(TestItem.Addresses[0], 0));

            code.Should().NotBeEmpty();
        }

        [Test]
        public async Task insert_leaf_given_hash_is_emitting_an_event()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            var testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            BaselineModule baselineModule = new BaselineModule(testRpc.TxPoolBridge, _abiEncoder, _fileSystem, LimboLogs.Instance);
            await testRpc.AddFunds(TestItem.Addresses[0], 1.Ether());
            Keccak txHash = (await baselineModule.baseline_deploy(TestItem.Addresses[0], "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();

            ReceiptForRpc receipt = (await testRpc.EthModule.eth_getTransactionReceipt(txHash)).Data;
            
            Keccak insertLeafTxHash = (await baselineModule.baseline_insertLeaf(TestItem.Addresses[1], receipt.ContractAddress, TestItem.KeccakH)).Data;
            await testRpc.AddBlock();
            
            ReceiptForRpc insertLeafReceipt = (await testRpc.EthModule.eth_getTransactionReceipt(insertLeafTxHash)).Data;
            insertLeafReceipt.Logs.Should().HaveCount(1);
        }
    }
}