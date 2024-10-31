// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.t8n;

using System.CommandLine;

public static class T8NCommandOptions
{
    public static Option<string> InputAllocOpt { get; } = new("--input.alloc", description: "Input allocations");
    public static Option<string> InputEnvOpt { get; } = new("--input.env", description: "Input environment");
    public static Option<string> InputTxsOpt { get; } = new("--input.txs", description: "Input transactions");

    public static Option<string> OutputAllocOpt { get; } = new("--output.alloc", description: "Output allocations");
    public static Option<string> OutputResultOpt { get; } = new("--output.result", description: "Output result");
    public static Option<string> OutputBodyOpt { get; } = new("--output.body", description: "Output body");
    public static Option<string> OutputBaseDirOpt { get; } = new("--output.basedir", description: "Output base directory");

    public static Option<ulong?> StateChainIdOpt { get; } = new("--state.chainid", description: "State chain id");
    public static Option<string> StateForkOpt { get; } = new("--state.fork", description: "State fork");
    public static Option<string> StateRewardOpt { get; } = new("--state.reward", description: "State reward");

    public static Option<bool> TraceOpt { get; } = new("--trace", description: "Configures the use of the JSON opcode tracer. This tracer emits traces to files as trace-<txIndex>-<txHash>.json");
    public static Option<bool> TraceMemoryOpt { get; } = new("--trace.memory", description: "Trace memory");
    public static Option<bool> TraceNoStackOpt { get; } = new("--trace.nostack", description: "Trace no stack");
    public static Option<bool> TraceReturnDataOpt { get; } = new("--trace.returndata", description: "Trace return data");

    public static Command CreateCommand()
    {
        var cmd = new Command("t8n", "EVM State Transition command")
        {
            InputAllocOpt,
            InputEnvOpt,
            InputTxsOpt,
            OutputAllocOpt,
            OutputBaseDirOpt,
            OutputBodyOpt,
            OutputResultOpt,
            StateChainIdOpt,
            StateForkOpt,
            StateRewardOpt,
            TraceOpt,
            TraceMemoryOpt,
            TraceNoStackOpt,
            TraceReturnDataOpt,
        };
        cmd.AddAlias("transition");
        return cmd;
    }
}
