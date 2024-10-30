// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.t8n;

using System.CommandLine;

public static class T8NCommandOptions
{
    public static Option<string> InputAllocOpt { get; } = new Option<string>("--input.alloc", description: "Input allocations", getDefaultValue: () => "alloc.json");
    public static Option<string> InputEnvOpt { get; } = new Option<string>("--input.env", description: "Input environment", getDefaultValue: () => "env.json");
    public static Option<string> InputTxsOpt { get; } = new Option<string>("--input.txs", description: "Input transactions", getDefaultValue: () => "txs.json");

    public static Option<string> OutputAllocOpt { get; } = new Option<string>("--output.alloc", description: "Output allocations", getDefaultValue: () => "alloc.json");
    public static Option<string> OutputResultOpt { get; } = new Option<string>("--output.result", description: "Output result", getDefaultValue: () => "result.json");
    public static Option<string> OutputBodyOpt { get; } = new Option<string>("--output.body", description: "Output body");
    public static Option<string> OutputBaseDirOpt { get; } = new Option<string>("--output.basedir", description: "Output base directory");

    public static Option<ulong> StateChainIdOpt { get; } = new Option<ulong>("--state.chainid", description: "State chain id", getDefaultValue: () => 1);
    public static Option<string> StateForkOpt { get; } = new Option<string>("--state.fork", description: "State fork", getDefaultValue: () => "GrayGlacier");
    public static Option<string> StateRewardOpt { get; } = new Option<string>("--state.reward", description: "State reward");

    public static Option<bool> TraceOpt { get; } = new Option<bool>("--trace", description: "Configures the use of the JSON opcode tracer. This tracer emits traces to files as trace-<txIndex>-<txHash>.json", getDefaultValue: () => false);
    public static Option<bool> TraceMemoryOpt { get; } = new Option<bool>("--trace.memory", description: "Trace memory", getDefaultValue: () => false);
    public static Option<bool> TraceNoStackOpt { get; } = new Option<bool>("--trace.nostack", description: "Trace no stack", getDefaultValue: () => false);
    public static Option<bool> TraceReturnDataOpt { get; } = new Option<bool>("--trace.returndata", description: "Trace return data", getDefaultValue: () => false);

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
