// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine.Parsing;
using Nethermind.Specs;

namespace Evm.t8n;

public class T8NCommandArguments
{
    public string InputAlloc { get; set; } = "alloc.json";
    public string InputEnv { get; set; } = "env.json";
    public string InputTxs { get; set; } = "txs.json";

    public string OutputAlloc { get; set; } = "alloc.json";
    public string OutputResult { get; set; } = "result.json";
    public string? OutputBody { get; set; }
    public string? OutputBaseDir { get; set; }

    public ulong StateChainId { get; set; } = MainnetSpecProvider.Instance.ChainId;
    public string StateFork { get; set; } = "GrayGlacier";
    public string StateReward { get; set; } = "0";

    public bool Trace { get; set; }
    public bool TraceMemory { get; set; }
    public bool TraceNoStack { get; set; }
    public bool TraceReturnData { get; set; }

    public static T8NCommandArguments FromParseResult(ParseResult parseResult)
    {
        var arguments = new T8NCommandArguments
        {
            OutputBody = parseResult.GetValueForOption(T8NCommandOptions.OutputBodyOpt),
            OutputBaseDir = parseResult.GetValueForOption(T8NCommandOptions.OutputBaseDirOpt),
            Trace = parseResult.GetValueForOption(T8NCommandOptions.TraceOpt),
            TraceMemory = parseResult.GetValueForOption(T8NCommandOptions.TraceMemoryOpt),
            TraceNoStack = parseResult.GetValueForOption(T8NCommandOptions.TraceNoStackOpt),
            TraceReturnData = parseResult.GetValueForOption(T8NCommandOptions.TraceReturnDataOpt)
        };

        var inputAlloc = parseResult.GetValueForOption(T8NCommandOptions.InputAllocOpt);
        if (inputAlloc != null)
        {
            arguments.InputAlloc = inputAlloc;
        }

        var inputEnv = parseResult.GetValueForOption(T8NCommandOptions.InputEnvOpt);
        if (inputEnv != null)
        {
            arguments.InputEnv = inputEnv;
        }

        var inputTxs = parseResult.GetValueForOption(T8NCommandOptions.InputTxsOpt);
        if (inputTxs != null)
        {
            arguments.InputTxs = inputTxs;
        }

        var outputAlloc = parseResult.GetValueForOption(T8NCommandOptions.OutputAllocOpt);
        if (outputAlloc != null)
        {
            arguments.OutputAlloc = outputAlloc;
        }

        var outputResult = parseResult.GetValueForOption(T8NCommandOptions.OutputResultOpt);
        if (outputResult != null)
        {
            arguments.OutputResult = outputResult;
        }

        var stateFork = parseResult.GetValueForOption(T8NCommandOptions.StateForkOpt);
        if (stateFork != null)
        {
            arguments.StateFork = stateFork;
        }

        var stateReward = parseResult.GetValueForOption(T8NCommandOptions.StateRewardOpt);
        if (stateReward != null)
        {
            arguments.StateReward = stateReward;
        }

        var stateChainId = parseResult.GetValueForOption(T8NCommandOptions.StateChainIdOpt);
        if (stateChainId.HasValue)
        {
            arguments.StateChainId = stateChainId.Value;
        }

        return arguments;
    }

}
