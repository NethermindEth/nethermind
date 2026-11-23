// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Network;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>Creates the execution fork configuration returned by JSON-RPC.</summary>
internal static class ForkConfigFactory
{
    private static readonly JsonSerializerOptions UnchangedDictionaryKeyOptions =
        new(EthereumJsonSerializer.JsonOptionsIndented) { DictionaryKeyPolicy = null };

    /// <summary>Serializes the active, next and last scheduled execution forks for <c>eth_config</c>.</summary>
    internal static JsonNode CreateSummary(ForkActivationsSummary forks, ISpecProvider specProvider) =>
        JsonSerializer.SerializeToNode(new ForkConfigSummary
        {
            Current = Create(forks.Current, specProvider),
            Next = Create(forks.Next, specProvider),
            Last = Create(forks.Last, specProvider)
        }, TypeInfoJsonSerializer.GetTypeInfo<ForkConfigSummary>(UnchangedDictionaryKeyOptions))!;

    private static ForkConfig? Create(Fork? fork, ISpecProvider specProvider) =>
        fork is { } scheduledFork ? Create(scheduledFork, specProvider) : null;

    internal static ForkConfig Create(Fork fork, ISpecProvider specProvider, bool? active = null)
    {
        IReleaseSpec spec = specProvider.GetSpec(fork.Activation.BlockNumber, fork.Activation.Timestamp);

        return new ForkConfig
        {
            ActivationTime = fork.Activation.Timestamp,
            ActivationBlock = fork.Activation.Timestamp is null ? fork.Activation.BlockNumber : null,
            Active = active,
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
