// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class Hash256ArrayConverterTests
{
    private const string HashA = "0x0123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210";
    private const string HashB = "0xfedcba9876543210012345678abcdef00fedcba9876543210012345678abcdef";

    private static readonly JsonSerializerOptions s_options = new() { Converters = { new Hash256ArrayConverter() } };

    public static IEnumerable<TestCaseData> RoundtripCases()
    {
        yield return new TestCaseData("null", null).SetName("Null");
        yield return new TestCaseData("[]", System.Array.Empty<Hash256?>()).SetName("EmptyArray");
        yield return new TestCaseData($"[\"{HashA}\"]", new Hash256?[] { new(HashA) }).SetName("SingleHash");
        yield return new TestCaseData($"[\"{HashA}\",\"{HashB}\"]", new Hash256?[] { new(HashA), new(HashB) }).SetName("MultipleHashes");
        yield return new TestCaseData($"[null,\"{HashA}\"]", new Hash256?[] { null, new(HashA) }).SetName("LeadingNullElement");
        yield return new TestCaseData($"[\"{HashA}\",null]", new Hash256?[] { new(HashA), null }).SetName("TrailingNullElement");
    }

    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip(string json, Hash256?[]? expected)
    {
        Hash256?[]? hashes = JsonSerializer.Deserialize<Hash256?[]>(json, s_options);

        if (expected is null)
        {
            Assert.That(hashes, Is.Null);
        }
        else
        {
            Assert.That(hashes, Is.Not.Null);
            Assert.That(hashes!.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] is null)
                {
                    Assert.That(hashes[i], Is.Null);
                }
                else
                {
                    Assert.That(hashes[i], Is.EqualTo(expected[i]!));
                }
            }
        }

        Assert.That(JsonSerializer.Serialize(hashes, s_options), Is.EqualTo(json));
    }

    [TestCase("[\"0xabcd\"]", TestName = "TooShort")]
    [TestCase("[\"0x" + "00" + "0123456789abcdeffedcba9876543210" + "0123456789abcdeffedcba98765432" + "ff\"]", TestName = "TooLong")]
    public void Read_WrongLengthElement_Throws(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<Hash256?[]>(json, s_options), Throws.TypeOf<JsonException>());

    [TestCase("[\"0x123456789abcdeffedcba98765432100123456789abcdeffedcba9876543210\"]", TestName = "OddLengthElement")]
    public void Read_OddLengthElement_Throws(string json) =>
        Assert.That(() => JsonSerializer.Deserialize<Hash256?[]>(json, s_options), Throws.InstanceOf<System.FormatException>());
}
