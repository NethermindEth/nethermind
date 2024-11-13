// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Text.Json;

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class NullableBigIntegerConverterTests : ConverterTestBase<BigInteger?>
    {
        static readonly NullableBigIntegerConverter converter = new NullableBigIntegerConverter();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };
        public void Test_roundtrip()
        {
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(int.MaxValue, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.One, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(BigInteger.Zero, (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }

        [Test]
        public void Can_read_0()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("0", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("0")));
        }

        [Test]
        public void Can_read_1()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("1", options);
            Assert.That(result, Is.EqualTo(BigInteger.Parse("1")));
        }

        [Test]
        public void Can_read_null()
        {
            BigInteger? result = JsonSerializer.Deserialize<BigInteger?>("null", options);
            Assert.That(result, Is.EqualTo(null));
        }
    }

    [TestFixture]
    public class NullableByteReadOnlyMemoryConverterTests : ConverterTestBase<ReadOnlyMemory<byte>?>
    {
        static readonly NullableByteReadOnlyMemoryConverter converter = new NullableByteReadOnlyMemoryConverter();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };
        public void Test_roundtrip()
        {
            TestConverter(null, (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(new ReadOnlyMemory<byte>(new byte[] { }), (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(new ReadOnlyMemory<byte>(new byte[] { 0 }), (integer, bigInteger) => integer.Equals(bigInteger), converter);
            TestConverter(new ReadOnlyMemory<byte>(new byte[] { 1 }), (integer, bigInteger) => integer.Equals(bigInteger), converter);
        }
                
        [Test]
        public void Can_read_null0()
        {
            string result = JsonSerializer.Serialize<ReadOnlyMemory<byte>?>(null, options);
            Assert.That(result, Is.EqualTo("null"));
        }

        [Test]
        public void Can_read_null()
        {
            string result = JsonSerializer.Serialize<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(new byte[] {  }), options);
            Assert.That(result, Is.EqualTo("\"0x\""));
        }

        [Test]
        public void Can_read_null2()
        {
            string result = JsonSerializer.Serialize<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(new byte[] { 0 }), options);
            Assert.That(result, Is.EqualTo("\"0x0\""));
        }

        [Test]
        public void Can_read_null3()
        {
            string result = JsonSerializer.Serialize<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(new byte[] { 1 }), options);
            Assert.That(result, Is.EqualTo("\"0x1\""));
        }

        [Test]
        public void Can_read_null4()
        {
            string result = JsonSerializer.Serialize<ReadOnlyMemory<byte>?>(new ReadOnlyMemory<byte>(new byte[] { 0, 1 }), options);
            Assert.That(result, Is.EqualTo("\"0x1\""));
        }
    }
}
