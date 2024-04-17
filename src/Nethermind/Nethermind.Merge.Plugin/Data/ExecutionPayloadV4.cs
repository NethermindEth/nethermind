// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State.Proofs;
using Nethermind.Blockchain.ValidatorExit;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3
{
    public ExecutionPayloadV4() { } // Needed for tests

    public ExecutionPayloadV4(Block block) : base(block)
    {
        Deposits = block.Deposits;
        ValidatorExits = block.ValidatorExits;
    }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }

        block!.Header.DepositsRoot = Deposits is null ? null : new DepositTrie(Deposits).RootHash;
        block!.Header.ValidatorExitsRoot = ValidatorExits is null ? null : ValidatorExitsTrie.CalculateRoot(ValidatorExits);
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).DepositsEnabled
        && specProvider.GetSpec(BlockNumber, Timestamp).ValidatorExitsEnabled;

    /// <summary>
    /// Gets or sets <see cref="Block.Deposits"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-6110">EIP-6110</see>.
    /// </summary>
    [JsonRequired]
    public override Deposit[]? Deposits { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Block.ValidatorExits"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7002">EIP-7002</see>.
    /// </summary>
    [JsonRequired]
    public override ValidatorExit[]? ValidatorExits { get; set; }
}
