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

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Newtonsoft.Json;

namespace Nethermind.LibSolc.Test
{
    [TestFixture]
    public class LibSolcTests
    {
        [Test]
        public void Get_License()
        {
            string result = Proxy.GetSolcLicense();
            Assert.NotNull(result);
            TestContext.WriteLine(result);
        }

        [Test]
        public void Get_Version()
        {
            string result = Proxy.GetSolcVersion();
            Assert.NotNull(result);
            TestContext.WriteLine(result);
        }

        [Test]
        public void Can_Compile()
        {
            string testContract = "pragma solidity ^0.4.22; contract test { function multiply(uint a) public returns(uint d) {   return a * 7;   } }";
            
            string result = Proxy.Compile(testContract, "byzantium", false, null);
//            TestContext.WriteLine(result);
            JObject compiledCode = JObject.Parse(result);
            Assert.NotNull(result);
            Assert.NotNull(compiledCode["contracts"]["test"]["test"]["evm"]["bytecode"]["object"]);
            TestContext.WriteLine(JToken.Parse(result).ToString(Formatting.Indented));
        }

    }
}