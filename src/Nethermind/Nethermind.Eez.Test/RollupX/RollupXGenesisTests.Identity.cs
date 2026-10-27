// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private static IEnumerable<TestCaseData> PortableIdentityVectors()
    {
        foreach (ulong version in new ulong[] { 3, 4 })
            foreach (JsonElement vector in IdentityFixture(version).GetProperty("identities").EnumerateArray())
                yield return new TestCaseData(vector, version).SetName($"PortableIdentity_V{version}_Upstream_{vector.GetProperty("name").GetString()}");
    }

    private static IEnumerable<TestCaseData> CanonicalJsonVectors()
    {
        foreach (JsonElement vector in IdentityFixture().GetProperty("canonical_json").EnumerateArray())
            yield return new TestCaseData(vector).SetName($"CanonicalJson_Upstream_{vector.GetProperty("name").GetString()}");
    }

    private static IEnumerable<TestCaseData> InvalidCanonicalJsonVectors()
    {
        int index = 0;
        foreach (JsonElement vector in IdentityFixture().GetProperty("invalid_json").EnumerateArray())
            yield return new TestCaseData(vector.GetString()!).SetName($"CanonicalJson_UpstreamInvalid_{index++}");
    }

    [TestCaseSource(nameof(PortableIdentityVectors))]
    public void PortableIdentity_IndependentVector_MatchesPreimageAndHash(JsonElement vector, ulong version)
    {
        RollupXValidationIdentity.Inputs input = new(
            ulong.Parse(vector.GetProperty("rollup_id").GetString()!, CultureInfo.InvariantCulture),
            ulong.Parse(vector.GetProperty("chain_id").GetString()!, CultureInfo.InvariantCulture),
            new Address(vector.GetProperty("system_address").GetString()!),
            new Address(vector.GetProperty("eez_l2_address").GetString()!),
            new Hash256(vector.GetProperty("chain_document_digest").GetString()!),
            new Hash256(vector.GetProperty("offline_profile_hash").GetString()!),
            new Address(vector.GetProperty("registry").GetString()!),
            new Address(vector.GetProperty("manager").GetString()!), version);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RollupXValidationIdentity.Encode(in input), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("rlp").GetString()!)));
            Assert.That(RollupXValidationIdentity.Compute(in input), Is.EqualTo(new Hash256(vector.GetProperty("identity").GetString()!)));
        }
    }

    [TestCaseSource(nameof(CanonicalJsonVectors))]
    public void CanonicalJson_IndependentVector_MatchesBytesAndDigest(JsonElement vector)
    {
        byte[] input = Encoding.UTF8.GetBytes(vector.GetProperty("input").GetString()!);
        ReadOnlyMemory<byte> canonical = RollupXCanonicalJson.Encode(input);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(canonical.ToArray(), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("canonical_utf8_hex").GetString()!)));
            Assert.That(Keccak.Compute(canonical.Span), Is.EqualTo(new Hash256(vector.GetProperty("digest").GetString()!)));
        }
    }

    [TestCaseSource(nameof(InvalidCanonicalJsonVectors))]
    public void CanonicalJson_InvalidUpstreamInput_Rejects(string json) =>
        Assert.That(() => RollupXCanonicalJson.Encode(Encoding.UTF8.GetBytes(json)), Throws.InstanceOf<JsonException>());

    [Test]
    public void CanonicalJson_QuoteBackslashAndHexControl_UsesExactEscapes()
    {
        byte[] input = """{"z": false, "a\u0022\u005C\u001B": "\u0022\u005C/\u001B"}"""u8.ToArray();
        byte[] expected = """{"a\"\\\u001b":"\"\\/\u001b","z":false}"""u8.ToArray();
        Assert.That(RollupXCanonicalJson.Encode(input).ToArray(), Is.EqualTo(expected));
    }

    [TestCase(128, true, TestName = "CanonicalJson_128Containers_Accepts")]
    [TestCase(129, false, TestName = "CanonicalJson_129Containers_Rejects")]
    public void CanonicalJson_DepthBoundary_IsEnforced(int depth, bool valid)
    {
        byte[] input = Encoding.UTF8.GetBytes(new string('[', depth) + "0" + new string(']', depth));
        if (valid) Assert.That(RollupXCanonicalJson.Encode(input).ToArray(), Is.EqualTo(input));
        else Assert.That(() => RollupXCanonicalJson.Encode(input), Throws.InstanceOf<JsonException>());
    }

    [TestCase("\"\\uDC00\"", TestName = "CanonicalJson_UnpairedLowSurrogate_Rejects")]
    [TestCase("\"\\uD800x\"", TestName = "CanonicalJson_SurrogateFollowedByText_Rejects")]
    [TestCase("\"\\uD800\\u0041\"", TestName = "CanonicalJson_SurrogateFollowedByOrdinaryEscape_Rejects")]
    [TestCase("{\"\\ud83d\\ude00\":1,\"\U0001f600\":2}", TestName = "CanonicalJson_UnicodeKeyAlias_RejectsDuplicate")]
    public void CanonicalJson_InvalidEscapedUnicode_Rejects(string input) =>
        Assert.That(() => RollupXCanonicalJson.Encode(Encoding.UTF8.GetBytes(input)), Throws.InstanceOf<JsonException>());

    private static IEnumerable<TestCaseData> InvalidUtf8Vectors()
    {
        yield return new TestCaseData(new byte[] { 0x22, 0xc0, 0x80, 0x22 }).SetName("CanonicalJson_OverlongUtf8_Rejects");
        yield return new TestCaseData(new byte[] { 0x22, 0xed, 0xa0, 0x80, 0x22 }).SetName("CanonicalJson_Utf8Surrogate_Rejects");
        yield return new TestCaseData(new byte[] { 0x22, 0xf0, 0x90, 0x80, 0x22 }).SetName("CanonicalJson_TruncatedUtf8_Rejects");
    }

    [TestCaseSource(nameof(InvalidUtf8Vectors))]
    public void CanonicalJson_InvalidUtf8_Rejects(byte[] input) =>
        Assert.That(() => RollupXCanonicalJson.Encode(input), Throws.InstanceOf<JsonException>());

    private static JsonElement IdentityFixture(ulong version = 3) => StatelessFixtures.ReadJson($"rollupx/native-profile-v{version}", "validation-identity.json");
}
