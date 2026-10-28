// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Data;

/// <summary>An omission claim's index is a canonical <c>QUANTITY</c> (execution-apis common.md) however lenient the
/// serializer's own numeric parsing is.</summary>
public class CanonicalQuantityConverterTests
{
    private static readonly EthereumJsonSerializer Serializer = new();

    [TestCase("\"0x0\"", 0UL)]
    [TestCase("\"0x1\"", 1UL)]
    [TestCase("\"0x1f\"", 31UL)]
    [TestCase("\"0xAbC\"", 2748UL)]
    [TestCase("\"0xffffffffffffffff\"", ulong.MaxValue)]
    public void Reads_a_canonical_quantity(string index, ulong expected)
    {
        OmissionClaim claim = Serializer.Deserialize<OmissionClaim>(Claim(index));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claim.EvaluationIndex, Is.EqualTo(expected));
            Assert.That(Serializer.Serialize(claim), Does.Contain($"\"evaluationIndex\":{index.ToLowerInvariant()}"));
        }
    }

    [TestCase("\"0x01\"", TestName = "Leading zero")]
    [TestCase("\"0x00\"", TestName = "Zero with a leading zero")]
    [TestCase("\"0x\"", TestName = "No digits")]
    [TestCase("\"1\"", TestName = "Decimal without 0x")]
    [TestCase("\"x1\"", TestName = "Prefix without 0")]
    [TestCase("\"0X1\"", TestName = "Uppercase prefix")]
    [TestCase("1", TestName = "JSON number")]
    [TestCase("\"0x10000000000000000\"", TestName = "Over 64 bits")]
    [TestCase("\"0xg\"", TestName = "Not hex")]
    [TestCase("\" 0x1\"", TestName = "Leading whitespace")]
    public void Rejects_a_noncanonical_quantity(string index) =>
        Assert.Throws<JsonException>(() => Serializer.Deserialize<OmissionClaim>(Claim(index)));

    private static string Claim(string index) => $"{{\"transactionHash\":\"{TestItem.KeccakA}\",\"evaluationIndex\":{index}}}";
}
