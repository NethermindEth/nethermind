// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Evm.T8n;

public static class T8nInputProcessor
{
    private static readonly TxDecoder TransactionDecoder = TxDecoder.Instance;

    public static T8nTest ProcessInputAndConvertToT8nTest(T8nCommandArguments arguments)
    {
        InputData inputData = T8nInputReader.ReadInputData(arguments);

        if (inputData.Env is null)
        {
            throw new T8nException("Env is not provided", T8nErrorCodes.ErrorIO);
        }

        (ISpecProvider specProvider, IReleaseSpec spec) = GetSpec(arguments, inputData.Env);

        T8nValidator.ApplyChecks(inputData.Env, specProvider, spec);

        GethTraceOptions gethTraceOptions = new()
        {
            EnableMemory = arguments.TraceMemory,
            DisableStack = arguments.TraceNoStack,
            EnableReturnData = arguments.TraceReturnData,
        };

        T8nTest test = new(spec, specProvider, inputData.Env.CurrentCoinbase)
        {
            Alloc = inputData.Alloc ?? [],
            Transactions = inputData.GetTransactions(TransactionDecoder, specProvider.ChainId),
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
            BlockHashes = inputData.Env.BlockHashes?.ToDictionary(kvp => ParseBlockNumber(kvp.Key), kvp => kvp.Value),
            StateChainId = arguments.StateChainId,
            GethTraceOptions = gethTraceOptions,
            IsTraceEnabled = arguments.Trace,
            AreRewardsEnabled = !IsRewardDisabled(arguments),
        };

        return test;
    }

    private static (ISpecProvider, IReleaseSpec) GetSpec(T8nCommandArguments arguments, EnvJson env)
    {
        IReleaseSpec spec;
        try
        {
            spec = SpecNameParser.Parse(arguments.StateFork);
        }
        catch (NotSupportedException e)
        {
            throw new T8nException(e, $"unsupported fork {arguments.StateFork}", T8nErrorCodes.ErrorConfig);
        }
        OverridableReleaseSpec overridableReleaseSpec = new(spec);

        if (!string.IsNullOrEmpty(arguments.StateReward) && !IsRewardDisabled(arguments))
        {
            overridableReleaseSpec.BlockReward = UInt256.Parse(arguments.StateReward);
        }
        ISpecProvider specProvider = arguments.StateChainId == GnosisSpecProvider.Instance.ChainId
            ? GnosisSpecProvider.Instance
            : new CustomSpecProvider(((ForkActivation)0, Frontier.Instance),
                ((ForkActivation)1, overridableReleaseSpec));

        if (spec is NamedReleaseSpec { IsPostMerge: true })
        {
            specProvider.UpdateMergeTransitionInfo(env.CurrentNumber, 0);
        }

        return (specProvider, overridableReleaseSpec);
    }

    /// <remarks>As in geth, a negative <c>--state.reward</c> skips the block and ommer rewards.</remarks>
    private static bool IsRewardDisabled(T8nCommandArguments arguments) => arguments.StateReward.StartsWith('-');

    private static ulong ParseBlockNumber(string number) =>
        number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(number.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(number, CultureInfo.InvariantCulture);
}
