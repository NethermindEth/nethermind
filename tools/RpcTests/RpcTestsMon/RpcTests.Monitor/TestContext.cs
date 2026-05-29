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

    public static string Hex(long n) => $"0x{n:x}";

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
