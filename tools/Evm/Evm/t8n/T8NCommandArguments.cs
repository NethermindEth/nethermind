// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine.Parsing;

namespace Evm.t8n;

public class T8NCommandArguments
{
    public string? InputAlloc { get; set; }
    public string? InputEnv { get; set; }
    public string? InputTxs { get; set; }

    public string? OutputAlloc { get; set; }
    public string? OutputResult { get; set; }
    public string? OutputBody { get; set; }
    public string? OutputBaseDir { get; set; }

    public ulong? StateChainId { get; set; }
    public string? StateFork { get; set; }
    public string? StateReward { get; set; }

    public bool? Trace { get; set; }
    public bool? TraceMemory { get; set; }
    public bool? TraceNoStack { get; set; }
    public bool? TraceReturnData { get; set; }

    public static T8NCommandArguments FromParseResult(ParseResult parseResult)
    {
        return new T8NCommandArguments
        {
            InputAlloc = parseResult.GetValueForOption(T8NCommandOptions.InputAllocOpt),
            InputEnv = parseResult.GetValueForOption(T8NCommandOptions.InputEnvOpt),
            InputTxs = parseResult.GetValueForOption(T8NCommandOptions.InputTxsOpt),

            OutputAlloc = parseResult.GetValueForOption(T8NCommandOptions.OutputAllocOpt),
            OutputResult = parseResult.GetValueForOption(T8NCommandOptions.OutputResultOpt),
            OutputBody = parseResult.GetValueForOption(T8NCommandOptions.OutputBodyOpt),
            OutputBaseDir = parseResult.GetValueForOption(T8NCommandOptions.OutputBaseDirOpt),

            StateChainId = parseResult.GetValueForOption(T8NCommandOptions.StateChainIdOpt),
            StateFork = parseResult.GetValueForOption(T8NCommandOptions.StateForkOpt),
            StateReward = parseResult.GetValueForOption(T8NCommandOptions.StateRewardOpt),

            Trace = parseResult.GetValueForOption(T8NCommandOptions.TraceOpt),
            TraceMemory = parseResult.GetValueForOption(T8NCommandOptions.TraceMemoryOpt),
            TraceNoStack = parseResult.GetValueForOption(T8NCommandOptions.TraceNoStackOpt),
            TraceReturnData = parseResult.GetValueForOption(T8NCommandOptions.TraceReturnDataOpt)
        };
    }

}
