// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Test;

/// <summary>
/// The ValueOverflow fixtures expect the transaction to be rejected for its value wider than 256 bits. The state test
/// base judges only the post-state, so the rejection has to come from the raw transaction failing to decode, after
/// which the saturated template stands in for it.
/// </summary>
/// <remarks>
/// Reads the copies of the fixtures in the output directory. Each test unsets <c>TEST_CHUNK</c> while it loads, since
/// it checks every fork of the file; the fixture is <see cref="NonParallelizableAttribute"/>, so no other test reads
/// the variable meanwhile.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class ValueOverflowFixtureTests
{
    private const string ChunkVariable = "TEST_CHUNK";
    private static readonly string FixtureDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stTransactionTest");

    [TestCase("ValueOverflow", 10)]
    [TestCase("ValueOverflowParis", 13)]
    public void Fixture_loads_while_its_raw_transactions_stay_undecodable(string name, int forks)
    {
        GeneralStateTest[] cases = LoadUnchunked($"{name}.json");
        List<(string Fork, string Exception, byte[] TxBytes)> posts = Posts(Path.Combine(FixtureDirectory, $"{name}.json"), name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cases, Has.Length.EqualTo(forks), "one case per fork");
            Assert.That(cases.Select(static t => t.LoadFailure), Is.All.Null);
            Assert.That(cases.Select(static t => t.Transaction?.Value), Is.All.EqualTo(UInt256.MaxValue), "the template stands in for the transaction");
            Assert.That(posts, Has.Count.EqualTo(forks), "one post per fork");
            foreach ((string fork, string exception, byte[] txBytes) in posts)
            {
                Assert.That(exception, Is.EqualTo("TR_RLP_WRONGVALUE"), fork);
                Assert.That(() => Rlp.Decode<Transaction>(txBytes, RlpBehaviors.SkipTypedWrapping), Throws.InstanceOf<RlpReaderException>(), fork);
            }
        }
    }

    private static GeneralStateTest[] LoadUnchunked(string file)
    {
        string chunk = Environment.GetEnvironmentVariable(ChunkVariable);
        Environment.SetEnvironmentVariable(ChunkVariable, null);
        try
        {
            return [.. new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), FixtureDirectory, file).LoadTests<GeneralStateTest>()];
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChunkVariable, chunk);
        }
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
}
