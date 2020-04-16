//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Serialization.Json.Abi;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Abi.Test.Json
{
    public class AbiParameterConverterTests
    {
        public static IEnumerable TypeTestCases
        {
            get
            {
                yield return new TestCaseData("int", AbiType.Int256, null);
                yield return new TestCaseData("UINT", AbiType.UInt256, null);
                yield return new TestCaseData("int8", new AbiInt(8), null);
                yield return new TestCaseData("uint16", new AbiUInt(16), null);
                yield return new TestCaseData("uint32", new AbiUInt(32), null);
                yield return new TestCaseData("iNt64", new AbiInt(64), null);
                yield return new TestCaseData("int128", new AbiInt(128), null);
                yield return new TestCaseData("uint256", new AbiUInt(256), null);
                yield return new TestCaseData("address", AbiType.Address, null);
                yield return new TestCaseData("bool", AbiType.Bool, null);
                yield return new TestCaseData("fixed", AbiType.Fixed, null);
                yield return new TestCaseData("Ufixed", AbiType.UFixed, null);
                yield return new TestCaseData("fixed16x10", new AbiFixed(16, 10), null);
                yield return new TestCaseData("ufixed256x80", new AbiUFixed(256, 80), null);
                yield return new TestCaseData("fixed96x1", new AbiFixed(96, 1), null);
                yield return new TestCaseData("bytes", AbiType.DynamicBytes, null);
                yield return new TestCaseData("bytes32", new AbiBytes(32), null);
                yield return new TestCaseData("function", AbiType.Function, null);
                yield return new TestCaseData("string", AbiType.String, null);
                yield return new TestCaseData("int[]", new AbiArray(AbiType.Int256), null);
                yield return new TestCaseData("string[5]", new AbiFixedLengthArray(AbiType.String, 5), null);
                
                yield return new TestCaseData("tuple", null, new NotSupportedException());
                
                yield return new TestCaseData("int1", null, new ArgumentException());
                yield return new TestCaseData("int9", null, new ArgumentException());
                yield return new TestCaseData("int300", null, new ArgumentException());
                yield return new TestCaseData("int3000", null, new ArgumentException());
                yield return new TestCaseData("fixed80", null, new ArgumentException());
                yield return new TestCaseData("fixed80x81", null, new ArgumentException());
                yield return new TestCaseData("bytes33", null, new ArgumentException());
            }
        }

        [TestCaseSource(nameof(TypeTestCases))]
        public void Can_read_json(string type, AbiType expectedType, Exception expectedException = null)
        {
            var converter = new AbiParameterConverter();
            var model = new {name = "theName", type};
            string json = JsonConvert.SerializeObject(model);
            using (var jsonReader = new JsonTextReader(new StringReader(json)))
            {
                try
                {
                    var result = converter.ReadJson(jsonReader, typeof(AbiParameter), null, false, new JsonSerializer());
                    var expectation = new AbiParameter() {Name = "theName", Type = expectedType};
                    expectedException.Should().BeNull();
                    result.Should().BeEquivalentTo(expectation);
                }
                catch (Exception e)
                {
                    if (e.GetType() != expectedException?.GetType())
                    {
                        throw;
                    }
                }
            }
        }
    }
}