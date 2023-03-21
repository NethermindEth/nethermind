// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text.Json;

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class KeccakConverterTests
    {
        static KeccakConverter converter = new();
        static JsonSerializerOptions options = new JsonSerializerOptions { Converters = { converter } };

        [Test]
        public void Can_read_null()
        {
            Keccak? result = JsonSerializer.Deserialize<Keccak>(string.Empty, options);
            Assert.AreEqual(null, result);
        }
    }
}
