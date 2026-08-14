// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Data;

internal static class ForkConfigFactory
{
    internal static ForkConfig? Create(Fork? fork, ISpecProvider specProvider) =>
        fork is { } scheduledFork ? Create(scheduledFork, specProvider) : null;

    internal static ForkConfig Create(Fork fork, ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(fork.Activation.BlockNumber, fork.Activation.Timestamp);

        return new ForkConfig
        {
            ActivationTime = fork.Activation.Timestamp is not null ? (int)fork.Activation.Timestamp : null,
            ActivationBlock = fork.Activation.Timestamp is null ? (int)fork.Activation.BlockNumber : null,
            BlobSchedule = spec.IsEip4844Enabled ? new BlobScheduleSettingsForRpc
            {
                BaseFeeUpdateFraction = (int)spec.BlobBaseFeeUpdateFraction,
                Max = (int)spec.MaxBlobCount,
                Target = (int)spec.TargetBlobCount,
            } : null,
            ChainId = specProvider.ChainId,
            ForkId = fork.Id.HashBytes,
            Precompiles = spec.ListPrecompiles(),
            SystemContracts = spec.ListSystemContracts(),
        };
    }
}
