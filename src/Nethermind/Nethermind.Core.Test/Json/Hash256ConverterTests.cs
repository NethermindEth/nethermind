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
    }
}
