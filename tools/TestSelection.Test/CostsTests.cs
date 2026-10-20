// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.Matrices;
using Nethermind.TestSelection.Selection;
using NUnit.Framework;

namespace Nethermind.TestSelection.Test;

[TestFixture]
public class CostsTests
{
    private const string BlockLane = "blockTest|sequential";

    internal static readonly CostInventory Inventory = new(
        new Dictionary<string, Dictionary<string, Dictionary<string, double>>>
        {
            [BlockLane] = new()
            {
                ["test_add"] = new() { ["Cancun"] = 10.0, ["Prague"] = 10.0 },
                ["test_addmod"] = new() { ["Cancun"] = 10.0 },
                ["test_mul"] = new() { ["Cancun"] = 70.0 },
            },
            ["stateTest|sequential"] = new() { ["test_mul"] = new() { ["Cancun"] = 100.0 } },
        },
        new Dictionary<string, double> { ["Nethermind.Evm.Test"] = 30.0, ["Nethermind.JsonRpc.Test"] = 70.0 });

    internal static readonly JsonObject NethtestMatrix = new()
    {
        ["include"] = new JsonArray([
            .. Enumerable.Range(1, 4).Select(static i => (JsonNode)Lane("blockTest", $"{i}of4")),
            .. Enumerable.Range(1, 2).Select(static i => (JsonNode)Lane("stateTest", $"{i}of2")),
            Lane("txTest", "1of1"),
        ]),
    };

    private string _run = null!;
    private RunSelection _addOnly = null!;

    [SetUp]
    public void SetUp()
    {
        _run = Fixtures.RunWithoutHoldout();
        Assert.That(Fixtures.Unsampled(_run, "test_addmod", "Cancun") && Fixtures.Unsampled(_run, "test_mul", "Cancun")
            && Fixtures.Unsampled(_run, "Nethermind.JsonRpc.Test"), Is.True, "precondition: the sample skips the tests these cases leave out");
        _addOnly = Selection(tests: ["test_add"]);
    }

    [Test]
    public void FilterLines_MatchTheSelectedTestsAndNothingElse()
    {
        Regex pattern = new($"^(?:{string.Join('|', Inventory.Lanes(_addOnly)[BlockLane].FilterLines())})");

        Assert.That(pattern.IsMatch("test_add[fork_Cancun-blockchain_test-a]"), Is.True, "a selected test with a variant suffix");
        Assert.That(pattern.IsMatch("test_add[fork_Prague]"), Is.True, "a selected test without a suffix");
        Assert.That(pattern.IsMatch("test_addmod[fork_Cancun-blockchain_test-a]"), Is.False, "a test whose name starts with a selected one");
        Assert.That(pattern.IsMatch("test_mul[fork_Cancun-blockchain_test-a]"), Is.False, "an unselected test");
    }

    [Test]
    public void FilterLines_DoNotMatchAForkThatStartsWithTheSelectedOne()
    {
        LaneSelection lane = new();
        lane.Add("test_add", "Prague", 1.0, kept: true);
        Regex pattern = new($"^(?:{string.Join('|', lane.FilterLines())})");

        Assert.That(pattern.IsMatch("test_add[fork_Prague-blockchain_test]"), Is.True, "the selected fork");
        Assert.That(pattern.IsMatch("test_add[fork_PragueToOsakaAtTime15k-blockchain_test]"), Is.False, "a transition fork named after it");
    }

    [Test]
    public void Shares_WeighByCost_AndCountTheSampleByItsExpectedShare()
    {
        RunSelection prague = Selection(forks: ["Prague"]);

        Assert.That(Inventory.ExpectedEestShare(_addOnly), Is.EqualTo((20.0 + Sampling.SampleRate * 180.0) / 200.0).Within(1e-9), "test_add costs 20 of 200");
        Assert.That(Inventory.ExpectedEestShare(prague), Is.EqualTo((10.0 + Sampling.SampleRate * 190.0) / 200.0).Within(1e-9), "Prague costs 10 of 200");
        Assert.That(Inventory.ProjectShare(Selection(projects: ["Nethermind.Evm.Test"])), Is.EqualTo(0.3).Within(1e-9), "Evm.Test costs 30 of 100");
    }

    [Test]
    public void Shrink_KeepsTheSelectedShareOfChunks_AndDropsEmptyLanes()
    {
        JsonObject reduced = Matrices.NethtestMatrix.Shrink(NethtestMatrix, Inventory.Lanes(_addOnly));
        string[] lanes = [.. reduced["include"]!.AsArray().Select(static e => $"{e!["test_type"]} {e["chunk"]}")];
        Assert.That(lanes, Is.EqualTo(new[] { "blockTest 1of1", "txTest 1of1" }), "20% of blockTest is one chunk; stateTest has nothing; txTest is unknown");
    }

    [Test]
    public void Shrink_WhenNothingIsSelected_KeepsTheFullMatrix()
    {
        JsonObject known = new() { ["include"] = new JsonArray([.. NethtestMatrix["include"]!.AsArray().Where(static e => e!["test_type"]!.GetValue<string>() != "txTest").Select(static e => e!.DeepClone())]) };
        Assert.That(JsonNode.DeepEquals(Matrices.NethtestMatrix.Shrink(known, Inventory.Lanes(Selection())), known), Is.True, "GitHub rejects an empty matrix");
    }

    [Test]
    public void Inventory_SavedAndLoaded_KeepsItsCosts()
    {
        using TempDirectory directory = new();
        Inventory.Save(directory.File("inventory.json.gz"));
        CostInventory loaded = CostInventory.Load(directory.File("inventory.json.gz"));

        Assert.That(loaded.Projects, Is.EquivalentTo(Inventory.Projects), "project costs survive");
        Assert.That(loaded.Eest[BlockLane]["test_add"], Is.EquivalentTo(Inventory.Eest[BlockLane]["test_add"]), "EEST costs survive");
    }

    private RunSelection Selection(string[]? tests = null, string[]? forks = null, string[]? projects = null) =>
        new(_run, (tests ?? []).ToHashSet(), (forks ?? []).ToHashSet(), (projects ?? []).ToHashSet());

    private static JsonObject Lane(string type, string chunk) => new() { ["test_type"] = type, ["test_setup"] = "sequential", ["chunk"] = chunk };
}
