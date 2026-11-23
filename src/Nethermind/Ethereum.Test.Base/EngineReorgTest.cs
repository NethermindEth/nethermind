// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;

namespace Ethereum.Test.Base;

/// <summary>
/// An EEST <c>blockchain_test_engine_reorg</c> fixture: a DAG of engine payloads and a branching script of Engine API
/// and JSON-RPC steps.
/// </summary>
/// <remarks>
/// See <c>docs/running_tests/test_formats/blockchain_test_engine_reorg.md</c> in <c>ethereum/execution-specs</c>.
/// </remarks>
public class EngineReorgTest : BlockchainTest
{
    /// <summary>The block DAG, keyed by label.</summary>
    public Dictionary<string, EngineReorgBlockJson> ReorgBlocks { get; set; } = [];

    /// <summary>The ordered, branching step script, kept as JSON and interpreted as it runs.</summary>
    public JsonElement[] Steps { get; set; } = [];
}

public class EngineReorgTestJson : BlockchainTestJson
{
    public new Dictionary<string, EngineReorgBlockJson>? Blocks { get; set; }
    public JsonElement[]? Steps { get; set; }
}

public class EngineReorgBlockJson
{
    public string Parent { get; set; } = "";
    public TestEngineNewPayloadsJson Payload { get; set; } = new();
}
