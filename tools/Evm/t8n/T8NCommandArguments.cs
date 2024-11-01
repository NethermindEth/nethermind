// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
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
            OutputBody = parseResult.GetValue(T8NCommandOptions.OutputBodyOpt),
            OutputBaseDir = parseResult.GetValue(T8NCommandOptions.OutputBaseDirOpt),
            Trace = parseResult.GetValue(T8NCommandOptions.TraceOpt),
            TraceMemory = parseResult.GetValue(T8NCommandOptions.TraceMemoryOpt),
            TraceNoStack = parseResult.GetValue(T8NCommandOptions.TraceNoStackOpt),
            TraceReturnData = parseResult.GetValue(T8NCommandOptions.TraceReturnDataOpt)
        };

        var inputAlloc = parseResult.GetValue(T8NCommandOptions.InputAllocOpt);
        if (inputAlloc is not null)
        {
            arguments.InputAlloc = inputAlloc;
        }

        var inputEnv = parseResult.GetValue(T8NCommandOptions.InputEnvOpt);
        if (inputEnv is not null)
        {
            arguments.InputEnv = inputEnv;
        }

        var inputTxs = parseResult.GetValue(T8NCommandOptions.InputTxsOpt);
        if (inputTxs is not null)
        {
            arguments.InputTxs = inputTxs;
        }

        var outputAlloc = parseResult.GetValue(T8NCommandOptions.OutputAllocOpt);
        if (outputAlloc is not null)
        {
            arguments.OutputAlloc = outputAlloc;
        }

        var outputResult = parseResult.GetValue(T8NCommandOptions.OutputResultOpt);
        if (outputResult is not null)
        {
            arguments.OutputResult = outputResult;
        }

        var stateFork = parseResult.GetValue(T8NCommandOptions.StateForkOpt);
        if (stateFork is not null)
        {
            arguments.StateFork = stateFork;
        }

        var stateReward = parseResult.GetValue(T8NCommandOptions.StateRewardOpt);
        if (stateReward is not null)
        {
            arguments.StateReward = stateReward;
        }

        var stateChainId = parseResult.GetValue(T8NCommandOptions.StateChainIdOpt);
        if (stateChainId.HasValue)
        {
            arguments.StateChainId = stateChainId.Value;
        }

        return arguments;
    }

}
