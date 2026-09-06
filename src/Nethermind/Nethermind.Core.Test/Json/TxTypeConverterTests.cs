// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Test.Sources;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class TxTypeConverterTests : ConverterTestBase<TxType>
    {
        [TestCaseSource(typeof(TxTypeSource), nameof(TxTypeSource.Any))]
        public void Test_roundtrip(TxType arg) => TestConverter(arg, static (before, after) => before.Equals(after), new TxTypeConverter());

        [TestCase("null")]
        [TestCase("1")]
        [TestCase("true")]
        [TestCase("{}")]
        [TestCase("[]")]
        public void Rejects_non_string_tokens(string json)
        {
            JsonSerializerOptions options = new();
            options.Converters.Add(new TxTypeConverter());

            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TxType>(json, options));
        }

        [TestCase(TxType.Legacy, "\"0x0\"")]
        [TestCase(TxType.AccessList, "\"0x1\"")]
        [TestCase(TxType.EIP1559, "\"0x2\"")]
        [TestCase(TxType.Blob, "\"0x3\"")]
        [TestCase(TxType.SetCode, "\"0x4\"")]
        [TestCase((TxType)16, "\"0x10\"")]
        [TestCase(TxType.DepositTx, "\"0x7e\"")]
        public void Serializes_as_hex_quantity(TxType type, string expectedJson) =>
            TestConverter(type, expectedJson, new TxTypeConverter(), static (before, after) => before.Equals(after));
    }
}
