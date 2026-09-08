// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal class ExecutionArgs
{
    public required Uri TargetUrl { get; init; }
    public required Uri? ReferenceUrl { get; init; }
    public required string[] TestGlobs { get; init; }
    public int Parallelism { get; init; } = 10;
}
