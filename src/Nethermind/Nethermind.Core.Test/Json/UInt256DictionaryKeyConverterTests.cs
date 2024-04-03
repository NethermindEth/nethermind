// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class UInt256DictionaryKeyConverterTests
{
    private static readonly JsonSerializerOptions Options = EthereumJsonSerializer.CreateOptions(true);

    [Test]
    public void WriteJson_Dictionary_SerializedCorrectly()
    {
        var dictionary = new Dictionary<UInt256, UInt256>
        {
            { new UInt256(1), new UInt256(12345) }
        };
        string serialised = JsonSerializer.Serialize(dictionary, Options);

        Assert.That(serialised, Is.EqualTo("{\n  \"0x1\": \"0x3039\"\n}"));
    }
}
