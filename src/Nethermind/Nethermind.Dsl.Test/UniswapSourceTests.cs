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

using System;
using System.Numerics;
using Nethermind.Api;
using Nethermind.Core.Extensions;
using Nethermind.Dsl.ANTLR;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class UniswapSourceTests
    {
        private Interpreter _interpreter;
        private INethermindApi _api;

        [SetUp]
        public void SetUp()
        {
            _interpreter = null;
            _api = Substitute.For<INethermindApi>();
            _api.EthereumJsonSerializer.Returns(new EthereumJsonSerializer());
        }

        [Test]
        [TestCase("WATCH UNISWAP PUBLISH WEBSOCKETS")]
        public void will_create_interpreter_without_exceptions(string script)
        {
            _interpreter = new Interpreter(_api, script);
        }

        [Test]
        public void test()
        {
            // Int256.Int256 integer = Int256.Int256.Max;
            // var serializer = new EthereumJsonSerializer();
            // serializer.Serialize(integer);

            UInt256 x = 100;
            UInt256 y = 150;

        }
    }
}