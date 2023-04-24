// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using FluentAssertions;
using Nethermind.Blockchain.Contracts.Json;

using NUnit.Framework;

namespace Nethermind.Abi.Test.Json
{
    public class AbiParameterConverterTests
    {
        public static IEnumerable TypeTestCases
        {
            get
            {
                object[] GetTestData(string type, AbiType abiType, params object[] components) =>
                    new object[] { type, abiType, null, components };

                object[] GetTestDataWithException(string type, Exception exception, object[] components = null) =>
                    new object[] { type, null, exception, components };

                yield return new TestCaseData(GetTestData("int", AbiType.Int256));
                yield return new TestCaseData(GetTestData("UINT", AbiType.UInt256));
                yield return new TestCaseData(GetTestData("int8", new AbiInt(8)));
                yield return new TestCaseData(GetTestData("uint16", new AbiUInt(16)));
                yield return new TestCaseData(GetTestData("uint32", new AbiUInt(32)));
                yield return new TestCaseData(GetTestData("iNt64", new AbiInt(64)));
                yield return new TestCaseData(GetTestData("int128", new AbiInt(128)));
                yield return new TestCaseData(GetTestData("uint256", new AbiUInt(256)));
                yield return new TestCaseData(GetTestData("address", AbiType.Address));
                yield return new TestCaseData(GetTestData("bool", AbiType.Bool));
                yield return new TestCaseData(GetTestData("fixed", AbiType.Fixed));
                yield return new TestCaseData(GetTestData("Ufixed", AbiType.UFixed));
                yield return new TestCaseData(GetTestData("fixed16x10", new AbiFixed(16, 10)));
                yield return new TestCaseData(GetTestData("ufixed256x80", new AbiUFixed(256, 80)));
                yield return new TestCaseData(GetTestData("fixed96x1", new AbiFixed(96, 1)));
                yield return new TestCaseData(GetTestData("bytes", AbiType.DynamicBytes));
                yield return new TestCaseData(GetTestData("bytes32", new AbiBytes(32)));
                yield return new TestCaseData(GetTestData("function", AbiType.Function));
                yield return new TestCaseData(GetTestData("string", AbiType.String));
                yield return new TestCaseData(GetTestData("int[]", new AbiArray(AbiType.Int256)));
                yield return new TestCaseData(GetTestData("string[5]", new AbiFixedLengthArray(AbiType.String, 5)));

                yield return new TestCaseData(GetTestData("tuple", new AbiTuple(Array.Empty<AbiType>())));
                yield return new TestCaseData(GetTestData("tuple",
                    new AbiTuple(new AbiType[] { AbiType.Int256 }),
                    new { name = "property", type = "int" }));

                yield return new TestCaseData(GetTestData("tuple", new AbiTuple<CustomAbiType>(),
                    new { name = "c", type = "int32" }));
                yield return new TestCaseData(GetTestDataWithException("int1", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("int9", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("int300", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("int3000", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("fixed80", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("fixed80x81", new ArgumentException()));
                yield return new TestCaseData(GetTestDataWithException("bytes33", new ArgumentException()));
            }
        }

        [TestCaseSource(nameof(TypeTestCases))]
        public void Can_read_json(string type, AbiType expectedType, Exception expectedException, object[] components)
        {
            AbiParameterConverter.RegisterFactory(new AbiTypeFactory(new AbiTuple<CustomAbiType>()));

            var converter = new AbiParameterConverter();
            var model = new { name = "theName", type, components };
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model));
            Utf8JsonReader jsonReader = new Utf8JsonReader(json);
            try
            {
                var result = converter.Read(ref jsonReader, typeof(AbiParameter), null);
                var expectation = new AbiParameter() { Name = "theName", Type = expectedType };
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
        public struct CustomAbiType
        {
            public int C { get; set; }
        }
    }
}
