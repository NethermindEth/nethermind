// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class KeccakConverterTests
    {
        [Test]
        public void Can_read_null()
        {
            KeccakConverter converter = new();
            JsonReader reader = new JsonTextReader(new StringReader(string.Empty));
            reader.ReadAsString();
            Hash256 result = converter.ReadJson(reader, typeof(Hash256), null, false, JsonSerializer.CreateDefault());
            Assert.That(result, Is.EqualTo(null));
        }
    }
}
