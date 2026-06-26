// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.RpcTests.Monitor.Dynamic;

namespace Nethermind.RpcTests.Monitor;

// ReSharper disable UnusedMember.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable MemberCanBePrivate.Global

/// <summary>
/// Contains properties and methods accessible in tests JSONs when compiling a new request.
/// </summary>
internal readonly record struct TestContext(TestDefinition Definition, BlockInfo Head)
{
    /// <summary>
    /// Shift behind the head of a block close to the latest, but old enough to assume to be available in all nodes.
    /// </summary>
    internal long RecentNumber => Head.Number - 5;

    public RequestContext Request { get; init; }
    public BlockInfo Recent { get; init; } = null!;

    #region Helper Methods

    public static bool Maybe => Random.Shared.Next(0, 2) == 0;

    public static string Hex(long n) => $"0x{n:x}";
    public static string AsTopic(string address) => address.StartsWith("0x") ? address[2..].PadLeft(64, '0') : address.PadLeft(64, '0');

    public static KnownTopics Topic { get; } = new();

    #endregion

    #region Run Conditions

    public bool EveryBlocks(int n) => (Head.Number + Definition.Index) % n == 0; // index added as jitter
    public static bool EveryBlock => true;

    #endregion
}

internal class BlockInfo(JsonNode json) : IFormattable
{
    public long Number { get; } = Convert.ToInt64(json["number"]!.GetValue<string>(), 16);
    public string Hash { get; } = json["hash"]!.GetValue<string>();
    public long BaseFeePerGas { get; } = Convert.ToInt64(json["baseFeePerGas"]!.GetValue<string>(), 16);

    public override string ToString() => $"{Number} ({Hash})";

    public string ToString(string? format, IFormatProvider? formatProvider) => format?.Equals("#") == true
        ? Number.ToString()
        : ToString();
}

internal class TestDefinition(int index, string filePath, JsonNode json, bool requiresResponse)
{
    public int Index { get; } = index;
    public string FilePath { get; } = filePath;
    public string Description { get; } = json["test"]?["description"]?.GetValue<string>() ?? string.Empty;

    public DynamicExpression<TestContext, bool> Run { get; } = new(json["run"]?.GetValue<string>()
        ?? throw new Exception("Test is missing required property 'run'"));

    public DynamicJson<TestContext> Request { get; } = new(json["request"]
        ?? throw new Exception("Test is missing required property 'request'"));

    public DynamicJson<TestContext>? Response { get; } = json["response"] is { } responseNode
        ? new(responseNode)
        : requiresResponse ? throw new Exception("Test is missing required 'response' property") : null;

    // basic response paths excluded from comparison (e.g. "result.totalDifficulty")
    public JsonPath[] IgnorePaths { get; } = ParseIgnorePaths(json["ignore"]);

    private static JsonPath[] ParseIgnorePaths(JsonNode? ignore) => ignore switch
    {
        JsonArray paths => [.. paths.Select(static p => new JsonPath(p!.GetValue<string>()))],
        JsonValue value => [new JsonPath(value.GetValue<string>())],
        _ => []
    };
}

internal record struct RequestContext(long Number) { }

internal record TestFailure(TestContext Test, JsonNode Request, JsonNode ActualResponse, JsonNode ExpectedResponse)
{
    public BlockInfo Head => Test.Head;
    public string MonitorName { get; init; } = "";
    public IReadOnlyList<ReorgEntry> RecentReorgs { get; init; } = [];
}

internal class KnownTopics
{
    public string Transfer => "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    public string Approval => "0x8c5be1e5ebec7d5bd14f71427d1e84f3dd0314c0f7b2291e5b200ac8c7c3b925";
    public string Withdrawal => "0x7fcf532c15f0a6db0bd6d0e038bea71d30d808c7d98cb3bf7268a95bf5081b65";
}
