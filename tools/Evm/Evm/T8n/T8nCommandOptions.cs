// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Evm.T8n;

public static class T8nCommandOptions
{
    public static Option<string> InputAllocOpt { get; } = new("--input.alloc", "--input-alloc")
    {
        Description = "Input allocations"
    };
    public static Option<string> InputEnvOpt { get; } = new("--input.env", "--input-env")
    {
        Description = "Input environment"
    };
    public static Option<string> InputTxsOpt { get; } = new("--input.txs", "--input-txs")
    {
        Description = "Input transactions"
    };

    public static Option<string> OutputAllocOpt { get; } = new("--output.alloc", "--output-alloc")
    {
        Description = "Output allocations"
    };
    public static Option<string> OutputResultOpt { get; } = new("--output.result", "--output-result")
    {
        Description = "Output result"
    };
    public static Option<string> OutputBodyOpt { get; } = new("--output.body", "--output-body")
    {
        Description = "Output body"
    };
    public static Option<string> OutputBaseDirOpt { get; } = new("--output.basedir", "--output-basedir")
    {
        Description = "Output base directory"
    };

    public static Option<ulong?> StateChainIdOpt { get; } = new("--state.chainid", "--state-chainid")
    {
        Description = "State chain id"
    };
    public static Option<string> StateForkOpt { get; } = new("--state.fork", "--state-fork")
    {
        Description = "State fork"
    };
    public static Option<string> StateRewardOpt { get; } = new("--state.reward", "--state-reward")
    {
        Description = "State reward"
    };
    public static Option<bool> TraceOpt { get; } = new("--trace")
    {
        Description = "Configures the use of the JSON opcode tracer. This tracer emits traces to files as trace-<txIndex>-<txHash>.json"
    };
    public static Option<bool> TraceMemoryOpt { get; } = new("--trace.memory", "--trace-memory")
    {
        Description = "Trace memory"
    };
    public static Option<bool> TraceNoStackOpt { get; } = new("--trace.nostack", "--trace-nostack")
    {
        Description = "Trace no stack"
    };
    public static Option<bool> TraceReturnDataOpt { get; } = new("--trace.returndata", "--trace-returndata")
    {
        Description = "Trace return data"
    };

    public static Command CreateCommand()
    {
        Command cmd = new("t8n", "EVM state transition")
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
