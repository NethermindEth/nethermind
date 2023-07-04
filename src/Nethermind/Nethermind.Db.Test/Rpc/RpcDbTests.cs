// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    [Parallelizable(ParallelScope.All)]
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
            _recordDb = new MemDb();
            _rpcDb = new RpcDb("Name", _jsonSerializer, _jsonRpcClient, LimboLogs.Instance, _recordDb);
        }

        [Test]
        public void gets_through_rpc()
        {
            string result = "0x0123";
            _jsonSerializer.Deserialize<JsonRpcSuccessResponse>(Arg.Any<string>()).Returns(new JsonRpcSuccessResponse() { Result = result });
            byte[] key = new byte[1];
            byte[] elem = _rpcDb[key];
            _jsonRpcClient.Received().Post("debug_getFromDb", "Name", key.ToHexString());
            _recordDb[key].Should().BeEquivalentTo(Bytes.FromHexString(result));
        }
    }
}
