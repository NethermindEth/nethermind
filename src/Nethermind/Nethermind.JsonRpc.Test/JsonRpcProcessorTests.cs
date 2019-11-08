/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcProcessorTests
    {
        [SetUp]
        public void Initialize()
        {
            _jsonRpcProcessor = new JsonRpcProcessor(Substitute.For<IJsonRpcService>(), new EthereumJsonSerializer(), LimboLogs.Instance);
        }

        private JsonRpcProcessor _jsonRpcProcessor;
        
        [Test]
        public void Can_process_guid_ids()
        {
            _jsonRpcProcessor.ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
        }
    }
}