// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class Hash256ConverterTests
    {
        static readonly Hash256Converter converter = new();
        static readonly JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [Test]
        public void Can_read_null()
        {
            Hash256? result = JsonSerializer.Deserialize<Hash256>("null", options);
            Assert.That(result, Is.EqualTo(null));
        }

        [Test]
        public void Writes_zero_hash()
        {
            Hash256 hash = new(new byte[32]);
            string result = JsonSerializer.Serialize(hash, options);
            Assert.That(result, Is.EqualTo("\"0x0000000000000000000000000000000000000000000000000000000000000000\""));
        }

        [Test]
        public void Writes_all_ones_hash()
        {
            byte[] bytes = new byte[32];
            Array.Fill(bytes, (byte)0xFF);
            Hash256 hash = new(bytes);
            string result = JsonSerializer.Serialize(hash, options);
            Assert.That(result, Is.EqualTo("\"0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\""));
        }

        [Test]
        public void Writes_known_hash()
        {
            // Keccak256 of empty string
            Hash256 hash = Keccak.OfAnEmptyString;
            string result = JsonSerializer.Serialize(hash, options);
            Assert.That(result, Is.EqualTo($"\"0x{hash.ToString(false)}\""));
        }

        [Test]
        public void Writes_sequential_bytes()
        {
            byte[] bytes = new byte[32];
            for (int i = 0; i < 32; i++) bytes[i] = (byte)i;
            Hash256 hash = new(bytes);
            string result = JsonSerializer.Serialize(hash, options);
            Assert.That(result, Is.EqualTo("\"0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f\""));
        }

        [Test]
        public void Writes_roundtrip()
        {
            Hash256 hash = Keccak.Compute("test data"u8);
            string json = JsonSerializer.Serialize(hash, options);
            Hash256? deserialized = JsonSerializer.Deserialize<Hash256>(json, options);
            Assert.That(deserialized, Is.EqualTo(hash));
        }

        [Test]
        public void Writes_each_nibble_value()
        {
            // Ensure all hex chars 0-f appear correctly
            byte[] bytes = new byte[32];
            for (int i = 0; i < 16; i++)
            {
                bytes[i * 2] = (byte)((i << 4) | i);     // 0x00, 0x11, 0x22, ..., 0xff
                bytes[i * 2 + 1] = (byte)((i << 4) | (15 - i)); // 0x0f, 0x1e, 0x2d, ...
            }
            Hash256 hash = new(bytes);
            string result = JsonSerializer.Serialize(hash, options);
            Hash256? roundtrip = JsonSerializer.Deserialize<Hash256>(result, options);
            Assert.That(roundtrip, Is.EqualTo(hash));
        }
    }
}
