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

using System;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class EnvConfigSourceTests
    {
        [Test]
        public void Works_fine_with_unset_values()
        {
            EnvConfigSource configSource = new EnvConfigSource();
            Assert.IsFalse(configSource.GetValue(typeof(int), "b", "a").IsSet);
        }
        
        [Test]
        public void Is_case_insensitive()
        {
            EnvConfigSource configSource = new EnvConfigSource();
            Environment.SetEnvironmentVariable("NETHERMIND_A_A", "12", EnvironmentVariableTarget.Process);
            Assert.IsTrue(configSource.GetValue(typeof(int), "a", "A").IsSet);
        }
        
        [TestCase(typeof(byte), "12", (byte)12)]
        [TestCase(typeof(int), "12", 12)]
        [TestCase(typeof(uint), "12", 12U)]
        [TestCase(typeof(long), "12", 12L)]
        [TestCase(typeof(ulong), "12", 12UL)]
        [TestCase(typeof(string), "12", "12")]
        [TestCase(typeof(bool), "false", false)]
        public void Can_parse_various_values(Type valueType, string valueString, object parsedValue)
        {
            Environment.SetEnvironmentVariable("NETHERMIND_A_A", valueString, EnvironmentVariableTarget.Process);
            EnvConfigSource configSource = new EnvConfigSource();
            Assert.AreEqual(parsedValue, configSource.GetValue(valueType, "a", "A").Value);
        }
    }
}
