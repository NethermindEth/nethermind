// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;

/// <summary>
/// The Optimism specific execution payload.
/// </summary>
public class OptimismExecutionPayloadV3 : ExecutionPayloadV3, IExecutionPayloadFactory<OptimismExecutionPayloadV3>
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hash256? WithdrawalsRoot { get; set; }

    public new static OptimismExecutionPayloadV3 Create(Block block)
    {
        OptimismExecutionPayloadV3 payload = Create<OptimismExecutionPayloadV3>(block);
        payload.WithdrawalsRoot = block.WithdrawalsRoot;
        return payload;
    }

    protected override Hash256? BuildWithdrawalsRoot() => WithdrawalsRoot ?? Keccak.EmptyTreeHash;

    /// <inheritdoc/>
    /// <remarks>Isthmus also opens <c>engine_newPayloadV4</c>, matching the capability advertised for it,
    /// whether or not the chain enables the request EIPs.</remarks>
    public override bool ValidateForkOnNewPayload(ISpecProvider specProvider, int newPayloadVersion) =>
        base.ValidateForkOnNewPayload(specProvider, newPayloadVersion)
        || (newPayloadVersion == EngineApiVersions.NewPayload.V4
            && specProvider.GetSpec(BlockNumber, Timestamp) is IOptimismReleaseSpec { IsOpIsthmusEnabled: true });
}
