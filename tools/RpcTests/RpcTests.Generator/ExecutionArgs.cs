// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using SmartFormat.Core.Parsing;

namespace Nethermind.RpcTests.Generator;

public class ExecutionArgs
{
    public required FilePos[] Sources { get; init; }
    public required Uri? Client { get; init; }
    public required int Parallelism { get; init; }
    public required HashSet<string> Methods { get; init; }
    public required int? MinBlocks { get; init; }
    public required int? MaxBlocks { get; init; }
    public required int? MinResultLen { get; init; }
    public required Format OutputPath { get; init; }
}
