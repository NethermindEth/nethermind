// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;

namespace Evm.T8n;

public static class T8nValidator
{
    public static void ApplyChecks(EnvJson env, ISpecProvider specProvider, IReleaseSpec spec)
    {
        ApplyLondonChecks(env, spec);
        ApplyShanghaiChecks(env, spec);
        ApplyCancunChecks(env, spec);
        ApplyMergeChecks(env, specProvider);
    }

    private static void ApplyLondonChecks(EnvJson env, IReleaseSpec spec)
    {
        if (spec is not London) return;
        if (env.CurrentBaseFee is not null) return;

        if (!env.ParentBaseFee.HasValue || env.CurrentNumber == 0)
        {
            throw new T8nException("EIP-1559 config but missing 'parentBaseFee' in env section",
                T8nErrorCodes.ErrorConfig);
        }

        var parent = Build.A.BlockHeader.WithNumber(env.CurrentNumber - 1).WithBaseFee(env.ParentBaseFee.Value)
            .WithGasUsed(env.ParentGasUsed).WithGasLimit(env.ParentGasLimit).TestObject;
        env.CurrentBaseFee = BaseFeeCalculator.Calculate(parent, spec);
    }

    private static void ApplyShanghaiChecks(EnvJson env, IReleaseSpec spec)
    {
        if (spec is not Shanghai) return;
        if (env.Withdrawals is null)
        {
            throw new T8nException("Shanghai config but missing 'withdrawals' in env section",
                T8nErrorCodes.ErrorConfig);
        }
    }

    private static void ApplyCancunChecks(EnvJson env, IReleaseSpec spec)
    {
        if (spec is not Cancun)
        {
            env.ParentBeaconBlockRoot = null;
            return;
        }

        if (env.ParentBeaconBlockRoot is null)
        {
            throw new T8nException("post-cancun env requires parentBeaconBlockRoot to be set",
                T8nErrorCodes.ErrorConfig);
        }
    }

    private static void ApplyMergeChecks(EnvJson env, ISpecProvider specProvider)
    {
        if (specProvider.TerminalTotalDifficulty?.IsZero ?? false)
        {
            if (env.CurrentRandom is null)
                throw new T8nException("post-merge requires currentRandom to be defined in env",
                    T8nErrorCodes.ErrorConfig);
            if (env.CurrentDifficulty?.IsZero ?? false)
                throw new T8nException("post-merge difficulty must be zero (or omitted) in env",
                    T8nErrorCodes.ErrorConfig);
            return;
        }

        if (env.CurrentDifficulty is not null) return;
        if (!env.ParentDifficulty.HasValue)
        {
            throw new T8nException(
                "currentDifficulty was not provided, and cannot be calculated due to missing parentDifficulty",
                T8nErrorCodes.ErrorConfig);
        }

        if (env.CurrentNumber == 0)
        {
            throw new T8nException("currentDifficulty needs to be provided for block number 0",
                T8nErrorCodes.ErrorConfig);
        }

        if (env.CurrentTimestamp <= env.ParentTimestamp)
        {
            throw new T8nException(
                $"currentDifficulty cannot be calculated -- currentTime ({env.CurrentTimestamp}) needs to be after parent time ({env.ParentTimestamp})",
                T8nErrorCodes.ErrorConfig);
        }

        EthashDifficultyCalculator difficultyCalculator = new(specProvider);

        env.CurrentDifficulty = difficultyCalculator.Calculate(env.ParentDifficulty.Value, env.ParentTimestamp,
            env.CurrentTimestamp, env.CurrentNumber, env.ParentUncleHash is not null);
    }
}

