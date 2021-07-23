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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Witness;
using Nethermind.Logging;
using Nethermind.State.Witnesses;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class WitnessModuleTests
    {
        private const string OneNodeResponse =
            "{\"jsonrpc\":\"2.0\",\"result\":[\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\"],\"id\":67}";

        private IBlockFinder _blockFinder;

        private WitnessCollector _witnessRepository;
        private WitnessRpcModule _witnessRpcModule;

        private Block _block;


        [SetUp]
        public async Task Setup()
        {
            _block =  Build.A.Block
                .WithOmmers(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;
            _blockFinder = Substitute.For<IBlockTree>();
            _blockFinder.FindBlock((BlockParameter)null).ReturnsForAnyArgs(_block);

            _witnessRepository = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            
            _witnessRepository.Add(_block.Hash);
            _witnessRepository.Persist(_block.Hash);
            _witnessRpcModule = new WitnessRpcModule(_witnessRepository, _blockFinder);
        }

        [Test]
        public void GetOneWitnessHash()
        {
            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", _block.Hash.ToString());
            serialized.Should().Be(OneNodeResponse);
        }
    }
}
