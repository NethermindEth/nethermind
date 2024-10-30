// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.t8n.Errors;
using Evm.t8n.JsonTypes;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Evm.t8n;

public static class T8NInputProcessor
{
    private static readonly TxDecoder TxDecoder = TxDecoder.Instance;

    public static T8NTest ProcessInputAndConvertToT8NTest(T8NCommandArguments arguments)
    {
        InputData inputData = T8NInputReader.ReadInputData(arguments);

        if (inputData.Env is null)
        {
            throw new T8NException("Env is not provided", T8NErrorCodes.ErrorIO);
        }

        (ISpecProvider specProvider, IReleaseSpec spec) = GetSpec(arguments, inputData.Env);

        T8NValidator.ApplyChecks(inputData.Env, specProvider, spec);

        var gethTraceOptions = new GethTraceOptions
        {
            EnableMemory = arguments.TraceMemory,
            DisableStack = arguments.TraceNoStack
        };

        T8NTest test = new(spec, specProvider)
        {
            Alloc = inputData.Alloc ?? [],
            Transactions = inputData.GetTransactions(TxDecoder),
            CurrentCoinbase = inputData.Env.CurrentCoinbase,
            CurrentGasLimit = inputData.Env.CurrentGasLimit,
            CurrentTimestamp = inputData.Env.CurrentTimestamp,
            CurrentNumber = inputData.Env.CurrentNumber,
            Withdrawals = inputData.Env.Withdrawals,
            CurrentRandom = inputData.Env.GetCurrentRandomHash256(),
            ParentTimestamp = inputData.Env.ParentTimestamp,
            ParentDifficulty = inputData.Env.ParentDifficulty,
            CurrentBaseFee = inputData.Env.CurrentBaseFee,
            CurrentDifficulty = inputData.Env.CurrentDifficulty,
            ParentUncleHash = inputData.Env.ParentUncleHash,
            ParentBaseFee = inputData.Env.ParentBaseFee,
            ParentBeaconBlockRoot = inputData.Env.ParentBeaconBlockRoot,
            ParentGasUsed = inputData.Env.ParentGasUsed,
            ParentGasLimit = inputData.Env.ParentGasLimit,
            ParentExcessBlobGas = inputData.Env.ParentExcessBlobGas,
            CurrentExcessBlobGas = inputData.Env.CurrentExcessBlobGas,
            ParentBlobGasUsed = inputData.Env.ParentBlobGasUsed,
            Ommers = inputData.Env.Ommers,
            BlockHashes = inputData.Env.BlockHashes,
            StateChainId = arguments.StateChainId,
            GethTraceOptions = gethTraceOptions,
            IsTraceEnabled = arguments.Trace,
        };

        return test;
    }

    private static (ISpecProvider, IReleaseSpec) GetSpec(T8NCommandArguments arguments, EnvJson env)
    {
        IReleaseSpec spec;
        try
        {
            spec = JsonToEthereumTest.ParseSpec(arguments.StateFork);
        }
        catch (NotSupportedException)
        {
            throw new T8NException($"unsupported fork {arguments.StateFork}", T8NErrorCodes.ErrorConfig);
        }
        OverridableReleaseSpec overridableReleaseSpec = new(spec);

        if (!string.IsNullOrEmpty(arguments.StateReward) && arguments.StateReward != "-1") // (-1 means rewards are disabled)
        {
            overridableReleaseSpec.BlockReward = UInt256.Parse(arguments.StateReward);
        }
        ISpecProvider specProvider = arguments.StateChainId == GnosisSpecProvider.Instance.ChainId
            ? GnosisSpecProvider.Instance
            : new CustomSpecProvider(((ForkActivation)0, Frontier.Instance),
                ((ForkActivation)1, overridableReleaseSpec));

        if (spec is Paris)
        {
            specProvider.UpdateMergeTransitionInfo(env.CurrentNumber, 0);
        }

        return (specProvider, overridableReleaseSpec);
    }
}
