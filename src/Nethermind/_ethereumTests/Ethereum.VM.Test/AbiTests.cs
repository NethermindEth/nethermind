/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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
using Ethereum.Test.Base;
using Nethermind.Abi;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Ethereum.VM.Test
{
    internal class AbiTests
    {
        private static readonly Dictionary<string, AbiType> TypesByName = new Dictionary<string, AbiType>
        {
            {"uint256", AbiType.UInt256},
            {"uint32[]", new AbiArray(new AbiUInt(32))},
            {"bytes10", new AbiBytes(10)},
            {"bytes", AbiType.DynamicBytes},
            {"address", AbiType.Address}
        };

        private static AbiType ToAbiType(string typeName)
        {
            return TypesByName[typeName];
        }

        private static AbiTest Convert(string name, AbiTestJson testJson)
        {
            AbiTest test = new AbiTest();
            test.Name = name;
            test.Result = Bytes.FromHexString(testJson.Result);
            test.Types = testJson.Types.Select(ToAbiType).ToArray();
            test.Args = testJson.Args.Select(TestLoader.PrepareInput).ToArray();
            return test;
        }

        private static IEnumerable<AbiTest> LoadBasicAbiTests()
        {
            IEnumerable<AbiTest> tests = TestLoader.LoadFromFile<Dictionary<string, AbiTestJson>, AbiTest>(
                "basic_abi_tests.json",
                allTests => allTests.Select(namedTest => Convert(namedTest.Key, namedTest.Value)));
            return tests;
        }

        [TestCaseSource(nameof(LoadBasicAbiTests))]
        public void Test(AbiTest abiTest)
        {
            AbiEncoder encoder = new AbiEncoder();
            AbiSignature signature = new AbiSignature(abiTest.Name, abiTest.Types);
            byte[] encoded = encoder.Encode(AbiEncodingStyle.IncludeSignature, signature, abiTest.Args).Slice(4);
            Assert.True(Bytes.AreEqual(abiTest.Result, encoded));
        }

        public class AbiTestJson
        {
            public object[] Args { get; set; }
            public string Result { get; set; }
            public string[] Types { get; set; }
        }

        public class AbiTest
        {
            public string Name { get; set; }
            public object[] Args { get; set; }
            public byte[] Result { get; set; }
            public AbiType[] Types { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
