// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

/// <summary>
/// The state test reader takes a transaction template value wider than 256 bits, with or without the retesteth
/// <c>0x:bigint </c> prefix, as <see cref="UInt256.MaxValue"/>, while the transaction itself stays undecodable.
/// </summary>
public class TransactionValueReaderTests
{
    private const string Prefix = "0x:bigint ";
    private const string StateTestDirectory = "stTransactionTest";

    private static readonly string Max = "0x" + new string('f', 64);
    private static readonly string BelowMax = "0x" + new string('f', 63) + "e";
    private static readonly string TwoTo256 = "0x1" + new string('0', 64);
    private static readonly string TwoTo256PlusOne = "0x1" + new string('0', 63) + "1";

    private static IEnumerable<TestCaseData> ReadableValues()
    {
        foreach (string prefix in new[] { "", Prefix })
        {
            string form = prefix.Length == 0 ? "plain" : "prefixed";
            yield return Case($"\"{prefix}0x01\"", UInt256.One, $"{form} 1");
            yield return Case($"\"{prefix}0x0\"", UInt256.Zero, $"{form} 0");
            yield return Case($"\"{prefix}0x{new string('0', 70)}1\"", UInt256.One, $"{form} 1 with leading zeros past 64 digits");
            yield return Case($"\"{prefix}{BelowMax}\"", UInt256.MaxValue - 1, $"{form} 2^256-2");
            yield return Case($"\"{prefix}{Max}\"", UInt256.MaxValue, $"{form} 2^256-1");
            yield return Case($"\"{prefix}{TwoTo256}\"", UInt256.MaxValue, $"{form} 2^256");
            yield return Case($"\"{prefix}{TwoTo256PlusOne}\"", UInt256.MaxValue, $"{form} 2^256+1");
            yield return Case($"\"{prefix}0x{new string('F', 100)}\"", UInt256.MaxValue, $"{form} 400 bits");
        }

        yield return Case("1", UInt256.One, "number token");

        static TestCaseData Case(string token, UInt256 expected, string name) => new TestCaseData(token, expected).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(ReadableValues))]
    public void Reads_a_value_exactly_up_to_256_bits_and_saturates_a_wider_one(string token, UInt256 expected) =>
        Assert.That(ReadValues($"[{token}]"), Is.EqualTo(new[] { expected }));

    [Test]
    public void Reads_every_value_of_the_list_in_order() =>
        Assert.That(ReadValues($"[\"0x01\", \"{Prefix}{TwoTo256PlusOne}\", \"{BelowMax}\", \"{Prefix}0x02\"]"),
            Is.EqualTo(new[] { UInt256.One, UInt256.MaxValue, UInt256.MaxValue - 1, (UInt256)2 }));

    [TestCase("[]")]
    [TestCase("null")]
    public void Reads_an_empty_or_missing_list(string json) =>
        Assert.That(ReadValues(json), json == "null" ? Is.Null : Is.Empty);

    [TestCase("\"0x\"", TestName = "{m}(bare 0x)")]
    [TestCase("\"0x:bigint 0x\"", TestName = "{m}(prefixed bare 0x)")]
    [TestCase("\"0x:bigint \"", TestName = "{m}(prefix alone)")]
    [TestCase("\"0xzz\"", TestName = "{m}(non-hex)")]
    [TestCase("\"0x:bigint 0xzzzz\"", TestName = "{m}(prefixed non-hex)")]
    [TestCase("\"0x:bigint 0xggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"", TestName = "{m}(prefixed long non-hex)")]
    [TestCase("\"0x:bigint0x01\"", TestName = "{m}(prefix without its space)")]
    [TestCase("\"0x:bigint 0x:bigint 0x01\"", TestName = "{m}(prefix twice)")]
    [TestCase("\"value\"", TestName = "{m}(text)")]
    [TestCase("true", TestName = "{m}(boolean)")]
    [TestCase("null", TestName = "{m}(null)")]
    [TestCase("{}", TestName = "{m}(object)")]
    [TestCase("[\"0x01\"]", TestName = "{m}(nested list)")]
    public void Rejects_a_malformed_value(string token) =>
        Assert.That(() => ReadValues($"[{token}]"), Throws.InstanceOf<Exception>());

    [TestCase("\"0x01\"")]
    [TestCase("1")]
    [TestCase("{}")]
    public void Rejects_a_value_that_is_not_a_list(string json) =>
        Assert.That(() => ReadValues(json), Throws.InstanceOf<JsonException>());

    [Test]
    public void Rejects_an_unterminated_list() =>
        Assert.That(() => ReadUnterminatedList(), Throws.InstanceOf<JsonException>().With.Message.Contains("not terminated"));

    [Test]
    public void Production_reader_still_rejects_an_over_wide_or_prefixed_value()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => JsonSerializer.Deserialize<UInt256[]>($"[\"{TwoTo256}\"]", EthereumJsonSerializer.JsonOptions), Throws.InstanceOf<JsonException>());
            Assert.That(() => JsonSerializer.Deserialize<UInt256[]>($"[\"{Prefix}0x01\"]", EthereumJsonSerializer.JsonOptions), Throws.InstanceOf<JsonException>());
        }
    }

    /// <summary>
    /// The ValueOverflow fixtures expect the transaction to be rejected for its over-wide value. The state test base
    /// judges only the post-state, so the rejection has to come from the raw transaction failing to decode, after which
    /// the saturated template stands in for it.
    /// </summary>
    [TestCase("ValueOverflow", 10)]
    [TestCase("ValueOverflowParis", 13)]
    public void ValueOverflow_fixture_loads_while_its_raw_transactions_stay_undecodable(string name, int forks)
    {
        GeneralStateTest[] cases = [.. new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), StateTestDirectory, $"{name}.json")
            .LoadTests<GeneralStateTest>()];
        string file = Path.Combine(LegacyStateTestsDirectory(), StateTestDirectory, $"{name}.json");
        List<(string Fork, string Exception, byte[] TxBytes)> posts = Posts(file, name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases, Has.Length.EqualTo(forks), "one case per fork");
            Assert.That(cases.Select(static t => t.LoadFailure), Is.All.Null);
            Assert.That(cases.Select(static t => t.Transaction?.Value), Is.All.EqualTo(UInt256.MaxValue), "the template stands in for the transaction");
            Assert.That(posts, Has.Count.EqualTo(forks));
            foreach ((string fork, string exception, byte[] txBytes) in posts)
            {
                Assert.That(exception, Is.EqualTo("TR_RLP_WRONGVALUE"), fork);
                Assert.That(() => Rlp.Decode<Transaction>(txBytes, RlpBehaviors.SkipTypedWrapping), Throws.InstanceOf<RlpException>(), fork);
            }
        }
    }

    private static UInt256[] ReadValues(string valueJson) =>
        JsonSerializer.Deserialize<TransactionJson>($"{{\"value\": {valueJson}}}", EthereumJsonSerializer.JsonOptions).Value;

    private static void ReadUnterminatedList()
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes("[\"0x01\", \"0x02\""), isFinalBlock: false, state: default);
        reader.Read();
        new SaturatingUInt256ArrayConverter().Read(ref reader, typeof(UInt256[]), EthereumJsonSerializer.JsonOptions);
    }

    private static List<(string Fork, string Exception, byte[] TxBytes)> Posts(string file, string name)
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllBytes(file));
        List<(string, string, byte[])> posts = [];
        foreach (JsonProperty fork in fixture.RootElement.GetProperty(name).GetProperty("post").EnumerateObject())
        {
            foreach (JsonElement post in fork.Value.EnumerateArray())
            {
                posts.Add((fork.Name, post.GetProperty("expectException").GetString(), Bytes.FromHexString(post.GetProperty("txbytes").GetString())));
            }
        }

        return posts;
    }

    private static string LegacyStateTestsDirectory()
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDirectory[..baseDirectory.LastIndexOf("src", StringComparison.Ordinal)], "src", "tests", "LegacyTests", "Cancun", "GeneralStateTests");
    }
}
