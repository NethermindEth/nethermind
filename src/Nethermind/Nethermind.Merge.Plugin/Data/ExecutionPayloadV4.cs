// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV3>
{
    public override bool TryGetBlock([NotNullWhen(true)] out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block.Header.RequestsHash = ExecutionRequests?.CalculateRoot();

        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).DepositsEnabled
        && specProvider.GetSpec(BlockNumber, Timestamp).WithdrawalRequestsEnabled
        && specProvider.GetSpec(BlockNumber, Timestamp).ConsolidationRequestsEnabled;

    /// <summary>
    /// Gets or sets <see cref="ExecutionRequets"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7685">EIP-7685</see>.
    /// </summary>
    [JsonIgnore]
    public sealed override ExecutionRequest[]? ExecutionRequests { get; set; }

}
