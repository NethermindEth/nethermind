// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.Specs;

namespace Evm.T8n;

public class T8nCommandArguments
{
    public string InputAlloc { get; set; } = "alloc.json";
    public string InputEnv { get; set; } = "env.json";
    public string InputTxs { get; set; } = "txs.json";

    public string OutputAlloc { get; set; } = "alloc.json";
    public string OutputResult { get; set; } = "result.json";
    public string? OutputBody { get; set; }
    public string OutputBaseDir { get; set; } = "";

    public ulong StateChainId { get; set; } = MainnetSpecProvider.Instance.ChainId;
    public string StateFork { get; set; } = "GrayGlacier";
    public string StateReward { get; set; } = "0";

    public bool Trace { get; set; }
    public bool TraceMemory { get; set; }
    public bool TraceNoStack { get; set; }
    public bool TraceReturnData { get; set; }

    public static T8nCommandArguments FromParseResult(ParseResult parseResult)
    {
        var arguments = new T8nCommandArguments
        {
            OutputBody = parseResult.GetValue(T8nCommandOptions.OutputBodyOpt),
            Trace = parseResult.GetValue(T8nCommandOptions.TraceOpt),
            TraceMemory = parseResult.GetValue(T8nCommandOptions.TraceMemoryOpt),
            TraceNoStack = parseResult.GetValue(T8nCommandOptions.TraceNoStackOpt),
            TraceReturnData = parseResult.GetValue(T8nCommandOptions.TraceReturnDataOpt)
        };

        var inputAlloc = parseResult.GetValue(T8nCommandOptions.InputAllocOpt);
        if (inputAlloc is not null)
        {
            arguments.InputAlloc = inputAlloc;
        }

        var inputEnv = parseResult.GetValue(T8nCommandOptions.InputEnvOpt);
        if (inputEnv is not null)
        {
            arguments.InputEnv = inputEnv;
        }

        var inputTxs = parseResult.GetValue(T8nCommandOptions.InputTxsOpt);
        if (inputTxs is not null)
        {
            arguments.InputTxs = inputTxs;
        }

        var outputAlloc = parseResult.GetValue(T8nCommandOptions.OutputAllocOpt);
        if (outputAlloc is not null)
        {
            arguments.OutputAlloc = outputAlloc;
        }

        var outputResult = parseResult.GetValue(T8nCommandOptions.OutputResultOpt);
        if (outputResult is not null)
        {
            arguments.OutputResult = outputResult;
        }

        var outputBasedir = parseResult.GetValue(T8nCommandOptions.OutputBaseDirOpt);
        if (outputBasedir is not null)
        {
            arguments.OutputBaseDir = outputBasedir;
        }

        var stateFork = parseResult.GetValue(T8nCommandOptions.StateForkOpt);
        if (stateFork is not null)
        {
            arguments.StateFork = stateFork;
        }

        var stateReward = parseResult.GetValue(T8nCommandOptions.StateRewardOpt);
        if (stateReward is not null)
        {
            arguments.StateReward = stateReward;
        }

        var stateChainId = parseResult.GetValue(T8nCommandOptions.StateChainIdOpt);
        if (stateChainId.HasValue)
        {
            arguments.StateChainId = stateChainId.Value;
        }

        return arguments;
    }

}
