// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Converters;
using NUnit.Framework;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Test.Data;

public class Base64FieldType
{
    [JsonConverter(typeof(Base64Converter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? Base64 { get; set; }
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
