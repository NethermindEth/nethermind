// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public static class ExecutionEnvironmentExtensions
{
    public static int GetGethTraceDepth(in this ExecutionEnvironment env) => env.CallDepth + 1;
}
