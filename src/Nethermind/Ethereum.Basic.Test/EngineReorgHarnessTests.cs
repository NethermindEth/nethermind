// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

/// <summary>
/// Runs a <c>blockchain_test_engine_reorg</c> fixture through <see cref="BlockchainTestBase.RunEngineReorgTest"/>
/// as it is, and with one expectation changed so that the harness must fail at the named step.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class EngineReorgHarnessTests : BlockchainTestBase
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "parent_hash_equals_block_hash.json");

    [Test]
    public async Task Unchanged_fixture_passes() =>
        Assert.That((await RunEngineReorgTest(Load(await ReadFixture()))).Pass, Is.True);

    [TestCase("5", "latest", "a1", "step 5 (assertHead): latest block", TestName = "{m}(assertHead)")]
    [TestCase("1/branches/applied/0", "latest", "genesis", "step applied/0 (assertHead): latest block", TestName = "{m}(assertHead in a branch)")]
    [TestCase("4/expect/0", "status", "VALID", "step 4 (newPayload): the response", TestName = "{m}(newPayload outcome)")]
    public async Task Changed_expectation_fails_at_its_step(string stepPath, string property, string value, string message)
    {
        JsonNode fixture = await ReadFixture();
        JsonNode node = fixture.AsObject().Single().Value!["steps"]!;
        foreach (string segment in stepPath.Split('/'))
            node = int.TryParse(segment, out int index) ? node[index]! : node[segment]!;
        node[property] = value;

        AssertionException exception = await Assert.CatchAsync<AssertionException>(() => RunEngineReorgTest(Load(fixture)));

        Assert.That(exception.Message, Does.Contain(message));
    }

    private static async Task<JsonNode> ReadFixture() => JsonNode.Parse(await File.ReadAllTextAsync(Fixture))!;

    private static EngineReorgTest Load(JsonNode fixture) =>
        (EngineReorgTest)JsonToEthereumTest.ConvertToEngineReorgTests(Encoding.UTF8.GetBytes(fixture.ToJsonString())).Single();
}
