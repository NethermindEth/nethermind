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

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
        private const string GetOneWitnessHashResponse =
            "{\"jsonrpc\":\"2.0\",\"result\":[\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\"],\"id\":67}";
        private const string BlockNotFoundResponse = 
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Block not found\"},\"id\":67}";
        private const string WitnessNotFoundResponse = 
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Witness not found\"},\"id\":67}";

        private IBlockFinder _blockFinder;

        private WitnessCollector _witnessRepository;
        private WitnessRpcModule _witnessRpcModule;

        private Block _block;
        
        private Block _block2;
        private Keccak _hash;


        [SetUp]
        public void Setup()
        {
            _block2 = Build.An.Block.TestObject;
            _block =  Build.A.Block
                .WithOmmers(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;
            
            _hash = _block.CalculateHash();
        }

        [Test]
        public void GetOneWitnessHash()
        {
            _blockFinder = Substitute.For<IBlockTree>();
            _blockFinder.FindHeader((BlockParameter)null).ReturnsForAnyArgs(_block.Header);

            _witnessRepository = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            _witnessRepository.Add(_hash);
            _witnessRepository.Persist(_hash);
            _witnessRpcModule = new WitnessRpcModule(_witnessRepository, _blockFinder);

            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", _block.CalculateHash().ToString());
            serialized.Should().Be(GetOneWitnessHashResponse);
        }

        [Test]
        public void BlockNotFound()
        {
            _blockFinder = Substitute.For<IBlockTree>();
            _witnessRepository = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            _witnessRpcModule = new WitnessRpcModule(_witnessRepository, _blockFinder);

            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "0x583");
            serialized.Should().Be(BlockNotFoundResponse);
        }

        [Test]
        public void WitnessNotFound()
        {
            _blockFinder = Substitute.For<IBlockTree>();
            _blockFinder.FindHeader((BlockParameter)null).ReturnsForAnyArgs(_block2.Header);

            _witnessRepository = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            _witnessRpcModule = new WitnessRpcModule(_witnessRepository, _blockFinder);


            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "0x1");
            serialized.Should().Be(WitnessNotFoundResponse);
            
        }
    }
}
