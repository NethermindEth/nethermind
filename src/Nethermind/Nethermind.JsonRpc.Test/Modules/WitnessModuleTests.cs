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
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Witness;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Witnesses;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class WitnessModuleTests
    {
        private const string OneNodeResponse =
            "{\"jsonrpc\":\"2.0\",\"result\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"id\":67}";

        private const string TwoNodesResponse =
            "{\"jsonrpc\":\"2.0\",\"result\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760,0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"id\":67}";
        
        private const string ErrorResponse = 
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Can convert n (represent the number of witness to return) to int\"},\"id\":67}";
        
        private static readonly Keccak _keccakA = Keccak.Compute("A");
        private static readonly Keccak _keccakB = Keccak.Compute("B");
        private IWitnessCollector _witnessCollector;
        private WitnessRpcModule _witnessRpcModule;

        [SetUp]
        public void Setup()
        {
            _witnessCollector = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            _witnessRpcModule = new WitnessRpcModule(_witnessCollector);
        }

        [Test]
        public void GetTwoWitnessHash()
        {
            _witnessCollector.Add(_keccakA);
            _witnessCollector.Add(_keccakB);
            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "5");
            serialized.Should().Be(TwoNodesResponse);
        }

        [Test]
        public void GetOneWitnessHash()
        {
            _witnessCollector.Add(_keccakA);
            _witnessCollector.Add(_keccakB);
            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "1");
            serialized.Should().Be(OneNodeResponse);
        }

        [Test]
        public void GetError()
        {
            _witnessCollector.Add(_keccakA);
            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "n");
            serialized.Should().Be(ErrorResponse);
        }
    }
}
