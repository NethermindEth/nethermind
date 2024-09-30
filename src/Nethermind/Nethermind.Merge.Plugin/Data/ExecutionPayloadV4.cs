// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3, IExecutionPayloadFactory<ExecutionPayloadV4>
{
    protected new static TExecutionPayload Create<TExecutionPayload>(Block block) where TExecutionPayload : ExecutionPayloadV4, new()
    {
        TExecutionPayload executionPayload = ExecutionPayloadV3.Create<TExecutionPayload>(block);
        ConsensusRequest[]? blockRequests = block.Requests;
        (executionPayload.DepositRequests, executionPayload.WithdrawalRequests) = blockRequests?.SplitRequests() ?? ([], []);
        return executionPayload;
    }

    public new static ExecutionPayloadV4 Create(Block block) => Create<ExecutionPayloadV4>(block);

    public override bool TryGetBlock([NotNullWhen(true)] out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        var depositsLength = DepositRequests?.Length ?? 0;
        var withdrawalRequestsLength = WithdrawalRequests?.Length ?? 0;
        var requestsCount = depositsLength + withdrawalRequestsLength;
        if (requestsCount > 0)
        {
            var requests = new ConsensusRequest[requestsCount];
            int i = 0;
            for (; i < depositsLength; ++i)
            {
                requests[i] = DepositRequests![i];
            }

            for (; i < requestsCount; ++i)
            {
                requests[i] = WithdrawalRequests![i - depositsLength];
            }

            block.Body.Requests = requests;
            block.Header.RequestsRoot = new RequestsTrie(requests).RootHash;
        }
        else
        {
            block.Body.Requests = Array.Empty<ConsensusRequest>();
            block.Header.RequestsRoot = Keccak.EmptyTreeHash;
        }

        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).DepositsEnabled
        && specProvider.GetSpec(BlockNumber, Timestamp).WithdrawalRequestsEnabled;

    /// <summary>
    /// Gets or sets <see cref="Block.Requests"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-6110">EIP-6110</see>.
    /// </summary>
    [JsonRequired]
    public sealed override Deposit[]? DepositRequests { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.WithdrawalRequests"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7002">EIP-7002</see>.
    /// </summary>
    [JsonRequired]
    public sealed override WithdrawalRequest[]? WithdrawalRequests { get; set; }
}
