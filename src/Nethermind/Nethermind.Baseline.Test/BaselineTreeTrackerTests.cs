//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.IO;
using System.IO.Abstractions;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Baseline.Test.Contracts;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public class BaselineTreeTrackerTests
    {
        private IFileSystem _fileSystem;

        [SetUp]
        public void SetUp()
        {
            _fileSystem = Substitute.For<IFileSystem>();
            const string expectedFilePath = "contracts/MerkleTreeSHA.bin";
            _fileSystem.File.ReadAllLinesAsync(expectedFilePath).Returns(File.ReadAllLines(expectedFilePath));
        }
        [Test]
        public async Task Tree_tracker_should_track_blocks()
        {
            var address = TestItem.Addresses[0];
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(address, new SecureString());
            await testRpc.AddFunds(address, 1.Ether());

            BaselineTree baselineTree = BuildATree();
            new BaselineTreeTracker(ContractAddress.From(address, 0), baselineTree, testRpc.LogFinder, testRpc.BlockFinder, testRpc.BlockProcessor);
            BaselineModule baselineModule = new BaselineModule(
    testRpc.TxSender,
    testRpc.StateReader,
    testRpc.LogFinder,
    testRpc.BlockTree,
    new AbiEncoder(),
    _fileSystem,
    new MemDb(),
    LimboLogs.Instance,
    testRpc.BlockProcessor);
            Keccak txHash = (await baselineModule.baseline_deploy(address, "MerkleTreeSHA")).Data;
            await testRpc.AddBlock();
            AbiEncoder abiEncoder = new AbiEncoder();
            ReadOnlyTxProcessorSource readOnlyTransactionProcessorSource = new ReadOnlyTxProcessorSource(new MemDbProvider(), testRpc.BlockTree, testRpc.SpecProvider, LimboLogs.Instance);
            var contract = new MerkleTreeSHAContract(abiEncoder, ContractAddress.From(address, 0), readOnlyTransactionProcessorSource);
            var hash = TestItem.KeccakA;
            var transaction = contract.InsertLeaf(address, hash);
            await testRpc.TxSender.SendTransaction(transaction, TxPool.TxHandlingOptions.ManagedNonce);

            await testRpc.AddBlock(transaction);
        }

        private BaselineTree BuildATree(IKeyValueStore keyValueStore = null)
        {
            return new ShaBaselineTree(keyValueStore ?? new MemDb(), new byte[] { }, 0);
        }
    }
}
