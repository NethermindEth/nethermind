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

using Nethermind.Api;
using Nethermind.Dsl.ANTLR;
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
        public void test(string script)
        {
            _interpreter = new Interpreter(_api, script);
        }
    }
}