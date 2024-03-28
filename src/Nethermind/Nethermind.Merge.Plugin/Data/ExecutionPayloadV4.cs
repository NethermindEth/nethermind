// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayload
{
    public ExecutionPayloadV4() { } // Needed for tests

    public ExecutionPayloadV4(Block block) : base(block)
    {
        Deposits = block.Deposits;
    }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block!.Header.DepositsRoot = Deposits is null ? null : new DepositTrie(Deposits).RootHash;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip6110Enabled;

    /// </summary>
    [JsonRequired]
    public override Deposit[]? Deposits { get; set; }
}
