// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal record struct RequestContext(long Number) { }

// ReSharper disable UnusedMember.Global - to be used by test compiler
internal readonly record struct TestContext(TestDefinition Definition, BlockInfo Head)
{
    public RequestContext Request { get; init; }

    #region Helper Methods

    public long RecentBlock => Math.Max(1, Head.Number - 5); // recent, but old enough to assume available in all nodes

    public static string Hex(long n) => $"0x{n:x}";
    public static string AsTopic(string address) => address.StartsWith("0x") ? address[2..].PadLeft(64, '0') : address.PadLeft(64, '0');

    public static KnownAddresses Address { get; } = new();
    public static KnownTopics Topic { get; } = new();

    #endregion

    #region Run Conditions

    public bool EveryBlocks(int n) => (Head.Number + Definition.Index) % n == 0; // index added as jitter
    public static bool EveryBlock => true;

    #endregion
}

internal record TestFailure(TestContext Test, JsonNode Request, JsonNode ActualResponse, JsonNode ExpectedResponse)
{
    public BlockInfo Head => Test.Head;
}

internal class KnownAddresses
{
    public string WETH => "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2";
    public string USDT => "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2";
}

internal class KnownTopics
{
    public string Transfer => "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    public string Approval => "0x8c5be1e5ebec7d5bd14f71427d1e84f3dd0314c0f7b2291e5b200ac8c7c3b925";
}
