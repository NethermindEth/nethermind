// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public static class ExecutionEnvironmentExtensions
{
    public static int GetGethTraceDepth(this ExecutionEnvironment env) => env.CallDepth + 1;

    public static bool IsPostMerge(this ExecutionEnvironment env) => env.TxExecutionContext.BlockExecutionContext.Header.IsPostMerge;
}
