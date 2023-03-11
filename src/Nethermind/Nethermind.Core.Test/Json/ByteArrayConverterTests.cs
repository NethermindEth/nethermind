// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class ByteArrayConverterTests : ConverterTestBase<byte[]>
    {
        [TestCase(null)]
        [TestCase(new byte[0])]
        [TestCase(new byte[] { 1 })]
        public void Test_roundtrip(byte[] bytes)
        {
            TestConverter(bytes, (before, after) => Bytes.AreEqual(before, after), new ByteArrayConverter());
        }

        [Test]
        public void Direct_null()
        {
            ByteArrayConverter converter = new();
            StringBuilder sb = new();
            JsonSerializer serializer = new();
            serializer.Converters.Add(converter);
            converter.WriteJson(
                new JsonTextWriter(new StringWriter(sb)), null, serializer);
            sb.ToString().Should().Be("null");
        }
    }
}
