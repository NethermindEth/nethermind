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

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.Rpc
{
    public class RpcDbTests
    {
        private IJsonSerializer _jsonSerializer;
        private IJsonRpcClient _jsonRpcClient;
        private IDb _recordDb;
        private RpcDb _rpcDb;

        [SetUp]
        public void SetUp()
        {
            _jsonSerializer = Substitute.For<IJsonSerializer>();
            _jsonRpcClient = Substitute.For<IJsonRpcClient>();
            _recordDb = Substitute.For<IDb>();
            _rpcDb = new RpcDb("Name", _jsonSerializer, _jsonRpcClient, LimboLogs.Instance, _recordDb);
        }

        [Test]
        public void innermost_is_self()
        {
            _rpcDb.Innermost.Should().Be(_rpcDb);
        }

        [Test]
        public void gets_through_rpc()
        {
            var result = "0x0123";
            _jsonSerializer.Deserialize<JsonRpcSuccessResponse>(Arg.Any<string>()).Returns(new JsonRpcSuccessResponse() {Result = result});
            var key = new byte[1];
            var elem = _rpcDb[key];
            _jsonRpcClient.Received().Post("debug_getFromDb", "Name", key.ToHexString());
            _recordDb.Received()[key] = Arg.Is<byte[]>(a => a.SequenceEqual(Bytes.FromHexString(result)));
        }
    }
}
