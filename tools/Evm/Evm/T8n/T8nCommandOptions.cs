// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.T8n;

public static class T8nCommandOptions
{
    public static CliOption<string> InputAllocOpt { get; } = new("--input.alloc")
    {
        Description = "Input allocations"
    };
    public static CliOption<string> InputEnvOpt { get; } = new("--input.env")
    {
        Description = "Input environment"
    };
    public static CliOption<string> InputTxsOpt { get; } = new("--input.txs")
    {
        Description = "Input transactions"
    };

    public static CliOption<string> OutputAllocOpt { get; } = new("--output.alloc")
    {
        Description = "Output allocations"
    };
    public static CliOption<string> OutputResultOpt { get; } = new("--output.result")
    {
        Description = "Output result"
    };
    public static CliOption<string> OutputBodyOpt { get; } = new("--output.body")
    {
        Description = "Output body"
    };
    public static CliOption<string> OutputBaseDirOpt { get; } = new("--output.basedir")
    {
        Description = "Output base directory"
    };

    public static CliOption<ulong?> StateChainIdOpt { get; } = new("--state.chainid")
    {
        Description = "State chain id"
    };
    public static CliOption<string> StateForkOpt { get; } = new("--state.fork")
    {
        Description = "State fork"
    };
    public static CliOption<string> StateRewardOpt { get; } = new("--state.reward")
    {
        Description = "State reward"
    };
    public static CliOption<bool> TraceOpt { get; } = new("--trace")
    {
        Description = "Configures the use of the JSON opcode tracer. This tracer emits traces to files as trace-<txIndex>-<txHash>.json"
    };
    public static CliOption<bool> TraceMemoryOpt { get; } = new("--trace.memory")
    {
        Description = "Trace memory"
    };
    public static CliOption<bool> TraceNoStackOpt { get; } = new("--trace.nostack")
    {
        Description = "Trace no stack"
    };
    public static CliOption<bool> TraceReturnDataOpt { get; } = new("--trace.returndata")
    {
        Description = "Trace return data"
    };

    public static CliCommand CreateCommand()
    {
        CliCommand cmd = new("t8n", "EVM state transition")
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
        cmd.Aliases.Add("transition");

        return cmd;
    }
}
