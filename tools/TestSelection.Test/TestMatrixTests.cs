// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.Commands;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Matrices;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class TestMatrixTests
{
    internal static JsonObject Matrix => JsonNode.Parse("""
        {
          "runner": ["ubuntu-latest", "ubuntu-24.04-arm"],
          "project": ["Nethermind.Evm.Test", "Nethermind.JsonRpc.Test"],
          "exclude": [{"runner": "ubuntu-latest", "project": "Nethermind.JsonRpc.Test"}],
          "include": [
            {"runner": "ubuntu-latest", "project": "Nethermind.JsonRpc.Test", "chunk": "1of2"},
            {"runner": "ubuntu-24.04-arm", "timeout": 60}
          ]
        }
        """)!.AsObject();

    [Test]
    public void Filter_KeepsOtherDimensions_AndDropsUnselectedProjectsEverywhere()
    {
        JsonObject? filtered = TestMatrix.Filter(Matrix, static project => project == "Nethermind.Evm.Test");
        JsonNode expected = JsonNode.Parse("""
            {
              "runner": ["ubuntu-latest", "ubuntu-24.04-arm"], "project": ["Nethermind.Evm.Test"], "exclude": [],
              "include": [{"runner": "ubuntu-24.04-arm", "timeout": 60}]
            }
            """)!;
        Assert.That(JsonNode.DeepEquals(filtered, expected), Is.True, $"an include entry without a project belongs to every project: {filtered}");
    }

    [Test]
    public void Filter_OfAnIncludeOnlyMatrix_KeepsOnlyEntriesWithASelectedProject()
    {
        JsonObject includeOnly = new() { ["include"] = Matrix["include"]!.DeepClone() };
        JsonObject? filtered = TestMatrix.Filter(includeOnly, static _ => true);
        Assert.That(filtered!["include"]!.AsArray().Count, Is.EqualTo(1), "an entry without a project cannot run alone");
        Assert.That(TestMatrix.Filter(includeOnly, static _ => false), Is.Null, "GitHub rejects an empty matrix, so the job is skipped");
    }

    [Test]
    public void Filter_OfAPack_RunsOnlyItsSelectedProjects()
    {
        JsonObject packed = JsonNode.Parse("""
            {"runner": ["ubuntu-latest"], "project": ["Nethermind.Trie.Test", {"name": "Nethermind tests 1", "list": "Nethermind.Abi.Test Nethermind.Api.Test"}]}
            """)!.AsObject();

        JsonObject? filtered = TestMatrix.Filter(packed, static project => project == "Nethermind.Api.Test");

        Assert.That(filtered!["project"]!.ToJsonString(), Is.EqualTo("""[{"name":"Nethermind tests 1","list":"Nethermind.Api.Test"}]"""),
            "the pack keeps its name and runs only its selected project");
        Assert.That(TestMatrix.Filter(packed, static _ => false), Is.Null, "no project, no job");
    }

    [Test]
    public void Filter_WithTestObjects_KeepsTheSelectedOnes()
    {
        JsonObject matrix = JsonNode.Parse("""{"runner": ["ubuntu-latest"], "test": [{"project": "Ethereum.Legacy.Blockchain.Test", "chunk": "1of8"}]}""")!.AsObject();
        Assert.That(TestMatrix.Filter(matrix, static _ => false), Is.Null, "a job with nothing to run is skipped");
        Assert.That(JsonNode.DeepEquals(TestMatrix.Filter(matrix, static _ => true), matrix), Is.True, "a kept test object is unchanged");
    }

    [Test]
    public void Filter_KeepingEveryProject_ReturnsEveryRepositoryMatrixUnchanged()
    {
        JsonObject matrices = JsonFiles.ReadNode(Path.Combine(RepositoryRoot.Find(), TestWorkflows.MatricesFile)).AsObject();
        foreach ((string workflow, JsonNode? jobs) in matrices)
        {
            foreach ((string job, JsonNode? matrix) in jobs!.AsObject())
            {
                Assert.That(JsonNode.DeepEquals(TestMatrix.Filter(matrix!.AsObject(), static _ => true), matrix), Is.True, $"{workflow}/{job} stays whole");
            }
        }
    }

    [Test]
    public void AllProjects_FindsProjectsInEveryShape()
    {
        JsonObject matrices = new()
        {
            ["tests"] = Matrix,
            ["packed"] = JsonNode.Parse("""{"project": [{"name": "p", "list": "Nethermind.Abi.Test Nethermind.Api.Test"}]}""")!,
        };
        Assert.That(TestMatrix.AllProjects(matrices), Is.EquivalentTo(new[] { "Nethermind.Evm.Test", "Nethermind.JsonRpc.Test", "Nethermind.Abi.Test", "Nethermind.Api.Test" }),
            "plain names, include entries and packs");
    }
}
