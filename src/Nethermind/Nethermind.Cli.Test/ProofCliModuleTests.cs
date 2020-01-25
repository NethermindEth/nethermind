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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Cli.Modules;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Cli.Test
{
    public class ProofCliModuleTests
    {
        private IProofModule _proofModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;

        [SetUp]
        public void Setup()
        {
            InMemoryReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            _blockTree = Build.A.BlockTree().WithTransactions(receiptStorage, specProvider).OfChainLength(10).TestObject;
            _dbProvider = new MemDbProvider();
            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new CompositeDataRecoveryStep(),
                receiptStorage,
                specProvider,
                LimboLogs.Instance);

            _proofModule = moduleFactory.Create();
        }

        [Test]
        public void Test1()
        {
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            CliEngine engine = new CliEngine();
            NodeManager nodeManager = new NodeManager(engine, serializer, LimboLogs.Instance);
            ProofCliModule proofCliModule = new ProofCliModule(engine, nodeManager);

            proofCliModule.GetTransactionByHash($"{TestItem.KeccakA}", true);
        }
    }
}