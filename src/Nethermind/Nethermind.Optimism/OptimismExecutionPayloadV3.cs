// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;

/// <summary>
/// The Optimism specific execution payload.
/// </summary>
public class OptimismExecutionPayloadV3 : ExecutionPayloadV3
{
    public Hash256? WithdrawalsRoot { get; set; }

    protected override Hash256? BuildWithdrawalsRoot() => WithdrawalsRoot ?? Keccak.EmptyTreeHash;

    /// <inheritdoc/>
    /// <remarks>Isthmus also opens <c>engine_newPayloadV4</c>, matching the capability advertised for it,
    /// whether or not the chain enables the request EIPs.</remarks>
    public override bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion)
    {
        IReleaseSpec spec = specProvider.GetSpec(BlockNumber, Timestamp);
        bool isIsthmusEnabled = spec is IOptimismReleaseSpec { IsOpIsthmusEnabled: true };

        return newPayloadVersion switch
        {
            EngineApiVersions.NewPayload.V3 => spec.IsEip4844Enabled && !isIsthmusEnabled,
            EngineApiVersions.NewPayload.V4 => isIsthmusEnabled,
            _ => false,
        };
    }
}
