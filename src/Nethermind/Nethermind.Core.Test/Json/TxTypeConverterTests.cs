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
    }
}
