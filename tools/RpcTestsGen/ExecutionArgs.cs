// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace RpcTestsGen;

public class ExecutionArgs
{
    public required FilePos[] Sources { get; init; }
    public required Uri[] Clients { get; init; }
    public required int Parallelism { get; init; }
    public required string? Include { get; init; }
    public required string? Exclude { get; init; }
    public required int? MinBlocks { get; init; }
    public required int? MaxBlocks { get; init; }
    public required int? MinResultLen { get; init; }
}
