// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data;


/// <summary>
///  Represent an object mapping the <c>ExecutionPayloadV4</c> structure of the beacon chain spec.
/// </summary>
public class ExecutionPayloadV4 : ExecutionPayloadV3
{
    public ExecutionPayloadV4() { } // Needed for tests

    public ExecutionPayloadV4(Block block) : base(block)
    {
        InclusionListSummary = block.InclusionListSummary;
        InclusionListSummaryRoot = block.InclusionListSummaryRoot;
    }


    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
       if (!base.TryGetBlock(out block, totalDifficulty))
        {
            return false;
        }
        block!.Header.InclusionListSummaryRoot = InclusionListSummaryRoot;
        block!.Body.InclusionListSummary = InclusionListSummary;
        return true;
    }

    public override bool ValidateFork(ISpecProvider specProvider) =>
        specProvider.GetSpec(BlockNumber, Timestamp).IsEip7547Enabled;


    /// <summary>
    ///  Gets or sets <see cref="InclusionListSummary"/> as defined in
    ///  <see href="https://eips.ethereum.org/EIPS/eip-7547">EIP-7547</see>.
    /// </summary>
    [JsonRequired]
    public InclusionListSummary? InclusionListSummary { get; set; }

    /// <summary>
    /// Gets or sets <see cref="InclusionListSummaryRoot"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-7547">EIP-7547</see>.
    /// </summary>
    [JsonRequired]
    public Hash256? InclusionListSummaryRoot { get; set; }

}
