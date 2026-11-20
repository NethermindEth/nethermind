// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class EngineXFixturesTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, EngineXFixtures.PreAllocDirectory));
        Directory.CreateDirectory(Path.Combine(_root, "for_paris", "module"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void Index_groups_tests_across_files_by_pre_allocation_in_file_order()
    {
        WritePreAlloc("0x01");
        WritePreAlloc("0x02");
        string first = WriteFixture("a.json", ("a1", "0x01"), ("a2", "0x02"));
        string second = WriteFixture("b.json", ("b1", "0x01"));

        (List<EngineXGroup> groups, List<FailedToLoadTest> failures) = EngineXFixtures.Index([first, second]);

        // Each byte range holds exactly one test property, so it parses back to that test alone.
        string[] names = [.. groups.SelectMany(static group => group.Tests).SelectMany(EngineXFixtures.LoadTests).Select(static test => test.Name)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Empty);
            Assert.That(groups.Select(static group => group.PreHash), Is.EqualTo(new[] { "0x01", "0x02" }));
            Assert.That(groups[0].Tests.Select(static test => test.File), Is.EqualTo(new[] { first, second }));
            Assert.That(groups[0].PreAllocFile, Is.EqualTo(Path.Combine(_root, EngineXFixtures.PreAllocDirectory, "0x01.json")));
            Assert.That(names, Is.EquivalentTo(new[] { "a1", "a2", "b1" }));
        }
    }

    [Test]
    public void Index_fails_a_file_whose_pre_allocation_group_is_missing()
    {
        WritePreAlloc("0x01");
        string good = WriteFixture("a.json", ("a1", "0x01"));
        string missing = WriteFixture("b.json", ("b1", "0x03"));

        (List<EngineXGroup> groups, List<FailedToLoadTest> failures) = EngineXFixtures.Index([good, missing]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups.Select(static group => group.PreHash), Is.EqualTo(new[] { "0x01" }));
            Assert.That(failures.Select(static failure => failure.Name), Is.EqualTo(new[] { missing }));
        }
    }

    private void WritePreAlloc(string preHash) =>
        File.WriteAllText(Path.Combine(_root, EngineXFixtures.PreAllocDirectory, preHash + ".json"), "{}");

    private string WriteFixture(string fileName, params (string Name, string PreHash)[] tests)
    {
        string path = Path.Combine(_root, "for_paris", "module", fileName);
        IEnumerable<string> properties = tests.Select(static test =>
            $$"""
              "{{test.Name}}": {
                "network": "Paris",
                "preHash": "{{test.PreHash}}",
                "engineNewPayloads": [],
                "lastblockhash": "0x0000000000000000000000000000000000000000000000000000000000000000",
                "postStateDiff": { "0x0000000000000000000000000000000000000001": null }
              }
            """);
        File.WriteAllText(path, "{" + string.Join(",", properties) + "}");
        return path;
    }
}
