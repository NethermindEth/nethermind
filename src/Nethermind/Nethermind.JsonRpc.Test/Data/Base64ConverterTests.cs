// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using NUnit.Framework;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Test.Data;

internal class Base64FieldType
{
    [JsonConverter(typeof(Base64Converter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required byte[] Base64 { get; set; }
}

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class Base64ConverterTests : SerializationTestBase
{
    [Test]
    public void Can_do_roundtrip()
    {
        TestRoundtrip<Base64FieldType>(@"{""base64"":""MC4xOC4wLWRldgAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=""}");
        TestRoundtrip<Base64FieldType>(@"{""base64"":null}");
    }
}
